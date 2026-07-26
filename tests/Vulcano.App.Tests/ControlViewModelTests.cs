using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vulcano.App.ViewModels;
using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.App.Tests;

/// <summary>
/// The cockpit's own logic. Nearly every test here stands for something that went wrong on a real
/// device this month and was found by looking at a screenshot - which is the argument for the file
/// existing.
/// </summary>
public sealed class ControlViewModelTests : IDisposable
{
    private readonly string _logFile =
        Path.Combine(Path.GetTempPath(), $"vulcano-control-vm-{Guid.NewGuid():N}.log");

    private readonly FakeVolcanoDevice _device = new();
    private readonly LogService _log;
    private readonly RampSessionController _ramp;
    private readonly ControlViewModel _vm;

    public ControlViewModelTests()
    {
        _log = new LogService(_logFile);
        _ramp = new RampSessionController(_device, _log, TimeSpan.FromMilliseconds(25));
        _vm = new ControlViewModel(_device, _ramp, new AppSettings());
    }

    public void Dispose()
    {
        _vm.Dispose();
        _ramp.Dispose();
        try { File.Delete(_logFile); } catch { /* best-effort */ }
    }

    /// <summary>The view models post every device event onto the dispatcher, so a test that raises
    /// one has to let the dispatcher run before it can assert anything.</summary>
    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private void Connect()
    {
        _device.ReportConnectionState(ConnectionState.Connected);
        Pump();
    }

    // --- The number ---

    /// <summary>
    /// A cold Volcano that has not heated since it was switched on answers the temperature read with
    /// zero and notifies nothing, so there is no reading to show. Printing 0 °C would be inventing
    /// one.
    /// </summary>
    [AvaloniaFact]
    public void Without_a_reading_the_temperature_is_a_dash_even_when_connected()
    {
        Connect();

        Assert.Equal("—", _vm.CurrentTemperatureText);
    }

    [AvaloniaFact]
    public void The_first_reading_turns_the_dash_into_a_number()
    {
        Connect();

        _device.ReportTemperature(184.6);
        Pump();

        Assert.Equal("185", _vm.CurrentTemperatureText);
    }

    // --- The switches ---

    /// <summary>
    /// The switch reports the device, not the wish. A write that never took - a watching client
    /// refused by the host, a device that did not answer - used to leave the knob showing on above a
    /// label reading off.
    /// </summary>
    [AvaloniaFact]
    public async Task A_switch_whose_write_changed_nothing_goes_back()
    {
        Connect();

        _vm.HeaterSwitchOn = true;

        // The write is recorded, but this device reports no activity back, which is what a refusal
        // looks like from here.
        await Wait.ForAsync(() => _device.WrittenHeaterStates.Count > 0, "the heater write");
        Pump();

        Assert.True(_device.WrittenHeaterStates[0]);
        Assert.False(_vm.HeaterSwitchOn);
        Assert.False(_vm.IsHeaterOn);
    }

    [AvaloniaFact]
    public void A_switch_follows_the_device_reporting_for_itself()
    {
        Connect();

        _device.ReportHeater(true);
        Pump();

        Assert.True(_vm.IsHeaterOn);
        Assert.True(_vm.HeaterSwitchOn);
    }

    // --- The chip ---

    [AvaloniaFact]
    public void The_chip_says_heating_while_the_heater_is_on_and_the_target_is_away()
    {
        Connect();
        _vm.TargetTemperature = 200;

        _device.ReportHeater(true);
        _device.ReportTemperature(120);
        Pump();

        Assert.Equal(HeatState.Heating, _vm.HeatState);
    }

    /// <summary>
    /// The chip is derived from three things and used to be recomputed only when a temperature
    /// arrived. A cooling device reports slowly, so it read "heating" for seconds after the heater
    /// had gone off.
    /// </summary>
    [AvaloniaFact]
    public void The_chip_stops_saying_heating_the_moment_the_heater_goes_off()
    {
        Connect();
        _vm.TargetTemperature = 200;
        _device.ReportHeater(true);
        _device.ReportTemperature(120);
        Pump();
        Assert.Equal(HeatState.Heating, _vm.HeatState);

        // No new temperature - only the heater switching off, which is all the device says at first.
        _device.ReportHeater(false);
        Pump();

        Assert.NotEqual(HeatState.Heating, _vm.HeatState);
    }

    [AvaloniaFact]
    public void The_chip_says_at_target_once_it_is_close_enough()
    {
        Connect();
        _vm.TargetTemperature = 185;
        _device.ReportHeater(true);
        _device.ReportTemperature(184.5);
        Pump();

        Assert.Equal(HeatState.AtTarget, _vm.HeatState);
    }

    // --- The target ---

    /// <summary>
    /// While a ramp runs it owns the target. Without this the cockpit sat at the value the device
    /// held when the app connected - 225 °C through a ramp that was driving it from 180 to 195 - and
    /// the delta underneath was nonsense.
    /// </summary>
    [AvaloniaFact]
    public async Task The_target_follows_a_running_ramp()
    {
        Connect();
        _vm.TargetTemperature = 225;

        RampPoint[] points = [new(0, 180, CurveKind.Linear), new(10, 200, CurveKind.Linear)];
        await _ramp.StartAsync(new TemperatureRampPlan(points, TimeSpan.Zero), heaterCurrentlyOn: true);

        await Wait.ForAsync(
            () => { Pump(); return Math.Abs(_vm.TargetTemperature - 180) < 0.01; },
            "the cockpit to follow the ramp's target");
    }

    [AvaloniaFact]
    public async Task The_target_is_read_back_from_the_device_when_the_ramp_stops()
    {
        Connect();
        await _device.SetTargetTemperatureAsync(190);

        RampPoint[] points = [new(0, 180, CurveKind.Linear), new(10, 200, CurveKind.Linear)];
        await _ramp.StartAsync(new TemperatureRampPlan(points, TimeSpan.Zero), heaterCurrentlyOn: true);
        await Wait.ForAsync(
            () => { Pump(); return Math.Abs(_vm.TargetTemperature - 180) < 0.01; },
            "the ramp to take the target");

        _ramp.Stop();

        // The fake hands back the last value written to it, which is what the ramp left behind.
        await Wait.ForAsync(
            () => { Pump(); return Math.Abs(_vm.TargetTemperature - _device.WrittenTargets[^1]) < 0.01; },
            "the target to be read back from the device");
    }

    // --- Connection ---

    [AvaloniaFact]
    public void Losing_the_device_clears_the_reading_and_the_chip()
    {
        Connect();
        _device.ReportHeater(true);
        _device.ReportTemperature(150);
        Pump();

        _device.ReportConnectionState(ConnectionState.Error);
        Pump();

        Assert.False(_vm.IsConnected);
        Assert.Equal("—", _vm.CurrentTemperatureText);
        Assert.Equal(HeatState.Idle, _vm.HeatState);
    }
}
