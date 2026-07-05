using System.Windows.Threading;
using Vulcano_Control.Models;

namespace Vulcano_Control.Services;

public readonly record struct RampProgressEventArgs(
    TimeSpan Elapsed,
    TimeSpan Remaining,
    double CurrentComputedTarget,
    double FractionComplete,
    bool IsWarmingUp);

/// <summary>
/// Drives a <see cref="TemperatureRampPlan"/> over time, pushing target-temperature
/// writes to a <see cref="VolcanoBluetoothService"/> at a hybrid cadence: whenever the
/// ideal target has drifted far enough from the last pushed value, or whenever too much
/// time has passed since the last push - whichever comes first.
///
/// Before the timed ramp itself begins, the controller waits in a "warm-up" phase until
/// the device's actual measured temperature has reached the configured start temperature -
/// the ramp duration only starts counting down once the device is actually there. Once the
/// ramp curve finishes, the target temperature is reset to <see cref="ResetTemperatureCelsius"/>
/// (so the device is left on its default start value for the next session, whether or not this
/// app controls it) and the heater is switched off right away - no waiting for actual cooldown.
/// </summary>
public sealed class RampSessionController : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private const double ResetTemperatureCelsius = 185.0;
    private const double TemperatureToleranceCelsius = 0.5;

    /// <summary>Minimum temperature drift (°C) from the last pushed value that triggers an update.</summary>
    public int PushThresholdCelsius { get; set; } = 1;

    /// <summary>Upper bound on how long to wait between updates even if the threshold isn't reached.</summary>
    public TimeSpan MaxPushInterval { get; set; } = TimeSpan.FromSeconds(30);

    private enum Phase { Idle, WarmingUp, Ramping }

    private readonly VolcanoBluetoothService _service;
    private readonly LogService _logService;
    private readonly DispatcherTimer _timer;

    private TemperatureRampPlan? _plan;
    private Phase _phase = Phase.Idle;
    private DateTime _startedAtUtc;
    private DateTime _lastPushAtUtc;
    private double _lastPushedTemperature;
    private double _lastKnownCurrentTemperature = double.NaN;

    public bool IsRunning => _phase != Phase.Idle;

    public event EventHandler<RampProgressEventArgs>? ProgressChanged;
    public event EventHandler? WarmupCompleted;
    public event EventHandler<double>? Completed;
    public event EventHandler<string>? ErrorOccurred;

    public RampSessionController(VolcanoBluetoothService service, LogService logService)
    {
        _service = service;
        _logService = logService;
        _service.CurrentTemperatureChanged += OnCurrentTemperatureChanged;

        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += OnTick;
    }

    public async Task StartAsync(
        double startTemperatureCelsius,
        double endTemperatureCelsius,
        TimeSpan duration,
        InterpolationMethod method,
        bool heaterCurrentlyOn)
    {
        if (IsRunning) return;

        _plan = new TemperatureRampPlan(startTemperatureCelsius, endTemperatureCelsius, duration, method);
        _phase = Phase.WarmingUp;
        _lastPushedTemperature = double.NaN;

        _logService.Log(
            $"Rampe gestartet: {startTemperatureCelsius:0}°C → {endTemperatureCelsius:0}°C " +
            $"über {duration.TotalMinutes:0} min, Verlauf {method}.");

        if (!heaterCurrentlyOn)
        {
            await _service.SetHeaterAsync(true);
        }

        await _service.SetTargetTemperatureAsync(startTemperatureCelsius);
        _lastPushedTemperature = startTemperatureCelsius;
        _lastPushAtUtc = DateTime.UtcNow;

        RaiseWarmupProgress();
        _timer.Start();
    }

    public void Stop()
    {
        if (_phase == Phase.Idle) return;
        _timer.Stop();
        _phase = Phase.Idle;
        _logService.Log("Rampe manuell gestoppt.");
    }

    private void OnCurrentTemperatureChanged(object? sender, double celsius) =>
        _lastKnownCurrentTemperature = celsius;

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_phase == Phase.Idle || _plan is null) return;

        try
        {
            if (_phase == Phase.WarmingUp)
            {
                if (!HasReachedRising(_plan.StartTemperatureCelsius))
                {
                    RaiseWarmupProgress();
                    return;
                }

                _phase = Phase.Ramping;
                _startedAtUtc = DateTime.UtcNow;
                _lastPushAtUtc = _startedAtUtc;
                _lastPushedTemperature = _plan.StartTemperatureCelsius;
                _logService.Log("Start-Temperatur erreicht, Rampe läuft.");
                WarmupCompleted?.Invoke(this, EventArgs.Empty);
            }

            var elapsed = DateTime.UtcNow - _startedAtUtc;

            if (_plan.IsComplete(elapsed))
            {
                // Go straight to the reset write - do not also push the curve's end
                // temperature this tick, since a second write to the same characteristic
                // immediately afterwards can collide with/override the reset write.
                await FinishAsync();
                return;
            }

            await PushIfDueAsync(elapsed);
            RaiseProgress(elapsed);
        }
        catch (Exception ex)
        {
            _timer.Stop();
            _phase = Phase.Idle;
            var message = $"Rampe abgebrochen: {ex.Message}";
            _logService.Log(message);
            ErrorOccurred?.Invoke(this, message);
        }
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
        var timeSinceLastPush = DateTime.UtcNow - _lastPushAtUtc;

        var due = delta >= PushThresholdCelsius || timeSinceLastPush >= MaxPushInterval;
        if (!due) return;

        await _service.SetTargetTemperatureAsync(idealTarget);
        _lastPushedTemperature = idealTarget;
        _lastPushAtUtc = DateTime.UtcNow;
        _logService.Log($"Rampen-Ziel aktualisiert: {idealTarget:0}°C.");
    }

    private async Task FinishAsync()
    {
        _timer.Stop();
        _phase = Phase.Idle;

        await _service.SetTargetTemperatureAsync(ResetTemperatureCelsius);
        await _service.SetHeaterAsync(false);

        _logService.Log($"Rampe abgeschlossen: Ziel auf {ResetTemperatureCelsius:0}°C zurückgesetzt, Heizung ausgeschaltet.");

        Completed?.Invoke(this, ResetTemperatureCelsius);
    }

    private void RaiseWarmupProgress()
    {
        if (_plan is null) return;

        ProgressChanged?.Invoke(this, new RampProgressEventArgs(
            Elapsed: TimeSpan.Zero,
            Remaining: _plan.Duration,
            CurrentComputedTarget: _plan.StartTemperatureCelsius,
            FractionComplete: 0.0,
            IsWarmingUp: true));
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
            IsWarmingUp: false));
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        _service.CurrentTemperatureChanged -= OnCurrentTemperatureChanged;
    }
}
