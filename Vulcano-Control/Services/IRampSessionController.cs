using Vulcano_Control.Models;

namespace Vulcano_Control.Services;

/// <summary>
/// Drives a temperature ramp against an <see cref="IVolcanoDevice"/>. Implemented directly by
/// <see cref="RampSessionController"/> (runs the actual state machine), and also by
/// <see cref="Relay.RemoteRampController"/> (forwards Start/Stop to a
/// <see cref="Relay.VolcanoRelayServer"/> over LAN and mirrors its progress events locally) and by
/// <see cref="VolcanoDeviceOrchestrator"/> (delegates to whichever of the two is currently active).
/// </summary>
public interface IRampSessionController : IDisposable
{
    /// <summary>Minimum temperature drift (°C) from the last pushed value that triggers an update.</summary>
    int PushThresholdCelsius { get; set; }

    bool IsRunning { get; }

    event EventHandler<RampProgressEventArgs>? ProgressChanged;
    event EventHandler? WarmupCompleted;
    event EventHandler<double>? Completed;
    event EventHandler<string>? ErrorOccurred;

    /// <summary>Raised when a running ramp is stopped manually (as opposed to finishing on its
    /// own, which raises <see cref="Completed"/> instead) - lets observers other than whoever
    /// called <see cref="Stop"/> (e.g. another LAN-relay participant) learn that it happened.</summary>
    event EventHandler? Stopped;

    Task StartAsync(
        double startTemperatureCelsius,
        double endTemperatureCelsius,
        TimeSpan duration,
        InterpolationMethod method,
        TimeSpan holdDuration,
        bool heaterCurrentlyOn);

    void Stop();
}
