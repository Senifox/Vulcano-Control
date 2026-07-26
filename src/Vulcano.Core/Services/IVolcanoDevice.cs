using Vulcano.Core.Models;

namespace Vulcano.Core.Services;

/// <summary>One-time device information, read on demand (e.g. when opening the Device tab).</summary>
public readonly record struct VolcanoDeviceInfo(
    string SerialNumber,
    string FirmwareVersion,
    string FirmwareBleVersion,
    int HoursOfHeating,
    int MinutesOfHeating);

/// <summary>
/// The full set of Volcano device operations the rest of the app depends on - implemented by the
/// local BLE device (real Bluetooth), by <see cref="Relay.VolcanoRelayClient"/> (talks to a
/// <see cref="Relay.VolcanoRelayServer"/> over LAN instead) and by
/// <see cref="VolcanoDeviceOrchestrator"/> (delegates to whichever of the two is currently active,
/// so the rest of the app never needs to know which one it's talking to).
/// </summary>
public interface IVolcanoDevice : IAsyncDisposable
{
    ConnectionState State { get; }

    /// <summary>True when the device is reached through someone else's relay rather than this
    /// machine's own Bluetooth adapter. The UI derives everything remote from this one flag:
    /// the "Remote" title-bar chip, "Leave" instead of "Disconnect", and the disabled firmware
    /// block in the Device tab.</summary>
    bool IsRemote { get; }

    /// <summary>Name of the machine hosting the relay, or null when not remote.</summary>
    string? HostName { get; }

    event EventHandler<ConnectionState>? ConnectionStateChanged;
    event EventHandler<string>? ErrorOccurred;
    event EventHandler<double>? CurrentTemperatureChanged;
    event EventHandler<ushort>? ActivityChanged;
    event EventHandler<int>? RemainingAutoOffSecondsChanged;

    Task<bool> ScanAndConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();

    Task SetTargetTemperatureAsync(double celsius);

    /// <summary>
    /// The target the device currently holds, or null when it cannot be read. Worth asking on
    /// connect: the device keeps its own target between sessions, and showing a value the app made
    /// up instead is how you end up heating to something nobody chose.
    /// </summary>
    Task<double?> ReadTargetTemperatureAsync();
    Task SetHeaterAsync(bool on);
    Task SetPumpAsync(bool on);

    Task<VolcanoDeviceInfo?> ReadDeviceInfoAsync();
    Task<int?> ReadBrightnessAsync();
    Task SetBrightnessAsync(int level);
    Task<int?> ReadAutoOffMinutesAsync();
    Task SetAutoOffMinutesAsync(int minutes);
    Task<(bool Fahrenheit, bool DisplayOnCooling)?> ReadDisplayFlagsAsync();
    Task SetFahrenheitAsync(bool enabled);
    Task SetDisplayOnCoolingAsync(bool enabled);
    Task<bool?> ReadVibrationAsync();
    Task SetVibrationAsync(bool enabled);
}
