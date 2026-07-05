using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Vulcano_Control.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;

namespace Vulcano_Control.Services;

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
