namespace Vulcano.Core.Services;

/// <summary>
/// Drives a temperature ramp against an <see cref="IVolcanoDevice"/>. Implemented directly by
/// <see cref="RampSessionController"/> (runs the actual state machine), and also by
/// <see cref="Relay.RemoteRampController"/> (forwards control to a
/// <see cref="Relay.VolcanoRelayServer"/> over LAN and mirrors its progress events locally) and by
/// <see cref="VolcanoDeviceOrchestrator"/> (delegates to whichever of the two is currently active).
///
/// Every event is raised from a background thread - view models must marshal onto the UI thread
/// themselves.
/// </summary>
public interface IRampSessionController : IDisposable
{
    /// <summary>Minimum temperature drift (°C) from the last pushed value that triggers an update.</summary>
    int PushThresholdCelsius { get; set; }

    bool IsRunning { get; }

    /// <summary>True while the ramp is held in place - either because the user paused it, or
    /// because the connection dropped mid-ramp.</summary>
    bool IsPaused { get; }

    event EventHandler<RampProgressEventArgs>? ProgressChanged;
    event EventHandler? WarmupCompleted;
    event EventHandler<double>? Completed;
    event EventHandler<string>? ErrorOccurred;

    /// <summary>Raised when a running ramp is stopped manually (as opposed to finishing on its
    /// own, which raises <see cref="Completed"/> instead) - lets observers other than whoever
    /// called <see cref="Stop"/> (e.g. another LAN-relay participant) learn that it happened.</summary>
    event EventHandler? Stopped;

    Task StartAsync(TemperatureRampPlan plan, bool heaterCurrentlyOn);

    void Stop();

    /// <summary>Freezes the clock and stops pushing targets. The heater stays as it is - a paused
    /// ramp is meant to be resumable, not a shutdown.</summary>
    void Pause();

    /// <summary>Continues a paused ramp from exactly where it stopped.</summary>
    void Resume();

    /// <summary>Jumps to the end of the segment currently running. From the warm-up this starts
    /// the ramp immediately; from the last segment it moves on to the hold.</summary>
    void SkipSegment();
}
