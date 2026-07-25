using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.Core.Tests;

public sealed class SimulatedVolcanoDeviceTests : IAsyncDisposable
{
    private readonly string _logFile = Path.Combine(Path.GetTempPath(), $"vulcano-test-{Guid.NewGuid():N}.log");
    private readonly LogService _log;
    private readonly SimulatedVolcanoDevice _device;

    public SimulatedVolcanoDeviceTests()
    {
        _log = new LogService(_logFile);
        _device = new SimulatedVolcanoDevice(
            _log,
            notifyInterval: TimeSpan.FromMilliseconds(25),
            scanDuration: TimeSpan.FromMilliseconds(20));
    }

    public async ValueTask DisposeAsync()
    {
        await _device.DisposeAsync();
        try { File.Delete(_logFile); } catch { /* best-effort */ }
    }

    private static async Task WaitFor(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        Assert.Fail($"Timed out waiting for: {because}");
    }

    [Fact]
    public async Task Connecting_walks_through_the_same_states_as_the_real_device()
    {
        var states = new List<ConnectionState>();
        _device.ConnectionStateChanged += (_, s) => states.Add(s);

        Assert.True(await _device.ScanAndConnectAsync());

        Assert.Equal(
            [ConnectionState.Scanning, ConnectionState.Connecting, ConnectionState.Connected],
            states);
    }

    [Fact]
    public async Task It_heats_towards_the_target_while_the_heater_is_on()
    {
        await _device.ScanAndConnectAsync();
        _device.CurrentCelsius = 100;

        double latest = 0;
        _device.CurrentTemperatureChanged += (_, c) => latest = c;

        await _device.SetTargetTemperatureAsync(180);
        await _device.SetHeaterAsync(true);

        await WaitFor(() => latest > 100, "the temperature to rise");
        Assert.InRange(latest, 100, 180);
    }

    [Fact]
    public async Task It_never_overshoots_the_target()
    {
        await _device.ScanAndConnectAsync();
        _device.CurrentCelsius = 179;

        var readings = new List<double>();
        _device.CurrentTemperatureChanged += (_, c) => readings.Add(c);

        await _device.SetTargetTemperatureAsync(180);
        await _device.SetHeaterAsync(true);

        await WaitFor(() => readings.Count >= 3, "a few readings");
        Assert.All(readings, r => Assert.True(r <= 180.01, $"reading {r} overshot the target"));
    }

    [Fact]
    public async Task It_cools_back_down_once_the_heater_is_off()
    {
        await _device.ScanAndConnectAsync();
        _device.CurrentCelsius = 200;

        double latest = 200;
        _device.CurrentTemperatureChanged += (_, c) => latest = c;

        await _device.SetHeaterAsync(false);

        await WaitFor(() => latest < 200, "the temperature to fall");
    }

    [Fact]
    public async Task Switching_the_heater_on_restarts_the_auto_shut_off_countdown()
    {
        await _device.ScanAndConnectAsync();
        await _device.SetAutoOffMinutesAsync(40);

        var remaining = -1;
        _device.RemainingAutoOffSecondsChanged += (_, s) => remaining = s;

        await _device.SetHeaterAsync(true);

        Assert.Equal(40 * 60, remaining);
    }

    [Fact]
    public async Task The_activity_flags_report_heater_and_pump()
    {
        await _device.ScanAndConnectAsync();

        ushort activity = 0;
        _device.ActivityChanged += (_, a) => activity = a;

        await _device.SetHeaterAsync(true);
        Assert.True((activity & VolcanoUuids.ActivityFlags.HeatingEnabled) != 0);
        Assert.True((activity & VolcanoUuids.ActivityFlags.PumpEnabled) == 0);

        await _device.SetPumpAsync(true);
        Assert.True((activity & VolcanoUuids.ActivityFlags.PumpEnabled) != 0);
    }

    [Fact]
    public async Task A_simulated_connection_loss_looks_like_the_real_thing()
    {
        await _device.ScanAndConnectAsync();

        var errors = new List<string>();
        _device.ErrorOccurred += (_, m) => errors.Add(m);

        await _device.SimulateConnectionLossAsync();

        Assert.Equal(ConnectionState.Error, _device.State);
        Assert.Single(errors);
    }

    [Fact]
    public async Task Device_settings_round_trip()
    {
        await _device.ScanAndConnectAsync();

        await _device.SetBrightnessAsync(55);
        await _device.SetAutoOffMinutesAsync(90);
        await _device.SetDisplayOnCoolingAsync(true);
        await _device.SetVibrationAsync(false);

        Assert.Equal(55, await _device.ReadBrightnessAsync());
        Assert.Equal(90, await _device.ReadAutoOffMinutesAsync());
        Assert.Equal((false, true), await _device.ReadDisplayFlagsAsync());
        Assert.False(await _device.ReadVibrationAsync());
    }

    [Fact]
    public async Task The_target_temperature_stays_inside_what_the_device_accepts()
    {
        await _device.ScanAndConnectAsync();
        _device.CurrentCelsius = 100;

        double latest = 0;
        _device.CurrentTemperatureChanged += (_, c) => latest = c;

        await _device.SetTargetTemperatureAsync(999);
        await _device.SetHeaterAsync(true);

        await WaitFor(() => latest > 100, "the temperature to rise");
        await Task.Delay(400);

        Assert.True(latest <= RampValidation.MaxCelsius + 0.01, $"reached {latest} °C");
    }
}
