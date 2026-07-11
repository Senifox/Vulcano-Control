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
public sealed class VolcanoBluetoothService : IVolcanoDevice
{
    private const int ScanTimeoutSeconds = 15;

    // Some Windows 10 machines have been observed with markedly slower (but still working) BLE
    // GATT round-trips than a typical Windows 11 machine (services/characteristics taking single-
    // digit seconds each instead of well under one) - generous since there are now only a handful
    // of these calls total (2 service + 2 unfiltered characteristic reads), not one per characteristic.
    private const int GattOperationTimeoutSeconds = 30;

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

    // Cached full characteristic lists per service, fetched once via GetCharacteristicsAsync()
    // (no UUID filter) - GetCharacteristicsForUuidAsync() was found to reliably time out on at
    // least one Windows 10 machine while the unfiltered call and GetGattServicesForUuidAsync()
    // both worked fine, so every individual characteristic is now looked up locally from these
    // cached lists instead of querying the device again per characteristic.
    private IReadOnlyList<GattCharacteristic>? _controlCharacteristics;
    private IReadOnlyList<GattCharacteristic>? _stateCharacteristics;

    // Resolving the optional characteristics happens in the background after the connection is
    // already Connected (see ConnectToDeviceAsync) - awaited by the device-setting read/write
    // methods so they wait for it without ever blocking the core connect flow on it.
    private Task? _optionalCharacteristicsResolutionTask;

    // Fallback for devices/characteristics that don't support Notify on CurrentAutoOffValue -
    // only started if the notify subscription attempt fails.
    private CancellationTokenSource? _autoOffPollCts;

    private readonly LogService _logService;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public event EventHandler<ConnectionState>? ConnectionStateChanged;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<double>? CurrentTemperatureChanged;
    public event EventHandler<ushort>? ActivityChanged;

    /// <summary>Live countdown (seconds) until the device auto-shuts-off; 0 while not counting down.</summary>
    public event EventHandler<int>? RemainingAutoOffSecondsChanged;

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
        await EnsureOptionalCharacteristicsResolvedAsync();

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

    public async Task<int?> ReadBrightnessAsync()
    {
        await EnsureOptionalCharacteristicsResolvedAsync();
        return await ReadUInt16Async(_brightnessChar);
    }

    public async Task SetBrightnessAsync(int level)
    {
        await EnsureOptionalCharacteristicsResolvedAsync();
        await WriteUInt16RawAsync(_brightnessChar, (ushort)level, "Helligkeit setzen");
    }

    /// <summary>
    /// Reads the currently configured auto-shutoff duration, in minutes. Read from
    /// <c>ShutoffTime</c> (the configured value) rather than <c>CurrentAutoOffValue</c> (a live
    /// countdown that's only non-zero while actively counting down) - confirmed against a live
    /// device showing 60 min.
    /// </summary>
    public async Task<int?> ReadAutoOffMinutesAsync()
    {
        await EnsureOptionalCharacteristicsResolvedAsync();
        var raw = await ReadUInt16Async(_shutoffTimeChar);
        return raw is null ? null : raw.Value / 60;
    }

    /// <summary>Writes the auto-shutoff duration in minutes (converted to the raw seconds unit).</summary>
    public async Task SetAutoOffMinutesAsync(int minutes)
    {
        await EnsureOptionalCharacteristicsResolvedAsync();
        await WriteUInt16RawAsync(_shutoffTimeChar, (ushort)(minutes * 60), "Abschalt-Timer setzen");
    }

    public async Task<(bool Fahrenheit, bool DisplayOnCooling)?> ReadDisplayFlagsAsync()
    {
        await EnsureOptionalCharacteristicsResolvedAsync();
        var raw = await ReadUInt16Async(_displayChar);
        if (raw is null) return null;

        return (
            (raw.Value & VolcanoUuids.DisplayFlags.FahrenheitEnabled) != 0,
            // Inverted vs. Fahrenheit: bit clear means the feature is ON, confirmed live.
            (raw.Value & VolcanoUuids.DisplayFlags.DisplayOnCoolingEnabled) == 0);
    }

    public async Task SetFahrenheitAsync(bool enabled)
    {
        await EnsureOptionalCharacteristicsResolvedAsync();
        await WriteFlagAsync(_displayChar, VolcanoUuids.DisplayFlags.FahrenheitEnabled, enabled, "Temperatureinheit setzen");
    }

    public async Task SetDisplayOnCoolingAsync(bool enabled)
    {
        await EnsureOptionalCharacteristicsResolvedAsync();
        await WriteFlagAsync(_displayChar, VolcanoUuids.DisplayFlags.DisplayOnCoolingEnabled, !enabled, "Anzeige beim Abkühlen setzen");
    }

    /// <summary>Bit clear means vibration is ON (inverted polarity vs. a typical flag), confirmed live.</summary>
    public async Task<bool?> ReadVibrationAsync()
    {
        await EnsureOptionalCharacteristicsResolvedAsync();
        var raw = await ReadUInt16Async(_vibrationChar);
        return raw is null ? null : (raw.Value & VolcanoUuids.VibrationFlags.VibrationEnabled) == 0;
    }

    public async Task SetVibrationAsync(bool enabled)
    {
        await EnsureOptionalCharacteristicsResolvedAsync();
        await WriteFlagAsync(_vibrationChar, VolcanoUuids.VibrationFlags.VibrationEnabled, !enabled, "Vibrationsalarm setzen");
    }

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
        _logService.Log("Verbinde mit Geräteadresse...");
        var deviceTask = BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask();
        var completed = await Task.WhenAny(deviceTask, Task.Delay(TimeSpan.FromSeconds(GattOperationTimeoutSeconds)));
        if (completed != deviceTask)
        {
            RaiseError($"Verbindung zur Geräteadresse: Timeout nach {GattOperationTimeoutSeconds}s.");
            return false;
        }

        var device = await deviceTask;
        if (device is null)
        {
            RaiseError("Gerät konnte nach dem Scan nicht verbunden werden.");
            return false;
        }
        _logService.Log("Geräteadresse verbunden, löse GATT-Services auf...");

        _device = device;
        device.ConnectionStatusChanged += OnDeviceConnectionStatusChanged;

        if (!await TryResolveCoreCharacteristicsAsync())
        {
            TearDown();
            return false;
        }

        SetState(ConnectionState.Connected);

        // The secondary device-info/device-settings characteristics (used only by the settings
        // dialog) are resolved in the background from here on - a slow or hanging GATT lookup on
        // any of them (seen on some Windows 10 machines) must never delay reaching Connected,
        // since none of them are needed for core temperature/heater/pump control.
        _optionalCharacteristicsResolutionTask = ResolveOptionalCharacteristicsAsync();

        return true;
    }

    private async Task<bool> TryResolveCoreCharacteristicsAsync()
    {
        if (_device is null) return false;

        _stateService = await GetServiceAsync(VolcanoUuids.Services.DeviceState, "DeviceState");
        if (_stateService is null) return false;

        _controlService = await GetServiceAsync(VolcanoUuids.Services.DeviceControl, "DeviceControl");
        if (_controlService is null) return false;

        _controlCharacteristics = await GetAllCharacteristicsAsync(_controlService, "DeviceControl");
        _stateCharacteristics = await GetAllCharacteristicsAsync(_stateService, "DeviceState");

        _currentTemperatureChar = FindCharacteristic(_controlCharacteristics, VolcanoUuids.Characteristics.CurrentTemperature, "CurrentTemperature");
        _targetTemperatureChar = FindCharacteristic(_controlCharacteristics, VolcanoUuids.Characteristics.TargetTemperature, "TargetTemperature");
        _heaterOnChar = FindCharacteristic(_controlCharacteristics, VolcanoUuids.Characteristics.HeaterOn, "HeaterOn");
        _heaterOffChar = FindCharacteristic(_controlCharacteristics, VolcanoUuids.Characteristics.HeaterOff, "HeaterOff");
        _pumpOnChar = FindCharacteristic(_controlCharacteristics, VolcanoUuids.Characteristics.PumpOn, "PumpOn");
        _pumpOffChar = FindCharacteristic(_controlCharacteristics, VolcanoUuids.Characteristics.PumpOff, "PumpOff");
        _activityChar = FindCharacteristic(_stateCharacteristics, VolcanoUuids.Characteristics.Activity, "Activity");

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

    /// <summary>
    /// Resolves the ten secondary device-info/device-settings characteristics used by the
    /// settings dialog, from the characteristic lists already cached during core resolution (no
    /// further BLE round-trips). Runs in the background after Connected (see
    /// ConnectToDeviceAsync). Missing ones just leave the corresponding setting unavailable.
    /// </summary>
    private async Task ResolveOptionalCharacteristicsAsync()
    {
        _brightnessChar = FindCharacteristic(_controlCharacteristics, VolcanoUuids.Characteristics.Brightness, "Brightness");
        _currentAutoOffValueChar = FindCharacteristic(_controlCharacteristics, VolcanoUuids.Characteristics.CurrentAutoOffValue, "CurrentAutoOffValue");
        _shutoffTimeChar = FindCharacteristic(_controlCharacteristics, VolcanoUuids.Characteristics.ShutoffTime, "ShutoffTime");
        _hoursOfHeatingChar = FindCharacteristic(_controlCharacteristics, VolcanoUuids.Characteristics.HoursOfHeating, "HoursOfHeating");
        _minutesOfHeatingChar = FindCharacteristic(_controlCharacteristics, VolcanoUuids.Characteristics.MinutesOfHeating, "MinutesOfHeating");
        _firmwareVersionChar = FindCharacteristic(_stateCharacteristics, VolcanoUuids.Characteristics.FirmwareVersion, "FirmwareVersion");
        _firmwareBleVersionChar = FindCharacteristic(_stateCharacteristics, VolcanoUuids.Characteristics.FirmwareBleVersion, "FirmwareBleVersion");
        _serialNumberChar = FindCharacteristic(_stateCharacteristics, VolcanoUuids.Characteristics.SerialNumber, "SerialNumber");
        _displayChar = FindCharacteristic(_stateCharacteristics, VolcanoUuids.Characteristics.Display, "Display");
        _vibrationChar = FindCharacteristic(_stateCharacteristics, VolcanoUuids.Characteristics.Vibration, "Vibration");

        if (_brightnessChar is null || _currentAutoOffValueChar is null || _shutoffTimeChar is null ||
            _hoursOfHeatingChar is null || _minutesOfHeatingChar is null || _firmwareVersionChar is null ||
            _firmwareBleVersionChar is null || _serialNumberChar is null || _displayChar is null || _vibrationChar is null)
        {
            _logService.Log("Eine oder mehrere optionale Geräte-Charakteristiken wurden nicht gefunden - die zugehörigen Einstellungen bleiben deaktiviert.", LogLevel.Warning);
        }
        else
        {
            _logService.Log("Optionale Geräte-Charakteristiken vollständig aufgelöst.");
        }

        await StartAutoOffTrackingAsync();
    }

    /// <summary>
    /// Tracks the live auto-shutoff countdown via Notify where supported, falling back to
    /// periodic polling otherwise (some optional characteristics have been observed not to
    /// support Notify the way the core temperature/activity ones do).
    /// </summary>
    private async Task StartAutoOffTrackingAsync()
    {
        if (_currentAutoOffValueChar is null) return;

        var subscribed = await TrySubscribeNotifyQuietAsync(_currentAutoOffValueChar, OnCurrentAutoOffValueChanged);
        if (subscribed)
        {
            _logService.Log("Live-Abo für verbleibende Abschalt-Zeit eingerichtet.", LogLevel.Debug);
        }
        else
        {
            _logService.Log("Kein Notify für verbleibende Abschalt-Zeit verfügbar - falle auf Polling zurück.", LogLevel.Debug);
            StartAutoOffPolling();
        }

        var initial = await ReadUInt16Async(_currentAutoOffValueChar);
        if (initial is not null)
        {
            RemainingAutoOffSecondsChanged?.Invoke(this, initial.Value);
        }
    }

    private void OnCurrentAutoOffValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var raw = BleEncoding.FromUInt16LEBytes(args.CharacteristicValue.ToArray());
        RemainingAutoOffSecondsChanged?.Invoke(this, raw);
    }

    private void StartAutoOffPolling()
    {
        _autoOffPollCts = new CancellationTokenSource();
        var ct = _autoOffPollCts.Token;

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var raw = await ReadUInt16Async(_currentAutoOffValueChar);
                    if (raw is not null)
                    {
                        RemainingAutoOffSecondsChanged?.Invoke(this, raw.Value);
                    }
                }
                catch
                {
                    // Best-effort polling; a transient read failure shouldn't stop the loop.
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }, ct);
    }

    /// <summary>
    /// Like SubscribeNotifyAsync, but treats a failed subscription as an expected, silent
    /// fallback case (no RaiseError) rather than a connection-critical failure - used for
    /// optional characteristics where Notify support isn't guaranteed.
    /// </summary>
    private async Task<bool> TrySubscribeNotifyQuietAsync(
        GattCharacteristic characteristic,
        TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler)
    {
        characteristic.ValueChanged += handler;
        GattCommunicationStatus status;
        try
        {
            status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
        }
        catch (Exception)
        {
            status = GattCommunicationStatus.Unreachable;
        }

        if (status != GattCommunicationStatus.Success)
        {
            characteristic.ValueChanged -= handler;
            return false;
        }
        return true;
    }

    /// <summary>Awaits the background optional-characteristic resolution, if it's still running.</summary>
    private async Task EnsureOptionalCharacteristicsResolvedAsync()
    {
        if (_optionalCharacteristicsResolutionTask is not null)
        {
            await _optionalCharacteristicsResolutionTask;
        }
    }

    private async Task ReadInitialCurrentTemperatureAsync()
    {
        if (_currentTemperatureChar is null) return;
        var result = await _currentTemperatureChar.ReadValueAsync(BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success) return;
        var raw = BleEncoding.FromUInt16LEBytes(result.Value.ToArray());
        var celsius = BleEncoding.DecodeTemperature(raw);
        _logService.Log($"Anfangstemperatur gelesen: {celsius:0}°C.", LogLevel.Debug);
        CurrentTemperatureChanged?.Invoke(this, celsius);
    }

    private async Task<GattDeviceService?> GetServiceAsync(Guid serviceUuid, string name)
    {
        if (_device is null) return null;

        _logService.Log($"Suche Service '{name}'...", LogLevel.Debug);
        var task = _device.GetGattServicesForUuidAsync(serviceUuid, BluetoothCacheMode.Uncached).AsTask();
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(GattOperationTimeoutSeconds)));
        if (completed != task)
        {
            RaiseError($"Service '{name}' ({serviceUuid}): Timeout nach {GattOperationTimeoutSeconds}s beim Auflösen.");
            return null;
        }

        GattDeviceServicesResult result;
        try
        {
            result = await task;
        }
        catch (Exception ex)
        {
            RaiseError($"Service '{name}' ({serviceUuid}): Fehler beim Auflösen ({ex.Message}).");
            return null;
        }

        if (result.Status != GattCommunicationStatus.Success || result.Services.Count == 0)
        {
            RaiseError($"Service '{name}' ({serviceUuid}) nicht gefunden (Status: {result.Status}). Läuft evtl. noch die offizielle App?");
            return null;
        }

        _logService.Log($"Service '{name}' gefunden.", LogLevel.Debug);
        return result.Services[0];
    }

    /// <summary>
    /// Fetches every characteristic of a service in one round-trip (no UUID filter). Preferred
    /// over GetCharacteristicsForUuidAsync(), which was found to reliably time out on at least
    /// one Windows 10 machine while this unfiltered call worked fine.
    /// </summary>
    private async Task<IReadOnlyList<GattCharacteristic>?> GetAllCharacteristicsAsync(GattDeviceService service, string serviceName)
    {
        _logService.Log($"Lese alle Characteristics von '{serviceName}'...", LogLevel.Debug);
        var task = service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask();
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(GattOperationTimeoutSeconds)));
        if (completed != task)
        {
            RaiseError($"Characteristics von '{serviceName}': Timeout nach {GattOperationTimeoutSeconds}s beim Auflösen.");
            return null;
        }

        GattCharacteristicsResult result;
        try
        {
            result = await task;
        }
        catch (Exception ex)
        {
            RaiseError($"Characteristics von '{serviceName}': Fehler beim Auflösen ({ex.Message}).");
            return null;
        }

        if (result.Status != GattCommunicationStatus.Success)
        {
            RaiseError($"Characteristics von '{serviceName}' nicht lesbar (Status: {result.Status}).");
            return null;
        }

        _logService.Log($"{result.Characteristics.Count} Characteristic(s) von '{serviceName}' gelesen.", LogLevel.Debug);
        return result.Characteristics;
    }

    private GattCharacteristic? FindCharacteristic(IReadOnlyList<GattCharacteristic>? characteristics, Guid characteristicUuid, string name)
    {
        var match = characteristics?.FirstOrDefault(c => c.Uuid == characteristicUuid);
        _logService.Log(match is not null ? $"Characteristic '{name}' gefunden." : $"Characteristic '{name}' nicht gefunden.", LogLevel.Debug);
        return match;
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
        if (_currentAutoOffValueChar is not null)
        {
            _currentAutoOffValueChar.ValueChanged -= OnCurrentAutoOffValueChanged;
        }
        _autoOffPollCts?.Cancel();
        _autoOffPollCts?.Dispose();
        _autoOffPollCts = null;

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
        _controlCharacteristics = null;
        _stateCharacteristics = null;
        _optionalCharacteristicsResolutionTask = null;
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
        _logService.Log(message, LogLevel.Error);
        ErrorOccurred?.Invoke(this, message);
    }

    public ValueTask DisposeAsync()
    {
        TearDown();
        _watcher?.Stop();
        return ValueTask.CompletedTask;
    }
}
