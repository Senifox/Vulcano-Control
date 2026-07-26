using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.App.ViewModels;

/// <summary>
/// The Device tab: what the device is, and the handful of its own settings it lets us change.
/// Every change is written straight through - there is no OK button, because there is nothing to
/// cancel: the device has already changed by the time you see it.
/// </summary>
public partial class DeviceViewModel : ObservableObject, IDisposable
{
    private readonly VolcanoDeviceOrchestrator _device;
    private readonly LogService _log;

    /// <summary>True while values are being loaded from the device, so writing them back would
    /// immediately echo them out again.</summary>
    private bool _loading;

    [ObservableProperty]
    private string _serialNumber = "";

    [ObservableProperty]
    private string _firmwareVersion = "";

    [ObservableProperty]
    private string _bleFirmwareVersion = "";

    [ObservableProperty]
    private string _hoursOfHeating = "";

    [ObservableProperty]
    private int _brightness = 70;

    [ObservableProperty]
    private int _autoOffMinutes = 40;

    [ObservableProperty]
    private bool _vibration;

    [ObservableProperty]
    private bool _showTemperatureWhileCooling;

    [ObservableProperty]
    private bool _isConnected;

    public DeviceViewModel(VolcanoDeviceOrchestrator device, LogService log)
    {
        _device = device;
        _log = log;

        _device.ConnectionStateChanged += OnConnectionStateChanged;
    }

    /// <summary>
    /// A relay client is talking to a device paired with someone else's machine, so the identity and
    /// firmware block belongs to the host, not to us.
    /// </summary>
    public bool IsRemote => _device.IsRemote;

    public string RemoteNote => IsRemote ? "host only" : "";

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (!IsConnected) return;

        _loading = true;
        try
        {
            if (await _device.ReadDeviceInfoAsync() is { } info)
            {
                SerialNumber = info.SerialNumber;
                FirmwareVersion = info.FirmwareVersion;
                BleFirmwareVersion = info.FirmwareBleVersion;
                HoursOfHeating = Formatting.WithUnit(info.HoursOfHeating.ToString(), "h");
            }

            if (await _device.ReadBrightnessAsync() is { } brightness) Brightness = brightness;
            if (await _device.ReadAutoOffMinutesAsync() is { } minutes) AutoOffMinutes = minutes;
            if (await _device.ReadVibrationAsync() is { } vibration) Vibration = vibration;
            if (await _device.ReadDisplayFlagsAsync() is { } flags)
            {
                ShowTemperatureWhileCooling = flags.DisplayOnCooling;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnBrightnessChanged(int value) => Write(() => _device.SetBrightnessAsync(value));

    partial void OnAutoOffMinutesChanged(int value) => Write(() => _device.SetAutoOffMinutesAsync(value));

    partial void OnVibrationChanged(bool value) => Write(() => _device.SetVibrationAsync(value));

    partial void OnShowTemperatureWhileCoolingChanged(bool value) =>
        Write(() => _device.SetDisplayOnCoolingAsync(value));

    /// <summary>
    /// Fire-and-forget write with the "we are only reflecting what we just read" guard. Failures are
    /// logged rather than thrown: a device setting that would not take is worth knowing about, but
    /// not worth taking the app down for.
    /// </summary>
    private void Write(Func<Task> write)
    {
        if (_loading || !IsConnected) return;

        _ = WriteAsync(write);
    }

    private async Task WriteAsync(Func<Task> write)
    {
        try
        {
            await write();
        }
        catch (Exception ex)
        {
            _log.Log($"Writing a device setting failed: {ex.Message}", LogLevel.Warning);
        }
    }

    private void OnConnectionStateChanged(object? sender, ConnectionState state) =>
        Dispatcher.UIThread.Post(async () =>
        {
            IsConnected = state == ConnectionState.Connected;
            OnPropertyChanged(nameof(IsRemote));
            OnPropertyChanged(nameof(RemoteNote));

            if (IsConnected)
            {
                await ReloadAsync();
            }
            else
            {
                SerialNumber = "";
                FirmwareVersion = "";
                BleFirmwareVersion = "";
                HoursOfHeating = "";
            }
        });

    public void Dispose() => _device.ConnectionStateChanged -= OnConnectionStateChanged;
}
