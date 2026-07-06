using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vulcano_Control.Models;
using Vulcano_Control.Services;

namespace Vulcano_Control.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly VolcanoBluetoothService _service;

    public event EventHandler<AppSettings>? SettingsSaved;
    public event EventHandler? Cancelled;

    [ObservableProperty]
    private int historyRetentionMinutes;

    [ObservableProperty]
    private int rampPushThresholdCelsius;

    [ObservableProperty]
    private int rampMaxPushIntervalSeconds;

    [ObservableProperty]
    private bool soundEnabled;

    [ObservableProperty]
    private string? errorMessage;

    // Live Volcano device settings - not persisted to AppSettings, always read fresh from the
    // device rather than cached, so they're only meaningful while connected.
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

    public SettingsViewModel(SettingsService settingsService, VolcanoBluetoothService service)
    {
        _settingsService = settingsService;
        _service = service;
        LoadFromDisk();
    }

    public void LoadFromDisk()
    {
        var settings = _settingsService.Load();
        HistoryRetentionMinutes = settings.HistoryRetentionMinutes;
        RampPushThresholdCelsius = settings.RampPushThresholdCelsius;
        RampMaxPushIntervalSeconds = settings.RampMaxPushIntervalSeconds;
        SoundEnabled = settings.SoundEnabled;
        ErrorMessage = null;

        _ = RefreshDeviceSectionAsync();
    }

    /// <summary>
    /// Re-reads the live Volcano device settings section. Fire-and-forget from LoadFromDisk() so
    /// opening the dialog isn't blocked on BLE round-trips; not connected simply clears the
    /// fields and leaves the section disabled via IsDeviceConnected.
    /// </summary>
    private async Task RefreshDeviceSectionAsync()
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
    private void Save()
    {
        if (HistoryRetentionMinutes <= 0)
        {
            ErrorMessage = "Verlaufsgröße muss größer als 0 sein.";
            return;
        }
        if (RampPushThresholdCelsius <= 0)
        {
            ErrorMessage = "Update-Schwelle muss größer als 0 sein.";
            return;
        }
        if (RampMaxPushIntervalSeconds <= 0)
        {
            ErrorMessage = "Max. Update-Intervall muss größer als 0 sein.";
            return;
        }

        // Load fresh from disk first so an in-between change to another setting (e.g. Theme,
        // saved independently via the View menu) isn't clobbered by this save.
        var settings = _settingsService.Load();
        settings.HistoryRetentionMinutes = HistoryRetentionMinutes;
        settings.RampPushThresholdCelsius = RampPushThresholdCelsius;
        settings.RampMaxPushIntervalSeconds = RampMaxPushIntervalSeconds;
        settings.SoundEnabled = SoundEnabled;
        _settingsService.Save(settings);

        ErrorMessage = null;
        SettingsSaved?.Invoke(this, settings);
    }

    [RelayCommand]
    private void Cancel()
    {
        LoadFromDisk();
        Cancelled?.Invoke(this, EventArgs.Empty);
    }
}
