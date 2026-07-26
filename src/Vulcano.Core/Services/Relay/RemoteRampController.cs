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
    private TemperatureRampPlan? _activePlan;

    /// <summary>Not meaningful client-side - pacing of pushed target-temperature writes is owned
    /// by the host's local <see cref="RampSessionController"/>. Kept only for interface parity.</summary>
    public int PushThresholdCelsius { get; set; } = 1;

    public bool IsRunning => _isRunning;

    /// <summary>Mirrored from the host's progress events - pausing is decided over there.</summary>
    public bool IsPaused => _isPaused;

    /// <summary>
    /// The plan the host is driving, as the host described it - including to a client that joined
    /// after the ramp had already started, which is why it arrives over the wire rather than being
    /// remembered from a local Start. Null when no ramp is running, or if the host announced a plan
    /// this build cannot make sense of.
    /// </summary>
    public TemperatureRampPlan? ActivePlan => _activePlan;

    public event EventHandler<RampProgressEventArgs>? ProgressChanged;
    public event EventHandler? WarmupCompleted;
    public event EventHandler<double>? Completed;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler? Stopped;

    public RemoteRampController(VolcanoRelayClient client)
    {
        _client = client;
        _client.RampProgressChanged += OnRampProgressChanged;
        _client.RampPlanChanged += OnRampPlanChanged;
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
        _activePlan = null;

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

    /// <summary>Rebuilt rather than trusted: the host's own validation is what a plan has to pass to
    /// run at all, so a payload that will not build is one this build should not pretend to know.</summary>
    private void OnRampPlanChanged(object? sender, RampPlanPayload payload) =>
        _activePlan = TemperatureRampPlan.TryCreate(payload.Points, payload.HoldDuration, out var plan, out _)
            ? plan
            : null;

    private void OnRampWarmupCompleted(object? sender, EventArgs e) =>
        WarmupCompleted?.Invoke(this, EventArgs.Empty);

    private void OnRampCompleted(object? sender, double resetTemperatureCelsius)
    {
        _isRunning = false;
        _isPaused = false;
        _activePlan = null;
        Completed?.Invoke(this, resetTemperatureCelsius);
    }

    private void OnRampErrorOccurred(object? sender, string message)
    {
        _isRunning = false;
        _isPaused = false;
        _activePlan = null;
        ErrorOccurred?.Invoke(this, message);
    }

    private void OnRampStopped(object? sender, EventArgs e)
    {
        _isRunning = false;
        _isPaused = false;
        _activePlan = null;
        Stopped?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _client.RampProgressChanged -= OnRampProgressChanged;
        _client.RampPlanChanged -= OnRampPlanChanged;
        _client.RampWarmupCompleted -= OnRampWarmupCompleted;
        _client.RampCompleted -= OnRampCompleted;
        _client.RampErrorOccurred -= OnRampErrorOccurred;
        _client.RampStopped -= OnRampStopped;
    }
}
