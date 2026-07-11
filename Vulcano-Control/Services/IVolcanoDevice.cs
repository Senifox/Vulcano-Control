using Vulcano_Control.Models;

namespace Vulcano_Control.Services;

/// <summary>One-time device information, read on demand (e.g. when opening the settings dialog).</summary>
public readonly record struct VolcanoDeviceInfo(
    string SerialNumber,
    string FirmwareVersion,
    string FirmwareBleVersion,
    int HoursOfHeating,
    int MinutesOfHeating);

/// <summary>
/// The full set of Volcano device operations the rest of the app depends on - implemented
/// directly by <see cref="VolcanoBluetoothService"/> (real BLE), and also by
/// <see cref="Relay.VolcanoRelayClient"/> (talks to a <see cref="Relay.VolcanoRelayServer"/> over
/// LAN instead) and by <see cref="VolcanoDeviceOrchestrator"/> (delegates to whichever of the two
/// is currently active, so the rest of the app never needs to know which one it's talking to).
/// </summary>
public interface IVolcanoDevice : IAsyncDisposable
{
    ConnectionState State { get; }

    event EventHandler<ConnectionState>? ConnectionStateChanged;
    event EventHandler<string>? ErrorOccurred;
    event EventHandler<double>? CurrentTemperatureChanged;
    event EventHandler<ushort>? ActivityChanged;
    event EventHandler<int>? RemainingAutoOffSecondsChanged;

    Task<bool> ScanAndConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();

    Task SetTargetTemperatureAsync(double celsius);
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
