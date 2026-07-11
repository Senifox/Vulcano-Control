using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vulcano_Control.Models;
using Vulcano_Control.Services;

namespace Vulcano_Control.ViewModels;

/// <summary>
/// Live Volcano device settings (info + control) - not persisted to AppSettings, always read
/// fresh from the device rather than cached, so they're only meaningful while connected.
/// </summary>
public partial class DeviceSettingsViewModel : ObservableObject
{
    private readonly IVolcanoDevice _service;

    /// <summary>Raised by the Schließen button - the window (unlike SettingsWindow) has no
    /// Save/Cancel round trip, since every control here writes straight to the device.</summary>
    public event EventHandler? CloseRequested;

    [ObservableProperty]
    private bool isDeviceConnected;

    [ObservableProperty]
    private string? deviceSerialNumber;

    [ObservableProperty]
    private string? deviceOperatingHours;

    [ObservableProperty]
    private string? deviceFirmwareVersion;

    [ObservableProperty]
    private string? deviceBleFirmwareVersion;

    [ObservableProperty]
    private int deviceBrightness;

    [ObservableProperty]
    private int deviceAutoOffMinutes = 5;

    [ObservableProperty]
    private bool deviceVibrationEnabled;

    [ObservableProperty]
    private bool deviceDisplayOnCoolingEnabled;

    [ObservableProperty]
    private bool deviceFahrenheitEnabled;

    public DeviceSettingsViewModel(IVolcanoDevice service)
    {
        _service = service;
    }

    /// <summary>Re-reads the live device settings. Called right before the window is shown.</summary>
    public void Refresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        IsDeviceConnected = _service.State == ConnectionState.Connected;

        if (!IsDeviceConnected)
        {
            DeviceSerialNumber = null;
            DeviceOperatingHours = null;
            DeviceFirmwareVersion = null;
            DeviceBleFirmwareVersion = null;
            DeviceBrightness = 0;
            DeviceAutoOffMinutes = 5;
            DeviceVibrationEnabled = false;
            DeviceDisplayOnCoolingEnabled = false;
            DeviceFahrenheitEnabled = false;
            return;
        }

        var info = await _service.ReadDeviceInfoAsync();
        if (info is not null)
        {
            DeviceSerialNumber = info.Value.SerialNumber;
            DeviceOperatingHours = $"{info.Value.HoursOfHeating}h {info.Value.MinutesOfHeating}m";
            DeviceFirmwareVersion = info.Value.FirmwareVersion;
            DeviceBleFirmwareVersion = info.Value.FirmwareBleVersion;
        }

        var brightness = await _service.ReadBrightnessAsync();
        if (brightness is not null) DeviceBrightness = brightness.Value;

        var autoOffMinutes = await _service.ReadAutoOffMinutesAsync();
        if (autoOffMinutes is not null) DeviceAutoOffMinutes = autoOffMinutes.Value;

        var vibration = await _service.ReadVibrationAsync();
        if (vibration is not null) DeviceVibrationEnabled = vibration.Value;

        var displayFlags = await _service.ReadDisplayFlagsAsync();
        if (displayFlags is not null)
        {
            DeviceDisplayOnCoolingEnabled = displayFlags.Value.DisplayOnCooling;
            DeviceFahrenheitEnabled = displayFlags.Value.Fahrenheit;
        }
    }

    [RelayCommand]
    private Task SetDeviceBrightnessAsync() => _service.SetBrightnessAsync(DeviceBrightness);

    [RelayCommand]
    private Task SetDeviceAutoOffAsync() => _service.SetAutoOffMinutesAsync(DeviceAutoOffMinutes);

    [RelayCommand]
    private async Task ToggleDeviceVibrationAsync()
    {
        DeviceVibrationEnabled = !DeviceVibrationEnabled;
        await _service.SetVibrationAsync(DeviceVibrationEnabled);
    }

    [RelayCommand]
    private async Task ToggleDeviceDisplayOnCoolingAsync()
    {
        DeviceDisplayOnCoolingEnabled = !DeviceDisplayOnCoolingEnabled;
        await _service.SetDisplayOnCoolingAsync(DeviceDisplayOnCoolingEnabled);
    }

    [RelayCommand]
    private async Task ToggleDeviceTemperatureUnitAsync()
    {
        DeviceFahrenheitEnabled = !DeviceFahrenheitEnabled;
        await _service.SetFahrenheitAsync(DeviceFahrenheitEnabled);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
