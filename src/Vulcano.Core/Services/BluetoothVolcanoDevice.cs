using Vulcano.Core.Models;

namespace Vulcano.Core.Services;

/// <summary>
/// The Volcano protocol on top of an <see cref="IVolcanoTransport"/>: which characteristic carries
/// what, how the bytes are encoded, in which order to do things when connecting. Platform-free, so
/// the same code drives WinRT on Windows and BlueZ on Linux, and a fake transport in tests.
///
/// Several details here were learned from a real device and are not guessable. They are marked
/// individually; the important ones:
///
/// - Heater and pump have separate on/off characteristics, triggered by writing a single zero byte.
/// - Display and vibration flags have inverted polarity: the bit being *clear* means the feature is
///   on. Writing them is a toggle command, not a value.
/// - The configured auto shut-off is ShutoffTime in seconds; CurrentAutoOffValue is the live
///   countdown and reads zero when nothing is counting down.
/// - Notify is not available on CurrentAutoOffValue on every device, so it falls back to polling.
/// </summary>
public sealed class BluetoothVolcanoDevice : IVolcanoDevice
{
    private static readonly TimeSpan DefaultScanTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AutoOffPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Written to clear a flag: the raw flag value sets the bit, the value plus bit 16
    /// clears it. A quirk of the device, confirmed live.</summary>
    private const uint ClearFlagOffset = 0x10000;

    private static readonly Guid[] RequiredCharacteristics =
    [
        VolcanoUuids.Characteristics.CurrentTemperature,
        VolcanoUuids.Characteristics.TargetTemperature,
        VolcanoUuids.Characteristics.Activity,
        VolcanoUuids.Characteristics.HeaterOn,
        VolcanoUuids.Characteristics.HeaterOff,
        VolcanoUuids.Characteristics.PumpOn,
        VolcanoUuids.Characteristics.PumpOff,
    ];

    private readonly IVolcanoTransport _transport;
    private readonly LogService _logService;
    private readonly TimeSpan _scanTimeout;

    private IVolcanoConnection? _connection;
    private CancellationTokenSource? _autoOffPollCts;
    private ConnectionState _state = ConnectionState.Disconnected;

    /// <summary>So a device that has gone quiet is logged once rather than on every notification.</summary>
    private bool _sawZeroTemperature;

    /// <param name="scanTimeout">How long to keep scanning before giving up. Only tests pass this -
    /// they would otherwise sit out the full fifteen seconds to check the "nothing found" path.</param>
    public BluetoothVolcanoDevice(
        IVolcanoTransport transport, LogService logService, TimeSpan? scanTimeout = null)
    {
        _transport = transport;
        _logService = logService;
        _scanTimeout = scanTimeout ?? DefaultScanTimeout;
    }

    public ConnectionState State => _state;

    public bool IsRemote => false;

    public string? HostName => null;

    public event EventHandler<ConnectionState>? ConnectionStateChanged;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<double>? CurrentTemperatureChanged;
    public event EventHandler<ushort>? ActivityChanged;
    public event EventHandler<int>? RemainingAutoOffSecondsChanged;

    public async Task<bool> ScanAndConnectAsync(CancellationToken ct = default)
    {
        if (_state == ConnectionState.Connected) return true;

        SetState(ConnectionState.Scanning);

        DiscoveredDevice? found;
        try
        {
            found = await FindVolcanoAsync(ct);
        }
        catch (OperationCanceledException)
        {
            SetState(ConnectionState.Disconnected);
            return false;
        }

        if (found is null)
        {
            Fail(Strings.Get("Error.DeviceNotFound"));
            SetState(ConnectionState.Error);
            return false;
        }

        _logService.Log(Strings.Get("Log.DeviceFound", found.Name));
        SetState(ConnectionState.Connecting);

        try
        {
            return await ConnectAsync(found, ct);
        }
        catch (Exception ex)
        {
            Fail(Strings.Get("Error.ConnectFailed", ex.Message));
            await TearDownAsync();
            SetState(ConnectionState.Error);
            return false;
        }
    }

    /// <summary>
    /// Takes the first advertisement whose name starts with one of the known prefixes. Which names
    /// count is device knowledge and lives here rather than in the transport.
    /// </summary>
    private async Task<DiscoveredDevice?> FindVolcanoAsync(CancellationToken ct)
    {
        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        scanCts.CancelAfter(_scanTimeout);

        try
        {
            await foreach (var device in _transport.ScanAsync(scanCts.Token))
            {
                if (VolcanoUuids.NamePrefixes.Any(
                        prefix => device.Name.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    return device;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own scan timeout, not the caller giving up: "nothing found" rather than cancelled.
            return null;
        }

        return null;
    }

    private async Task<bool> ConnectAsync(DiscoveredDevice device, CancellationToken ct)
    {
        var connection = await _transport.ConnectAsync(
            device.Id,
            [VolcanoUuids.Services.DeviceState, VolcanoUuids.Services.DeviceControl],
            ct);

        if (connection is null)
        {
            Fail(Strings.Get("Error.ServicesNotFound"));
            SetState(ConnectionState.Error);
            return false;
        }

        _connection = connection;
        connection.Disconnected += OnTransportDisconnected;

        var missing = RequiredCharacteristics.Where(c => !connection.Supports(c)).ToArray();
        if (missing.Length > 0)
        {
            Fail(Strings.Get("Error.CharacteristicsMissing", missing.Length));
            await TearDownAsync();
            SetState(ConnectionState.Error);
            return false;
        }

        if (!await connection.SubscribeAsync(VolcanoUuids.Characteristics.CurrentTemperature, OnTemperatureValue, ct) ||
            !await connection.SubscribeAsync(VolcanoUuids.Characteristics.Activity, OnActivityValue, ct))
        {
            Fail(Strings.Get("Error.NotifyFailed"));
            await TearDownAsync();
            SetState(ConnectionState.Error);
            return false;
        }

        // Notifications only arrive on change, so without this the readout stays empty until the
        // temperature happens to move - which on a cold device standing idle can be a very long time.
        await ReadInitialTemperatureAsync();

        SetState(ConnectionState.Connected);

        // Deliberately not awaited: on some machines a GATT round trip takes seconds, and none of
        // this is needed to control temperature, heater or pump. Reaching Connected must not wait
        // for the auto shut-off countdown to be wired up.
        _ = StartAutoOffTrackingAsync();

        return true;
    }

    public async Task DisconnectAsync()
    {
        await TearDownAsync();
        SetState(ConnectionState.Disconnected);
    }

    // --- Control ---

    public Task SetTargetTemperatureAsync(double celsius) =>
        WriteUInt16Async(
            VolcanoUuids.Characteristics.TargetTemperature,
            BleEncoding.EncodeTemperature(celsius),
            Strings.Get("Log.TargetSet", Formatting.Celsius(celsius)));

    public async Task<double?> ReadTargetTemperatureAsync() =>
        await ReadUInt16Async(VolcanoUuids.Characteristics.TargetTemperature) is { } raw
            ? BleEncoding.DecodeTemperature(raw)
            : null;

    /// <summary>Two separate characteristics rather than one with a value; writing a single zero
    /// byte to either is the trigger.</summary>
    public Task SetHeaterAsync(bool on) =>
        WriteTriggerAsync(
            on ? VolcanoUuids.Characteristics.HeaterOn : VolcanoUuids.Characteristics.HeaterOff,
            Strings.Get(on ? "Log.HeaterOn" : "Log.HeaterOff"));

    public Task SetPumpAsync(bool on) =>
        WriteTriggerAsync(
            on ? VolcanoUuids.Characteristics.PumpOn : VolcanoUuids.Characteristics.PumpOff,
            Strings.Get(on ? "Log.PumpOn" : "Log.PumpOff"));

    // --- Device information and settings ---

    public async Task<VolcanoDeviceInfo?> ReadDeviceInfoAsync()
    {
        var serial = await ReadUtf8Async(VolcanoUuids.Characteristics.SerialNumber);
        var firmware = await ReadUtf8Async(VolcanoUuids.Characteristics.FirmwareVersion);
        var bleFirmware = await ReadUtf8Async(VolcanoUuids.Characteristics.FirmwareBleVersion);
        var hours = await ReadUInt16Async(VolcanoUuids.Characteristics.HoursOfHeating);
        var minutes = await ReadUInt16Async(VolcanoUuids.Characteristics.MinutesOfHeating);

        if (serial is null || firmware is null || bleFirmware is null || hours is null || minutes is null)
        {
            return null;
        }

        return new VolcanoDeviceInfo(serial, firmware, bleFirmware, hours.Value, minutes.Value);
    }

    public async Task<int?> ReadBrightnessAsync() =>
        await ReadUInt16Async(VolcanoUuids.Characteristics.Brightness);

    public Task SetBrightnessAsync(int level) =>
        WriteUInt16Async(
            VolcanoUuids.Characteristics.Brightness,
            (ushort)Math.Clamp(level, 0, 100),
            Strings.Get("Log.BrightnessSet", level));

    /// <summary>
    /// The configured duration, from ShutoffTime in seconds. Deliberately not CurrentAutoOffValue -
    /// that is the live countdown and reads zero whenever nothing is counting down, which looks like
    /// "auto shut-off is disabled" and is not.
    /// </summary>
    public async Task<int?> ReadAutoOffMinutesAsync()
    {
        var raw = await ReadUInt16Async(VolcanoUuids.Characteristics.ShutoffTime);
        return raw is null ? null : raw.Value / 60;
    }

    public Task SetAutoOffMinutesAsync(int minutes) =>
        WriteUInt16Async(
            VolcanoUuids.Characteristics.ShutoffTime,
            (ushort)(minutes * 60),
            Strings.Get("Log.AutoOffSet", minutes));

    public async Task<(bool Fahrenheit, bool DisplayOnCooling)?> ReadDisplayFlagsAsync()
    {
        var raw = await ReadUInt16Async(VolcanoUuids.Characteristics.Display);
        if (raw is null) return null;

        return (
            (raw.Value & VolcanoUuids.DisplayFlags.FahrenheitEnabled) != 0,
            // Inverted against Fahrenheit in the same characteristic: bit clear means on.
            (raw.Value & VolcanoUuids.DisplayFlags.DisplayOnCoolingEnabled) == 0);
    }

    public Task SetFahrenheitAsync(bool enabled) =>
        WriteFlagAsync(
            VolcanoUuids.Characteristics.Display,
            VolcanoUuids.DisplayFlags.FahrenheitEnabled,
            enabled,
            Strings.Get("Log.UnitSet"));

    public Task SetDisplayOnCoolingAsync(bool enabled) =>
        WriteFlagAsync(
            VolcanoUuids.Characteristics.Display,
            VolcanoUuids.DisplayFlags.DisplayOnCoolingEnabled,
            // Inverted: to switch the feature on, the bit has to be cleared.
            !enabled,
            Strings.Get("Log.DisplayOnCoolingSet"));

    /// <summary>Inverted polarity again: bit clear means vibration is on.</summary>
    public async Task<bool?> ReadVibrationAsync()
    {
        var raw = await ReadUInt16Async(VolcanoUuids.Characteristics.Vibration);
        return raw is null ? null : (raw.Value & VolcanoUuids.VibrationFlags.VibrationEnabled) == 0;
    }

    public Task SetVibrationAsync(bool enabled) =>
        WriteFlagAsync(
            VolcanoUuids.Characteristics.Vibration,
            VolcanoUuids.VibrationFlags.VibrationEnabled,
            !enabled,
            Strings.Get("Log.VibrationSet"));

    // --- Auto shut-off countdown ---

    private async Task StartAutoOffTrackingAsync()
    {
        if (_connection is not { } connection) return;
        if (!connection.Supports(VolcanoUuids.Characteristics.CurrentAutoOffValue)) return;

        var subscribed = await connection.SubscribeAsync(
            VolcanoUuids.Characteristics.CurrentAutoOffValue, OnAutoOffValue);

        if (subscribed)
        {
            _logService.Log(Strings.Get("Log.AutoOffNotify"), LogLevel.Debug);
        }
        else
        {
            // Expected on some devices rather than an error - the value is still readable.
            _logService.Log(Strings.Get("Log.AutoOffPolling"), LogLevel.Debug);
            StartAutoOffPolling();
        }

        if (await ReadUInt16Async(VolcanoUuids.Characteristics.CurrentAutoOffValue) is { } initial)
        {
            RemainingAutoOffSecondsChanged?.Invoke(this, initial);
        }
    }

    private void StartAutoOffPolling()
    {
        _autoOffPollCts = new CancellationTokenSource();
        var ct = _autoOffPollCts.Token;

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(AutoOffPollInterval);

            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    if (await ReadUInt16Async(VolcanoUuids.Characteristics.CurrentAutoOffValue) is { } raw)
                    {
                        RemainingAutoOffSecondsChanged?.Invoke(this, raw);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Disconnected.
            }
        }, ct);
    }

    /// <summary>
    /// Reads the temperature once on connect, retrying while the device answers with a raw zero.
    ///
    /// What was observed: connecting to a cold device that was sitting idle, the read answered zero
    /// five times over a second and a half, and no notification followed - notifications only come
    /// on a change, and nothing was changing. Every earlier session had the device either heating or
    /// cooling from a heat, which is why this never showed up before.
    ///
    /// Why it answers zero is not established. It may be that the reading is only maintained above
    /// the temperature the device is willing to display, around 40 °C, in which case a cooling
    /// device goes quiet at the same point. That is worth measuring rather than assuming, and the
    /// measurement tool's cooling phase runs past 40 °C precisely to find out.
    ///
    /// Either way zero is not a temperature: the Volcano works from 40 °C up and stands in a room.
    /// </summary>
    private async Task ReadInitialTemperatureAsync()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (await ReadUInt16Async(VolcanoUuids.Characteristics.CurrentTemperature) is { } raw && raw != 0)
            {
                CurrentTemperatureChanged?.Invoke(this, BleEncoding.DecodeTemperature(raw));
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }

        _logService.Log(Strings.Get("Log.NoInitialTemperature"), LogLevel.Warning);
    }

    // --- Notification handlers ---

    private void OnTemperatureValue(byte[] value)
    {
        if (value.Length < 2) return;

        var raw = BleEncoding.FromUInt16LEBytes(value);

        // Zero is the device declining to say, not a temperature. Passing it on would put 0 °C on
        // screen and into a recording, and 0 °C is not a state a Volcano standing in a room is in -
        // it works from 40 °C up. See ReadInitialTemperatureAsync for where this was first seen.
        if (raw == 0)
        {
            if (!_sawZeroTemperature)
            {
                _sawZeroTemperature = true;
                _logService.Log(Strings.Get("Log.ZeroTemperature"), LogLevel.Debug);
            }
            return;
        }

        _sawZeroTemperature = false;
        CurrentTemperatureChanged?.Invoke(this, BleEncoding.DecodeTemperature(raw));
    }

    private void OnActivityValue(byte[] value)
    {
        if (value.Length < 2) return;
        ActivityChanged?.Invoke(this, BleEncoding.FromUInt16LEBytes(value));
    }

    private void OnAutoOffValue(byte[] value)
    {
        if (value.Length < 2) return;
        RemainingAutoOffSecondsChanged?.Invoke(this, BleEncoding.FromUInt16LEBytes(value));
    }

    private void OnTransportDisconnected(object? sender, DisconnectReason reason)
    {
        if (reason == DisconnectReason.Requested) return;
        if (_state != ConnectionState.Connected) return;

        // Error rather than Disconnected: a ramp pauses on this and continues when it comes back,
        // which is not what "the user pressed disconnect" should do.
        _ = TearDownAsync();
        Fail(Strings.Get("Error.DeviceConnectionLost"));
        SetState(ConnectionState.Error);
    }

    // --- Reads and writes ---

    /// <summary>
    /// Some characteristics answer with a single byte where two are expected; taking that as the
    /// value rather than as a failure is what the WPF version learned to do.
    /// </summary>
    private async Task<ushort?> ReadUInt16Async(Guid characteristic)
    {
        if (await ReadAsync(characteristic) is not { } bytes) return null;

        return bytes.Length >= 2 ? BleEncoding.FromUInt16LEBytes(bytes)
            : bytes.Length == 1 ? bytes[0]
            : null;
    }

    private async Task<string?> ReadUtf8Async(Guid characteristic) =>
        await ReadAsync(characteristic) is { } bytes ? BleEncoding.DecodeUtf8(bytes) : null;

    private async Task<byte[]?> ReadAsync(Guid characteristic)
    {
        if (_connection is not { } connection) return null;
        if (!connection.Supports(characteristic)) return null;

        return await connection.ReadAsync(characteristic);
    }

    private Task WriteUInt16Async(Guid characteristic, ushort value, string success) =>
        WriteAsync(characteristic, BleEncoding.ToUInt16LEBytes(value), success);

    /// <summary>A command with no payload: one zero byte.</summary>
    private Task WriteTriggerAsync(Guid characteristic, string success) =>
        WriteAsync(characteristic, [0], success);

    /// <summary>
    /// The toggle command the display and vibration characteristics take: the flag value on its own
    /// sets the bit, the flag value plus bit 16 clears it, written as a 32-bit value.
    /// </summary>
    private Task WriteFlagAsync(Guid characteristic, ushort flag, bool setBit, string success) =>
        WriteAsync(
            characteristic,
            BleEncoding.ToUInt32LEBytes(setBit ? flag : ClearFlagOffset + flag),
            success);

    private async Task WriteAsync(Guid characteristic, byte[] value, string success)
    {
        if (_connection is not { } connection || !connection.Supports(characteristic))
        {
            Fail(Strings.Get("Error.NotConnected"));
            return;
        }

        if (await connection.WriteAsync(characteristic, value))
        {
            _logService.Log(success);
            return;
        }

        Fail(Strings.Get("Error.WriteFailed", success));
    }

    // --- Plumbing ---

    private async Task TearDownAsync()
    {
        _autoOffPollCts?.Cancel();
        _autoOffPollCts?.Dispose();
        _autoOffPollCts = null;

        if (_connection is { } connection)
        {
            connection.Disconnected -= OnTransportDisconnected;
            _connection = null;
            await connection.DisposeAsync();
        }
    }

    private void SetState(ConnectionState state)
    {
        if (_state == state) return;

        _state = state;
        _logService.Log(Strings.Get("Log.ConnectionState", state), LogLevel.Debug);
        ConnectionStateChanged?.Invoke(this, state);
    }

    private void Fail(string message)
    {
        _logService.Log(message, LogLevel.Error);
        ErrorOccurred?.Invoke(this, message);
    }

    public async ValueTask DisposeAsync() => await TearDownAsync();
}
