using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.Core.Tests;

/// <summary>
/// The Volcano protocol, checked against a fake transport. Most of what is asserted here was
/// learned from a real device and cannot be re-derived from a datasheet, so it is worth pinning:
/// inverted flag polarity, the toggle-command form of a flag write, seconds versus minutes, and the
/// fact that some reads come back one byte short.
/// </summary>
public sealed class BluetoothVolcanoDeviceTests : IAsyncDisposable
{

    private readonly string _logFile = Path.Combine(Path.GetTempPath(), $"vulcano-ble-{Guid.NewGuid():N}.log");
    private readonly LogService _log;
    private readonly FakeVolcanoTransport _transport = new();
    private readonly BluetoothVolcanoDevice _device;

    public BluetoothVolcanoDeviceTests()
    {
        _log = new LogService(_logFile);
        _device = new BluetoothVolcanoDevice(_transport, _log, TimeSpan.FromMilliseconds(150));
    }

    public async ValueTask DisposeAsync()
    {
        await _device.DisposeAsync();
        try { File.Delete(_logFile); } catch { /* best-effort */ }
    }

    private async Task<bool> ConnectAsync()
    {
        _transport.GiveEverything();
        _transport.Advertise("STORZ&BICKEL VOLCANO");
        return await _device.ScanAndConnectAsync();
    }

    private FakeConnection Connection => _transport.Connection!;

    private byte[] LastWriteTo(Guid characteristic) =>
        Connection.Writes.Last(w => w.Characteristic == characteristic).Value;

    [Fact]
    public async Task It_connects_to_a_device_whose_name_matches_a_known_prefix()
    {
        _transport.GiveEverything();
        _transport.Advertise("Some Headphones", "11:11");
        _transport.Advertise("S&B VOLCANO H", "22:22");

        Assert.True(await _device.ScanAndConnectAsync());
        Assert.Equal(ConnectionState.Connected, _device.State);
    }

    [Fact]
    public async Task A_device_with_no_matching_name_is_not_connected_to()
    {
        _transport.GiveEverything();
        _transport.Advertise("Some Headphones");

        var errors = new List<string>();
        _device.ErrorOccurred += (_, m) => errors.Add(m);

        Assert.False(await _device.ScanAndConnectAsync());
        Assert.Equal(ConnectionState.Error, _device.State);
        Assert.Single(errors);
    }

    [Fact]
    public async Task A_device_missing_a_required_characteristic_is_refused()
    {
        _transport.GiveEverything();
        _transport.Characteristics.Remove(VolcanoUuids.Characteristics.PumpOn);
        _transport.Advertise("STORZ&BICKEL");

        Assert.False(await _device.ScanAndConnectAsync());
        Assert.Equal(ConnectionState.Error, _device.State);
    }

    [Fact]
    public async Task Connecting_subscribes_to_temperature_and_activity()
    {
        Assert.True(await ConnectAsync());

        Assert.True(Connection.IsSubscribed(VolcanoUuids.Characteristics.CurrentTemperature));
        Assert.True(Connection.IsSubscribed(VolcanoUuids.Characteristics.Activity));
    }

    [Fact]
    public async Task The_first_temperature_is_read_rather_than_waited_for()
    {
        var readings = new List<double>();
        _device.CurrentTemperatureChanged += (_, c) => readings.Add(c);

        await ConnectAsync();

        // Notifications only arrive on change; without the initial read the readout would stay
        // empty until the temperature happened to move.
        Assert.Contains(193.0, readings);
    }

    [Fact]
    public async Task A_notification_is_decoded_as_tenths_of_a_degree()
    {
        double latest = 0;
        await ConnectAsync();
        _device.CurrentTemperatureChanged += (_, c) => latest = c;

        Connection.Notify(VolcanoUuids.Characteristics.CurrentTemperature, 2043);

        Assert.Equal(204.3, latest, 3);
    }

    [Fact]
    public async Task The_target_temperature_is_written_as_tenths_little_endian()
    {
        await ConnectAsync();

        await _device.SetTargetTemperatureAsync(195);

        // 195 °C -> 1950 -> 0x079E, low byte first.
        Assert.Equal(new byte[] { 0x9E, 0x07 }, LastWriteTo(VolcanoUuids.Characteristics.TargetTemperature));
    }

    [Fact]
    public async Task The_heater_is_two_characteristics_triggered_by_a_zero_byte()
    {
        await ConnectAsync();

        await _device.SetHeaterAsync(true);
        Assert.Equal(new byte[] { 0 }, LastWriteTo(VolcanoUuids.Characteristics.HeaterOn));

        await _device.SetHeaterAsync(false);
        Assert.Equal(new byte[] { 0 }, LastWriteTo(VolcanoUuids.Characteristics.HeaterOff));
    }

    [Fact]
    public async Task The_pump_works_the_same_way()
    {
        await ConnectAsync();

        await _device.SetPumpAsync(true);
        Assert.Equal(new byte[] { 0 }, LastWriteTo(VolcanoUuids.Characteristics.PumpOn));

        await _device.SetPumpAsync(false);
        Assert.Equal(new byte[] { 0 }, LastWriteTo(VolcanoUuids.Characteristics.PumpOff));
    }

    [Fact]
    public async Task The_auto_shut_off_is_stored_in_seconds_and_shown_in_minutes()
    {
        await ConnectAsync();

        Assert.Equal(40, await _device.ReadAutoOffMinutesAsync());

        await _device.SetAutoOffMinutesAsync(90);
        Assert.Equal(
            FakeVolcanoTransport.Bytes(90 * 60),
            LastWriteTo(VolcanoUuids.Characteristics.ShutoffTime));
    }

    [Fact]
    public async Task Fahrenheit_reads_with_normal_polarity_and_display_on_cooling_inverted()
    {
        _transport.GiveEverything();
        // Fahrenheit bit set, DisplayOnCooling bit clear.
        _transport.Characteristics[VolcanoUuids.Characteristics.Display] =
            FakeVolcanoTransport.Bytes(VolcanoUuids.DisplayFlags.FahrenheitEnabled);
        _transport.Advertise("STORZ&BICKEL");
        await _device.ScanAndConnectAsync();

        var flags = await _device.ReadDisplayFlagsAsync();

        Assert.NotNull(flags);
        Assert.True(flags!.Value.Fahrenheit);
        // Bit clear means the feature is on - the trap this test exists for.
        Assert.True(flags.Value.DisplayOnCooling);
    }

    [Fact]
    public async Task Switching_a_flag_on_writes_the_flag_and_off_writes_it_plus_bit_sixteen()
    {
        await ConnectAsync();

        await _device.SetFahrenheitAsync(true);
        Assert.Equal(
            BleEncoding.ToUInt32LEBytes(VolcanoUuids.DisplayFlags.FahrenheitEnabled),
            LastWriteTo(VolcanoUuids.Characteristics.Display));

        await _device.SetFahrenheitAsync(false);
        Assert.Equal(
            BleEncoding.ToUInt32LEBytes(0x10000u + VolcanoUuids.DisplayFlags.FahrenheitEnabled),
            LastWriteTo(VolcanoUuids.Characteristics.Display));
    }

    [Fact]
    public async Task Display_on_cooling_writes_the_opposite_command_because_its_bit_is_inverted()
    {
        await ConnectAsync();

        // Switching the feature ON has to CLEAR the bit, so this is the plus-bit-sixteen form.
        await _device.SetDisplayOnCoolingAsync(true);

        Assert.Equal(
            BleEncoding.ToUInt32LEBytes(0x10000u + VolcanoUuids.DisplayFlags.DisplayOnCoolingEnabled),
            LastWriteTo(VolcanoUuids.Characteristics.Display));
    }

    [Fact]
    public async Task Vibration_is_inverted_in_both_directions()
    {
        _transport.GiveEverything();
        _transport.Characteristics[VolcanoUuids.Characteristics.Vibration] = FakeVolcanoTransport.Bytes(0);
        _transport.Advertise("STORZ&BICKEL");
        await _device.ScanAndConnectAsync();

        // Bit clear -> on.
        Assert.True(await _device.ReadVibrationAsync());

        await _device.SetVibrationAsync(true);
        Assert.Equal(
            BleEncoding.ToUInt32LEBytes(0x10000u + VolcanoUuids.VibrationFlags.VibrationEnabled),
            LastWriteTo(VolcanoUuids.Characteristics.Vibration));
    }

    [Fact]
    public async Task A_reading_that_comes_back_one_byte_short_is_still_a_value()
    {
        _transport.GiveEverything();
        _transport.Characteristics[VolcanoUuids.Characteristics.Brightness] = [55];
        _transport.Advertise("STORZ&BICKEL");
        await _device.ScanAndConnectAsync();

        Assert.Equal(55, await _device.ReadBrightnessAsync());
    }

    [Fact]
    public async Task The_device_info_block_reads_as_text_and_numbers()
    {
        await ConnectAsync();

        var info = await _device.ReadDeviceInfoAsync();

        Assert.NotNull(info);
        Assert.Equal("VC22C0281", info!.Value.SerialNumber);
        Assert.Equal("V1.63", info.Value.FirmwareVersion);
        Assert.Equal("V1.35", info.Value.FirmwareBleVersion);
        Assert.Equal(412, info.Value.HoursOfHeating);
    }

    [Fact]
    public async Task A_missing_optional_characteristic_leaves_its_setting_unavailable_rather_than_failing()
    {
        _transport.GiveEverything();
        _transport.Characteristics.Remove(VolcanoUuids.Characteristics.Brightness);
        _transport.Advertise("STORZ&BICKEL");

        // Still connects: brightness is not needed to control temperature.
        Assert.True(await _device.ScanAndConnectAsync());
        Assert.Null(await _device.ReadBrightnessAsync());
    }

    [Fact]
    public async Task An_unexpected_disconnect_becomes_an_error_state_not_a_clean_disconnect()
    {
        await ConnectAsync();

        var states = new List<ConnectionState>();
        _device.ConnectionStateChanged += (_, s) => states.Add(s);

        Connection.DropConnection();

        // Error, not Disconnected: a running ramp pauses on this and resumes when it comes back,
        // which is not what pressing Disconnect should do.
        Assert.Equal(ConnectionState.Error, _device.State);
        Assert.Contains(ConnectionState.Error, states);
    }

    [Fact]
    public async Task Disconnecting_closes_the_connection()
    {
        await ConnectAsync();

        await _device.DisconnectAsync();

        Assert.True(Connection.IsDisposed);
        Assert.Equal(ConnectionState.Disconnected, _device.State);
    }

    [Fact]
    public async Task A_characteristic_that_refuses_notify_still_reports_its_value()
    {
        _transport.GiveEverything();
        _transport.Characteristics[VolcanoUuids.Characteristics.CurrentAutoOffValue] =
            FakeVolcanoTransport.Bytes(1634);
        _transport.NotifyRefused.Add(VolcanoUuids.Characteristics.CurrentAutoOffValue);
        _transport.Advertise("STORZ&BICKEL");

        var seconds = new List<int>();
        _device.RemainingAutoOffSecondsChanged += (_, s) => seconds.Add(s);

        await _device.ScanAndConnectAsync();

        // The subscription is refused, so this arrives from the initial read that comes with the
        // polling fallback.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (seconds.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);

        Assert.Contains(1634, seconds);
    }
}
