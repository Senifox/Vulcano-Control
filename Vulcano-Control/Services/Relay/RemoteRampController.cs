using Vulcano_Control.Models;

namespace Vulcano_Control.Services.Relay;

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

    /// <summary>Not meaningful client-side - pacing of pushed target-temperature writes is owned
    /// by the server's local <see cref="RampSessionController"/>. Kept only for interface parity.</summary>
    public int PushThresholdCelsius { get; set; } = 1;

    public bool IsRunning => _isRunning;

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

    public async Task StartAsync(
        double startTemperatureCelsius,
        double endTemperatureCelsius,
        TimeSpan duration,
        InterpolationMethod method,
        TimeSpan holdDuration,
        bool heaterCurrentlyOn)
    {
        var response = await _client.SendRequestAsync(
            RelayMethods.StartRamp,
            new StartRampArgs(startTemperatureCelsius, endTemperatureCelsius, duration, method, holdDuration, heaterCurrentlyOn));

        if (response.Error is not null)
        {
            ErrorOccurred?.Invoke(this, response.Error);
        }
    }

    public async void Stop()
    {
        // Update optimistically, matching RampSessionController.IsRunning's synchronous
        // transition to false - the caller (a ViewModel command) expects Stop() to take effect
        // immediately, not once a round trip to the server completes.
        _isRunning = false;

        try
        {
            var response = await _client.SendRequestAsync(RelayMethods.StopRamp, null);
            if (response.Error is not null)
            {
                ErrorOccurred?.Invoke(this, response.Error);
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Rampe konnte nicht gestoppt werden: {ex.Message}");
        }
    }

    private void OnRampProgressChanged(object? sender, RampProgressEventArgs progress)
    {
        _isRunning = true;
        ProgressChanged?.Invoke(this, progress);
    }

    private void OnRampWarmupCompleted(object? sender, EventArgs e) =>
        WarmupCompleted?.Invoke(this, EventArgs.Empty);

    private void OnRampCompleted(object? sender, double resetTemperatureCelsius)
    {
        _isRunning = false;
        Completed?.Invoke(this, resetTemperatureCelsius);
    }

    private void OnRampErrorOccurred(object? sender, string message)
    {
        _isRunning = false;
        ErrorOccurred?.Invoke(this, message);
    }

    private void OnRampStopped(object? sender, EventArgs e)
    {
        _isRunning = false;
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
