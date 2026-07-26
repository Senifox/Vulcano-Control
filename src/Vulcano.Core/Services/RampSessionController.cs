using Vulcano.Core.Models;

namespace Vulcano.Core.Services;

public readonly record struct RampProgressEventArgs(
    TimeSpan Elapsed,
    TimeSpan Remaining,
    double CurrentComputedTarget,
    double FractionComplete,
    bool IsWarmingUp,
    bool IsHolding = false,
    bool IsPaused = false,
    int SegmentIndex = 0,
    int SegmentCount = 0);

/// <summary>
/// Drives a <see cref="TemperatureRampPlan"/> over time, pushing target-temperature
/// writes to an <see cref="IVolcanoDevice"/> only once the ideal target has
/// drifted far enough from the last pushed value.
///
/// Before the timed ramp itself begins, the controller waits in a "warm-up" phase until
/// the device's actual measured temperature has reached the first point's temperature -
/// the ramp duration only starts counting down once the device is actually there. Once the
/// ramp curve finishes, the target temperature is reset to <see cref="ResetTemperatureCelsius"/>
/// (so the device is left on its default start value for the next session, whether or not this
/// app controls it) and the heater is switched off right away - no waiting for actual cooldown.
///
/// Losing the connection mid-ramp pauses rather than aborts: the clock freezes, the heater is
/// left alone, and reconnecting picks up exactly where it stopped.
///
/// The clock is a <see cref="PeriodicTimer"/> on a background loop rather than a UI-thread timer,
/// which is what keeps this class - and the whole ramp state machine - free of any UI framework.
/// Ticks never overlap: the loop awaits each one before waiting for the next.
/// </summary>
public sealed class RampSessionController : IRampSessionController
{
    private static readonly TimeSpan DefaultTickInterval = TimeSpan.FromSeconds(1);
    private const double ResetTemperatureCelsius = 185.0;
    private const double TemperatureToleranceCelsius = 0.5;

    /// <summary>Minimum temperature drift (°C) from the last pushed value that triggers an update.</summary>
    public int PushThresholdCelsius { get; set; } = 1;

    private enum Phase { Idle, WarmingUp, Ramping, Holding }

    private readonly IVolcanoDevice _device;
    private readonly LogService _logService;
    private readonly TimeSpan _tickInterval;

    private CancellationTokenSource? _cts;
    private TemperatureRampPlan? _plan;
    private Phase _phase = Phase.Idle;
    private DateTime _startedAtUtc;
    private DateTime? _pausedAtUtc;
    private bool _pausedByConnectionLoss;
    private bool _skipWarmup;
    private double _lastPushedTemperature;
    private double _lastKnownCurrentTemperature = double.NaN;
    private bool _lastKnownHeaterOn;
    private DateTime _holdStartedAtUtc;

    public bool IsRunning => _phase != Phase.Idle;

    public bool IsPaused => _pausedAtUtc is not null;

    public TemperatureRampPlan? ActivePlan => IsRunning ? _plan : null;

    public event EventHandler<RampProgressEventArgs>? ProgressChanged;
    public event EventHandler? WarmupCompleted;
    public event EventHandler<double>? Completed;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler? Stopped;

    /// <param name="tickInterval">How often the state machine advances. Only tests pass this -
    /// they would otherwise spend real seconds waiting for a ramp to move.</param>
    public RampSessionController(IVolcanoDevice device, LogService logService, TimeSpan? tickInterval = null)
    {
        _device = device;
        _logService = logService;
        _tickInterval = tickInterval ?? DefaultTickInterval;
        _device.CurrentTemperatureChanged += OnCurrentTemperatureChanged;
        _device.ActivityChanged += OnActivityChanged;
        _device.ConnectionStateChanged += OnConnectionStateChanged;
    }

    public async Task StartAsync(TemperatureRampPlan plan, bool heaterCurrentlyOn)
    {
        if (IsRunning) return;

        _plan = plan;
        _phase = Phase.WarmingUp;
        _pausedAtUtc = null;
        _pausedByConnectionLoss = false;
        _skipWarmup = false;
        _lastPushedTemperature = double.NaN;

        _logService.Log(
            $"Ramp started: {plan.Points.Count} points / {plan.Duration.TotalMinutes:0} min, " +
            $"{plan.StartTemperatureCelsius:0} °C to {plan.EndTemperatureCelsius:0} °C" +
            (plan.HoldDuration > TimeSpan.Zero ? $", hold {plan.HoldDuration.TotalMinutes:0} min" : ""));

        if (!heaterCurrentlyOn)
        {
            await _device.SetHeaterAsync(true);
        }
        _lastKnownHeaterOn = true;

        await _device.SetTargetTemperatureAsync(plan.StartTemperatureCelsius);
        _lastPushedTemperature = plan.StartTemperatureCelsius;

        RaiseWarmupProgress();

        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void Stop()
    {
        if (_phase == Phase.Idle) return;

        _phase = Phase.Idle;
        _pausedAtUtc = null;
        _pausedByConnectionLoss = false;
        _cts?.Cancel();
        _logService.Log("Ramp stopped manually");
        Stopped?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        if (!IsRunning || IsPaused) return;

        _pausedAtUtc = DateTime.UtcNow;
        _pausedByConnectionLoss = false;
        _logService.Log("Ramp paused");
        RaiseCurrentProgress();
    }

    public void Resume()
    {
        if (!IsRunning || _pausedAtUtc is not { } pausedAt) return;

        // Shift both clocks forward by however long the pause lasted, so elapsed time continues
        // from where it stopped instead of jumping.
        var pausedFor = DateTime.UtcNow - pausedAt;
        _startedAtUtc += pausedFor;
        _holdStartedAtUtc += pausedFor;
        _pausedAtUtc = null;
        _pausedByConnectionLoss = false;

        _logService.Log("Ramp resumed");
        RaiseCurrentProgress();
    }

    public void SkipSegment()
    {
        if (!IsRunning || _plan is null) return;

        switch (_phase)
        {
            case Phase.WarmingUp:
                // Handled on the next tick, which does the phase transition and the logging.
                _skipWarmup = true;
                break;

            case Phase.Ramping:
            {
                var reference = _pausedAtUtc ?? DateTime.UtcNow;
                var elapsed = reference - _startedAtUtc;
                var segment = _plan.GetSegmentIndex(elapsed);
                var segmentEnd = _plan.GetSegmentEnd(segment);

                _startedAtUtc = reference - segmentEnd;
                _logService.Log($"Segment {segment + 1} of {_plan.SegmentCount} skipped");
                break;
            }

            case Phase.Holding:
                // Make the hold look already elapsed; the next tick finishes the ramp.
                _holdStartedAtUtc = (_pausedAtUtc ?? DateTime.UtcNow) - _plan.HoldDuration;
                _logService.Log("Hold skipped");
                break;
        }
    }

    private void OnCurrentTemperatureChanged(object? sender, double celsius) =>
        _lastKnownCurrentTemperature = celsius;

    private void OnActivityChanged(object? sender, ushort activity) =>
        _lastKnownHeaterOn = (activity & VolcanoUuids.ActivityFlags.HeatingEnabled) != 0;

    private void OnConnectionStateChanged(object? sender, ConnectionState state)
    {
        if (!IsRunning) return;

        if (state != ConnectionState.Connected)
        {
            if (IsPaused) return;

            _pausedAtUtc = DateTime.UtcNow;
            _pausedByConnectionLoss = true;
            _logService.Log(
                $"Connection lost during a running ramp - paused at {_lastKnownCurrentTemperature:0} °C",
                LogLevel.Warning);
            RaiseCurrentProgress();
            return;
        }

        if (_pausedByConnectionLoss)
        {
            _logService.Log("Connection restored - continuing the ramp");
            Resume();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_tickInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!await TickAsync())
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stop() or Dispose() - nothing to report.
        }
        catch (Exception ex)
        {
            _phase = Phase.Idle;
            var message = $"Ramp aborted: {ex.Message}";
            _logService.Log(message, LogLevel.Error);
            ErrorOccurred?.Invoke(this, message);
        }
    }

    /// <summary>Runs one tick of the state machine. Returns false once the ramp is done and the
    /// loop should end.</summary>
    private async Task<bool> TickAsync()
    {
        if (_phase == Phase.Idle || _plan is null) return false;

        if (IsPaused)
        {
            // Keep the UI updating - the numbers stand still, which is exactly the point.
            RaiseCurrentProgress();
            return true;
        }

        if (!_lastKnownHeaterOn)
        {
            // The device can auto-shutoff on its own (see the auto-off timer) or otherwise
            // turn the heater off mid-ramp - re-enable it so the ramp keeps running rather
            // than silently stalling at a lower temperature.
            _logService.Log(
                "Heater switched off unexpectedly during a running ramp (e.g. auto shut-off) - switching it back on",
                LogLevel.Warning);
            await _device.SetHeaterAsync(true);
            _lastKnownHeaterOn = true; // optimistic, avoids resending every tick until the notify confirms it
        }

        if (_phase == Phase.WarmingUp)
        {
            if (!_skipWarmup && !HasReachedRising(_plan.StartTemperatureCelsius))
            {
                RaiseWarmupProgress();
                return true;
            }

            _phase = Phase.Ramping;
            _startedAtUtc = DateTime.UtcNow;
            _lastPushedTemperature = _plan.StartTemperatureCelsius;
            _logService.Log(_skipWarmup
                ? "Warm-up skipped, ramp is running"
                : "Start temperature reached, ramp is running");
            _skipWarmup = false;
            WarmupCompleted?.Invoke(this, EventArgs.Empty);
        }

        if (_phase == Phase.Holding)
        {
            var holdElapsed = DateTime.UtcNow - _holdStartedAtUtc;
            if (holdElapsed >= _plan.HoldDuration)
            {
                await FinishAsync();
                return false;
            }

            RaiseHoldProgress(holdElapsed);
            return true;
        }

        var elapsed = DateTime.UtcNow - _startedAtUtc;

        if (_plan.IsComplete(elapsed))
        {
            if (_plan.HoldDuration <= TimeSpan.Zero)
            {
                // No hold configured - go straight to the reset write rather than also
                // pushing the curve's end temperature this tick, since a second write to
                // the same characteristic immediately afterwards can collide with/override
                // the reset write.
                await FinishAsync();
                return false;
            }

            // Hold the end temperature for a while before shutting down - push it once
            // explicitly (the ramp loop above stops pushing once complete) and switch to
            // the Holding phase; FinishAsync() only runs once the hold time elapses.
            await _device.SetTargetTemperatureAsync(_plan.EndTemperatureCelsius);
            _phase = Phase.Holding;
            _holdStartedAtUtc = DateTime.UtcNow;
            _logService.Log(
                $"Ramp curve finished, holding {_plan.EndTemperatureCelsius:0} °C " +
                $"for {_plan.HoldDuration.TotalMinutes:0} min");
            RaiseHoldProgress(TimeSpan.Zero);
            return true;
        }

        await PushIfDueAsync(elapsed);
        RaiseProgress(elapsed);
        return true;
    }

    private bool HasReachedRising(double targetCelsius) =>
        !double.IsNaN(_lastKnownCurrentTemperature) &&
        _lastKnownCurrentTemperature >= targetCelsius - TemperatureToleranceCelsius;

    private async Task PushIfDueAsync(TimeSpan elapsed)
    {
        if (_plan is null) return;

        var idealTarget = _plan.GetTargetTemperature(elapsed);

        var delta = double.IsNaN(_lastPushedTemperature)
            ? double.MaxValue
            : Math.Abs(idealTarget - _lastPushedTemperature);

        if (delta < PushThresholdCelsius) return;

        await _device.SetTargetTemperatureAsync(idealTarget);
        _lastPushedTemperature = idealTarget;
        _logService.Log($"Ramp target updated: {idealTarget:0} °C", LogLevel.Debug);
    }

    private async Task FinishAsync()
    {
        _phase = Phase.Idle;
        _pausedAtUtc = null;

        await _device.SetTargetTemperatureAsync(ResetTemperatureCelsius);
        await _device.SetHeaterAsync(false);

        _logService.Log($"Ramp complete: target reset to {ResetTemperatureCelsius:0} °C, heater switched off");

        Completed?.Invoke(this, ResetTemperatureCelsius);
    }

    /// <summary>Re-raises progress for whatever phase is current - used by pause/resume, where the
    /// state changed but the clock did not.</summary>
    private void RaiseCurrentProgress()
    {
        if (_plan is null) return;

        var reference = _pausedAtUtc ?? DateTime.UtcNow;

        switch (_phase)
        {
            case Phase.WarmingUp:
                RaiseWarmupProgress();
                break;
            case Phase.Holding:
                RaiseHoldProgress(reference - _holdStartedAtUtc);
                break;
            case Phase.Ramping:
                RaiseProgress(reference - _startedAtUtc);
                break;
        }
    }

    private void RaiseWarmupProgress()
    {
        if (_plan is null) return;

        ProgressChanged?.Invoke(this, new RampProgressEventArgs(
            Elapsed: TimeSpan.Zero,
            Remaining: _plan.Duration,
            CurrentComputedTarget: _plan.StartTemperatureCelsius,
            FractionComplete: 0.0,
            IsWarmingUp: true,
            IsPaused: IsPaused,
            SegmentIndex: 0,
            SegmentCount: _plan.SegmentCount));
    }

    private void RaiseProgress(TimeSpan elapsed)
    {
        if (_plan is null) return;

        var remaining = _plan.Duration - elapsed;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

        ProgressChanged?.Invoke(this, new RampProgressEventArgs(
            Elapsed: elapsed,
            Remaining: remaining,
            CurrentComputedTarget: _plan.GetTargetTemperature(elapsed),
            FractionComplete: Math.Clamp(elapsed.TotalSeconds / _plan.Duration.TotalSeconds, 0.0, 1.0),
            IsWarmingUp: false,
            IsPaused: IsPaused,
            SegmentIndex: _plan.GetSegmentIndex(elapsed),
            SegmentCount: _plan.SegmentCount));
    }

    private void RaiseHoldProgress(TimeSpan holdElapsed)
    {
        if (_plan is null) return;

        var remaining = _plan.HoldDuration - holdElapsed;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

        ProgressChanged?.Invoke(this, new RampProgressEventArgs(
            Elapsed: holdElapsed,
            Remaining: remaining,
            CurrentComputedTarget: _plan.EndTemperatureCelsius,
            FractionComplete: 1.0,
            IsWarmingUp: false,
            IsHolding: true,
            IsPaused: IsPaused,
            SegmentIndex: Math.Max(_plan.SegmentCount - 1, 0),
            SegmentCount: _plan.SegmentCount));
    }

    public void Dispose()
    {
        _phase = Phase.Idle;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _device.CurrentTemperatureChanged -= OnCurrentTemperatureChanged;
        _device.ActivityChanged -= OnActivityChanged;
        _device.ConnectionStateChanged -= OnConnectionStateChanged;
    }
}
