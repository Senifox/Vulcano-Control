using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Vulcano_Control.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;

namespace Vulcano_Control.Services;

/// <summary>One-time device information, read on demand (e.g. when opening the settings dialog).</summary>
public readonly record struct VolcanoDeviceInfo(
    string SerialNumber,
    string FirmwareVersion,
    string FirmwareBleVersion,
    int HoursOfHeating,
    int MinutesOfHeating);

/// <summary>
/// Encapsulates BLE discovery, GATT connection and read/write/notify access to a
/// Storz &amp; Bickel Volcano. UI-agnostic: raises plain events, no Dispatcher usage here.
/// </summary>
public sealed class VolcanoBluetoothService : IAsyncDisposable
{
    private const int ScanTimeoutSeconds = 15;

    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEDevice? _device;
    private GattDeviceService? _stateService;
    private GattDeviceService? _controlService;

    private GattCharacteristic? _currentTemperatureChar;
    private GattCharacteristic? _targetTemperatureChar;
    private GattCharacteristic? _activityChar;
    private GattCharacteristic? _heaterOnChar;
    private GattCharacteristic? _heaterOffChar;
    private GattCharacteristic? _pumpOnChar;
    private GattCharacteristic? _pumpOffChar;

    // Optional secondary characteristics (device info + device settings) - resolved best-effort,
    // a missing one just leaves the corresponding setting unavailable rather than aborting the
    // whole connection.
    private GattCharacteristic? _brightnessChar;
    private GattCharacteristic? _currentAutoOffValueChar;
    private GattCharacteristic? _shutoffTimeChar;
    private GattCharacteristic? _hoursOfHeatingChar;
    private GattCharacteristic? _minutesOfHeatingChar;
    private GattCharacteristic? _firmwareVersionChar;
    private GattCharacteristic? _firmwareBleVersionChar;
    private GattCharacteristic? _serialNumberChar;
    private GattCharacteristic? _displayChar;
    private GattCharacteristic? _vibrationChar;

    private readonly LogService _logService;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public event EventHandler<ConnectionState>? ConnectionStateChanged;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<double>? CurrentTemperatureChanged;
    public event EventHandler<ushort>? ActivityChanged;

    public VolcanoBluetoothService(LogService logService)
    {
        _logService = logService;
    }

    public async Task<bool> ScanAndConnectAsync(CancellationToken ct = default)
    {
        SetState(ConnectionState.Scanning);

        ulong? address;
        try
        {
            address = await FindDeviceAddressAsync(ct);
        }
        catch (OperationCanceledException)
        {
            SetState(ConnectionState.Disconnected);
            return false;
        }

        if (address is null)
        {
            RaiseError("Volcano nicht gefunden – prüfen, ob es eingeschaltet und nicht mit einem anderen Client verbunden ist.");
            SetState(ConnectionState.Error);
            return false;
        }

        SetState(ConnectionState.Connecting);
        try
        {
            var connected = await ConnectToDeviceAsync(address.Value);
            if (!connected)
            {
                SetState(ConnectionState.Error);
            }
            return connected;
        }
        catch (Exception ex)
        {
            RaiseError($"Verbindung fehlgeschlagen: {ex.Message}");
            TearDown();
            SetState(ConnectionState.Error);
            return false;
        }
    }

    public Task DisconnectAsync()
    {
        TearDown();
        SetState(ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public Task SetTargetTemperatureAsync(double celsius) =>
        WriteUInt16Async(_targetTemperatureChar, BleEncoding.EncodeTemperature(celsius), "Zieltemperatur setzen");

    public Task SetHeaterAsync(bool on) =>
        WriteTriggerAsync(on ? _heaterOnChar : _heaterOffChar, on ? "Heizung einschalten" : "Heizung ausschalten");

    public Task SetPumpAsync(bool on) =>
        WriteTriggerAsync(on ? _pumpOnChar : _pumpOffChar, on ? "Pumpe einschalten" : "Pumpe ausschalten");

    /// <summary>Reads the one-time device info block (serial number, firmware versions, operating time).</summary>
    public async Task<VolcanoDeviceInfo?> ReadDeviceInfoAsync()
    {
        var serialNumber = await ReadUtf8Async(_serialNumberChar);
        var firmwareVersion = await ReadUtf8Async(_firmwareVersionChar);
        var firmwareBleVersion = await ReadUtf8Async(_firmwareBleVersionChar);
        var hours = await ReadUInt16Async(_hoursOfHeatingChar);
        var minutes = await ReadUInt16Async(_minutesOfHeatingChar);

        if (serialNumber is null || firmwareVersion is null || firmwareBleVersion is null ||
            hours is null || minutes is null)
        {
            return null;
        }

        return new VolcanoDeviceInfo(serialNumber, firmwareVersion, firmwareBleVersion, hours.Value, minutes.Value);
    }

    public async Task<int?> ReadBrightnessAsync() => await ReadUInt16Async(_brightnessChar);

    public Task SetBrightnessAsync(int level) =>
        WriteUInt16RawAsync(_brightnessChar, (ushort)level, "Helligkeit setzen");

    /// <summary>
    /// Reads the currently configured auto-shutoff duration, in minutes. Read from
    /// <c>ShutoffTime</c> (the configured value) rather than <c>CurrentAutoOffValue</c> (a live
    /// countdown that's only non-zero while actively counting down) - confirmed against a live
    /// device showing 60 min.
    /// </summary>
    public async Task<int?> ReadAutoOffMinutesAsync()
    {
        var raw = await ReadUInt16Async(_shutoffTimeChar);
        return raw is null ? null : raw.Value / 60;
    }

    /// <summary>Writes the auto-shutoff duration in minutes (converted to the raw seconds unit).</summary>
    public Task SetAutoOffMinutesAsync(int minutes) =>
        WriteUInt16RawAsync(_shutoffTimeChar, (ushort)(minutes * 60), "Abschalt-Timer setzen");

    public async Task<(bool Fahrenheit, bool DisplayOnCooling)?> ReadDisplayFlagsAsync()
    {
        var raw = await ReadUInt16Async(_displayChar);
        if (raw is null) return null;

        return (
            (raw.Value & VolcanoUuids.DisplayFlags.FahrenheitEnabled) != 0,
            // Inverted vs. Fahrenheit: bit clear means the feature is ON, confirmed live.
            (raw.Value & VolcanoUuids.DisplayFlags.DisplayOnCoolingEnabled) == 0);
    }

    public Task SetFahrenheitAsync(bool enabled) =>
        WriteFlagAsync(_displayChar, VolcanoUuids.DisplayFlags.FahrenheitEnabled, enabled, "Temperatureinheit setzen");

    public Task SetDisplayOnCoolingAsync(bool enabled) =>
        WriteFlagAsync(_displayChar, VolcanoUuids.DisplayFlags.DisplayOnCoolingEnabled, !enabled, "Anzeige beim Abkühlen setzen");

    /// <summary>Bit clear means vibration is ON (inverted polarity vs. a typical flag), confirmed live.</summary>
    public async Task<bool?> ReadVibrationAsync()
    {
        var raw = await ReadUInt16Async(_vibrationChar);
        return raw is null ? null : (raw.Value & VolcanoUuids.VibrationFlags.VibrationEnabled) == 0;
    }

    public Task SetVibrationAsync(bool enabled) =>
        WriteFlagAsync(_vibrationChar, VolcanoUuids.VibrationFlags.VibrationEnabled, !enabled, "Vibrationsalarm setzen");

    private async Task<ulong?> FindDeviceAddressAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ulong?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };
        _watcher = watcher;

        void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            var name = args.Advertisement.LocalName;
            if (string.IsNullOrEmpty(name)) return;
            if (!VolcanoUuids.NamePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))) return;
            tcs.TrySetResult(args.BluetoothAddress);
        }

        watcher.Received += OnReceived;
        using var ctReg = ct.Register(() => tcs.TrySetCanceled());

        watcher.Start();
        try
        {
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(ScanTimeoutSeconds), ct);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask);
            return completed == timeoutTask ? null : await tcs.Task;
        }
        finally
        {
            watcher.Received -= OnReceived;
            if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
            {
                watcher.Stop();
            }
            _watcher = null;
        }
    }

    private async Task<bool> ConnectToDeviceAsync(ulong address)
    {
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (device is null)
        {
            RaiseError("Gerät konnte nach dem Scan nicht verbunden werden.");
            return false;
        }

        _device = device;
        device.ConnectionStatusChanged += OnDeviceConnectionStatusChanged;

        if (!await TryResolveServicesAndCharacteristicsAsync())
        {
            TearDown();
            return false;
        }

        SetState(ConnectionState.Connected);
        return true;
    }

    private async Task<bool> TryResolveServicesAndCharacteristicsAsync()
    {
        if (_device is null) return false;

        _stateService = await GetServiceAsync(VolcanoUuids.Services.DeviceState);
        if (_stateService is null) return false;

        _controlService = await GetServiceAsync(VolcanoUuids.Services.DeviceControl);
        if (_controlService is null) return false;

        _currentTemperatureChar = await GetCharacteristicAsync(_controlService, VolcanoUuids.Characteristics.CurrentTemperature);
        _targetTemperatureChar = await GetCharacteristicAsync(_controlService, VolcanoUuids.Characteristics.TargetTemperature);
        _heaterOnChar = await GetCharacteristicAsync(_controlService, VolcanoUuids.Characteristics.HeaterOn);
        _heaterOffChar = await GetCharacteristicAsync(_controlService, VolcanoUuids.Characteristics.HeaterOff);
        _pumpOnChar = await GetCharacteristicAsync(_controlService, VolcanoUuids.Characteristics.PumpOn);
        _pumpOffChar = await GetCharacteristicAsync(_controlService, VolcanoUuids.Characteristics.PumpOff);
        _activityChar = await GetCharacteristicAsync(_stateService, VolcanoUuids.Characteristics.Activity);

        if (_currentTemperatureChar is null || _targetTemperatureChar is null || _activityChar is null ||
            _heaterOnChar is null || _heaterOffChar is null || _pumpOnChar is null || _pumpOffChar is null)
        {
            RaiseError("Eine oder mehrere erwartete GATT-Charakteristiken wurden nicht gefunden.");
            return false;
        }

        if (!await SubscribeNotifyAsync(_currentTemperatureChar, OnCurrentTemperatureValueChanged)) return false;
        await ReadInitialCurrentTemperatureAsync();
        if (!await SubscribeNotifyAsync(_activityChar, OnActivityValueChanged)) return false;

        // Secondary device-info/device-settings characteristics are optional: missing ones just
        // leave the corresponding setting unavailable in the UI rather than failing the connection.
        _brightnessChar = await GetCharacteristicAsync(_controlService, VolcanoUuids.Characteristics.Brightness);
        _currentAutoOffValueChar = await GetCharacteristicAsync(_controlService, VolcanoUuids.Characteristics.CurrentAutoOffValue);
        _shutoffTimeChar = await GetCharacteristicAsync(_controlService, VolcanoUuids.Characteristics.ShutoffTime);
        _hoursOfHeatingChar = await GetCharacteristicAsync(_controlService, VolcanoUuids.Characteristics.HoursOfHeating);
        _minutesOfHeatingChar = await GetCharacteristicAsync(_controlService, VolcanoUuids.Characteristics.MinutesOfHeating);
        _firmwareVersionChar = await GetCharacteristicAsync(_stateService, VolcanoUuids.Characteristics.FirmwareVersion);
        _firmwareBleVersionChar = await GetCharacteristicAsync(_stateService, VolcanoUuids.Characteristics.FirmwareBleVersion);
        _serialNumberChar = await GetCharacteristicAsync(_stateService, VolcanoUuids.Characteristics.SerialNumber);
        _displayChar = await GetCharacteristicAsync(_stateService, VolcanoUuids.Characteristics.Display);
        _vibrationChar = await GetCharacteristicAsync(_stateService, VolcanoUuids.Characteristics.Vibration);

        if (_brightnessChar is null || _currentAutoOffValueChar is null || _shutoffTimeChar is null ||
            _hoursOfHeatingChar is null || _minutesOfHeatingChar is null || _firmwareVersionChar is null ||
            _firmwareBleVersionChar is null || _serialNumberChar is null || _displayChar is null || _vibrationChar is null)
        {
            _logService.Log("Eine oder mehrere optionale Geräte-Charakteristiken wurden nicht gefunden - die zugehörigen Einstellungen bleiben deaktiviert.");
        }

        return true;
    }

    private async Task ReadInitialCurrentTemperatureAsync()
    {
        if (_currentTemperatureChar is null) return;
        var result = await _currentTemperatureChar.ReadValueAsync(BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success) return;
        var raw = BleEncoding.FromUInt16LEBytes(result.Value.ToArray());
        var celsius = BleEncoding.DecodeTemperature(raw);
        _logService.Log($"Anfangstemperatur gelesen: {celsius:0}°C.");
        CurrentTemperatureChanged?.Invoke(this, celsius);
    }

    private async Task<GattDeviceService?> GetServiceAsync(Guid serviceUuid)
    {
        if (_device is null) return null;
        var result = await _device.GetGattServicesForUuidAsync(serviceUuid, BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success || result.Services.Count == 0)
        {
            RaiseError($"Service {serviceUuid} nicht gefunden (Status: {result.Status}). Läuft evtl. noch die offizielle App?");
            return null;
        }
        return result.Services[0];
    }

    private static async Task<GattCharacteristic?> GetCharacteristicAsync(GattDeviceService service, Guid characteristicUuid)
    {
        var result = await service.GetCharacteristicsForUuidAsync(characteristicUuid, BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count == 0)
        {
            return null;
        }
        return result.Characteristics[0];
    }

    private async Task<bool> SubscribeNotifyAsync(
        GattCharacteristic characteristic,
        TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler)
    {
        characteristic.ValueChanged += handler;
        var status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (status != GattCommunicationStatus.Success)
        {
            characteristic.ValueChanged -= handler;
            RaiseError($"Notify-Abo für {characteristic.Uuid} fehlgeschlagen (Status: {status}).");
            return false;
        }
        return true;
    }

    private void OnCurrentTemperatureValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var raw = BleEncoding.FromUInt16LEBytes(args.CharacteristicValue.ToArray());
        var celsius = BleEncoding.DecodeTemperature(raw);
        CurrentTemperatureChanged?.Invoke(this, celsius);
    }

    private void OnActivityValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var raw = BleEncoding.FromUInt16LEBytes(args.CharacteristicValue.ToArray());
        var heating = (raw & VolcanoUuids.ActivityFlags.HeatingEnabled) != 0;
        var pumping = (raw & VolcanoUuids.ActivityFlags.PumpEnabled) != 0;
        _logService.Log($"Status empfangen: Heizung {(heating ? "an" : "aus")}, Pumpe {(pumping ? "an" : "aus")}.");
        ActivityChanged?.Invoke(this, raw);
    }

    private void OnDeviceConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected) return;
        if (State != ConnectionState.Connected) return;

        TearDown();
        RaiseError("Volcano hat die Verbindung unerwartet getrennt.");
        SetState(ConnectionState.Disconnected);
    }

    private async Task WriteUInt16Async(GattCharacteristic? characteristic, ushort value, string context)
    {
        if (characteristic is null)
        {
            RaiseError($"{context}: nicht verbunden.");
            return;
        }
        var status = await characteristic.WriteValueAsync(BleEncoding.ToUInt16LEBytes(value).AsBuffer());
        if (status != GattCommunicationStatus.Success)
        {
            RaiseError($"{context} fehlgeschlagen (Status: {status}).");
            return;
        }
        _logService.Log($"{context}: {BleEncoding.DecodeTemperature(value):0}°C gesendet.");
    }

    private async Task WriteTriggerAsync(GattCharacteristic? characteristic, string context)
    {
        if (characteristic is null)
        {
            RaiseError($"{context}: nicht verbunden.");
            return;
        }
        var status = await characteristic.WriteValueAsync(new byte[] { 0 }.AsBuffer());
        if (status != GattCommunicationStatus.Success)
        {
            RaiseError($"{context} fehlgeschlagen (Status: {status}).");
            return;
        }
        _logService.Log($"{context}.");
    }

    private async Task WriteUInt16RawAsync(GattCharacteristic? characteristic, ushort value, string context)
    {
        if (characteristic is null)
        {
            RaiseError($"{context}: nicht verbunden.");
            return;
        }
        var status = await characteristic.WriteValueAsync(BleEncoding.ToUInt16LEBytes(value).AsBuffer());
        if (status != GattCommunicationStatus.Success)
        {
            RaiseError($"{context} fehlgeschlagen (Status: {status}).");
            return;
        }
        _logService.Log($"{context}: {value} gesendet.");
    }

    /// <summary>
    /// Writes a bit-flag "toggle command" as used by the Display/Vibration characteristics: the
    /// raw flag value alone sets the bit, the flag value with bit 16 (0x10000) added clears it.
    /// </summary>
    private async Task WriteFlagAsync(GattCharacteristic? characteristic, ushort flag, bool enable, string context)
    {
        if (characteristic is null)
        {
            RaiseError($"{context}: nicht verbunden.");
            return;
        }
        var command = enable ? flag : (uint)(0x10000 + flag);
        var status = await characteristic.WriteValueAsync(BleEncoding.ToUInt32LEBytes(command).AsBuffer());
        if (status != GattCommunicationStatus.Success)
        {
            RaiseError($"{context} fehlgeschlagen (Status: {status}).");
            return;
        }
        _logService.Log($"{context}: {(enable ? "an" : "aus")}.");
    }

    private async Task<ushort?> ReadUInt16Async(GattCharacteristic? characteristic)
    {
        if (characteristic is null) return null;
        var result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success) return null;
        var bytes = result.Value.ToArray();
        return bytes.Length >= 2 ? BleEncoding.FromUInt16LEBytes(bytes) : bytes.Length == 1 ? bytes[0] : null;
    }

    private async Task<string?> ReadUtf8Async(GattCharacteristic? characteristic)
    {
        if (characteristic is null) return null;
        var result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success) return null;
        return BleEncoding.DecodeUtf8(result.Value.ToArray());
    }

    private void TearDown()
    {
        if (_currentTemperatureChar is not null)
        {
            _currentTemperatureChar.ValueChanged -= OnCurrentTemperatureValueChanged;
        }
        if (_activityChar is not null)
        {
            _activityChar.ValueChanged -= OnActivityValueChanged;
        }

        _controlService?.Dispose();
        _stateService?.Dispose();

        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= OnDeviceConnectionStatusChanged;
            _device.Dispose();
        }

        _currentTemperatureChar = null;
        _targetTemperatureChar = null;
        _activityChar = null;
        _heaterOnChar = null;
        _heaterOffChar = null;
        _pumpOnChar = null;
        _pumpOffChar = null;
        _brightnessChar = null;
        _currentAutoOffValueChar = null;
        _shutoffTimeChar = null;
        _hoursOfHeatingChar = null;
        _minutesOfHeatingChar = null;
        _firmwareVersionChar = null;
        _firmwareBleVersionChar = null;
        _serialNumberChar = null;
        _displayChar = null;
        _vibrationChar = null;
        _controlService = null;
        _stateService = null;
        _device = null;
    }

    private void SetState(ConnectionState state)
    {
        State = state;
        _logService.Log($"Status: {state}.");
        ConnectionStateChanged?.Invoke(this, state);
    }

    private void RaiseError(string message)
    {
        _logService.Log(message);
        ErrorOccurred?.Invoke(this, message);
    }

    public ValueTask DisposeAsync()
    {
        TearDown();
        _watcher?.Stop();
        return ValueTask.CompletedTask;
    }
}
