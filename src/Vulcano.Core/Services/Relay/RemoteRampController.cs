namespace Vulcano.Core.Services.Relay;

/// <summary>
/// Client-side <see cref="IRampSessionController"/> that forwards Start/Stop over the same TCP
/// connection as its paired <see cref="VolcanoRelayClient"/> (one physical connection shared by
/// both interfaces) and mirrors the ramp events the server fans out from its own single shared
/// <see cref="RampSessionController"/> instance - which is what actually gives "first to start
/// wins, anyone can stop" across every connected participant.
/// </summary>
public sealed class RemoteRampController : IRampSessionController
{
    private readonly VolcanoRelayClient _client;
    private bool _isRunning;
    private bool _isPaused;

    /// <summary>Not meaningful client-side - pacing of pushed target-temperature writes is owned
    /// by the host's local <see cref="RampSessionController"/>. Kept only for interface parity.</summary>
    public int PushThresholdCelsius { get; set; } = 1;

    public bool IsRunning => _isRunning;

    /// <summary>Mirrored from the host's progress events - pausing is decided over there.</summary>
    public bool IsPaused => _isPaused;

    /// <summary>Always null: the plan lives on the host, and this client may well have joined
    /// after it started.</summary>
    public TemperatureRampPlan? ActivePlan => null;

    public event EventHandler<RampProgressEventArgs>? ProgressChanged;
    public event EventHandler? WarmupCompleted;
    public event EventHandler<double>? Completed;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler? Stopped;

    public RemoteRampController(VolcanoRelayClient client)
    {
        _client = client;
        _client.RampProgressChanged += OnRampProgressChanged;
        _client.RampWarmupCompleted += OnRampWarmupCompleted;
        _client.RampCompleted += OnRampCompleted;
        _client.RampErrorOccurred += OnRampErrorOccurred;
        _client.RampStopped += OnRampStopped;
    }

    public async Task StartAsync(TemperatureRampPlan plan, bool heaterCurrentlyOn)
    {
        var response = await _client.SendRequestAsync(
            RelayMethods.StartRamp,
            new StartRampArgs(plan.Points, plan.HoldDuration, heaterCurrentlyOn));

        if (response.Error is not null)
        {
            ErrorOccurred?.Invoke(this, response.Error);
        }
    }

    public async void Stop()
    {
        // Update optimistically, matching RampSessionController.IsRunning's synchronous
        // transition to false - the caller (a view model command) expects Stop() to take effect
        // immediately, not once a round trip to the host completes.
        _isRunning = false;
        _isPaused = false;

        await SendAsync(RelayMethods.StopRamp, "Error.CannotStopRamp");
    }

    public async void Pause() => await SendAsync(RelayMethods.PauseRamp, "Error.CannotPauseRamp");

    public async void Resume() => await SendAsync(RelayMethods.ResumeRamp, "Error.CannotResumeRamp");

    public async void SkipSegment() => await SendAsync(RelayMethods.SkipRampSegment, "Error.CannotSkipSegment");

    /// <summary>Fire-and-forget forwarding for the void control methods. The resulting state change
    /// arrives as a progress event from the host, so there is nothing to await here beyond
    /// surfacing a refusal.</summary>
    private async Task SendAsync(string method, string failureKey)
    {
        try
        {
            var response = await _client.SendRequestAsync(method, null);
            if (response.Error is not null)
            {
                ErrorOccurred?.Invoke(this, response.Error);
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"{Strings.Get(failureKey)}: {ex.Message}");
        }
    }

    private void OnRampProgressChanged(object? sender, RampProgressEventArgs progress)
    {
        _isRunning = true;
        _isPaused = progress.IsPaused;
        ProgressChanged?.Invoke(this, progress);
    }

    private void OnRampWarmupCompleted(object? sender, EventArgs e) =>
        WarmupCompleted?.Invoke(this, EventArgs.Empty);

    private void OnRampCompleted(object? sender, double resetTemperatureCelsius)
    {
        _isRunning = false;
        _isPaused = false;
        Completed?.Invoke(this, resetTemperatureCelsius);
    }

    private void OnRampErrorOccurred(object? sender, string message)
    {
        _isRunning = false;
        _isPaused = false;
        ErrorOccurred?.Invoke(this, message);
    }

    private void OnRampStopped(object? sender, EventArgs e)
    {
        _isRunning = false;
        _isPaused = false;
        Stopped?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _client.RampProgressChanged -= OnRampProgressChanged;
        _client.RampWarmupCompleted -= OnRampWarmupCompleted;
        _client.RampCompleted -= OnRampCompleted;
        _client.RampErrorOccurred -= OnRampErrorOccurred;
        _client.RampStopped -= OnRampStopped;
    }
}
