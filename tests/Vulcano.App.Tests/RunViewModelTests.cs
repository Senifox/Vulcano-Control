using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vulcano.App.ViewModels;
using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.App.Tests;

/// <summary>
/// What a running ramp looks like while it runs. The strip across the top is the interesting part:
/// it is built from the plan when there is one and from bare numbers when there is not, and a relay
/// client only got a plan at all once the host started sending it.
/// </summary>
public sealed class RunViewModelTests : IDisposable
{
    private readonly string _logFile =
        Path.Combine(Path.GetTempPath(), $"vulcano-run-vm-{Guid.NewGuid():N}.log");

    private readonly FakeVolcanoDevice _device = new();
    private readonly LogService _log;
    private readonly RampSessionController _ramp;
    private readonly RunViewModel _vm;

    private static readonly RampPoint[] Points =
    [
        new(0, 180, CurveKind.Linear),
        new(10, 200, CurveKind.Linear),
        new(20, 220, CurveKind.Linear),
    ];

    public RunViewModelTests()
    {
        _log = new LogService(_logFile);
        _ramp = new RampSessionController(_device, _log, TimeSpan.FromMilliseconds(25));
        _vm = new RunViewModel(_device, _ramp);
    }

    public void Dispose()
    {
        _vm.Dispose();
        _ramp.Dispose();
        try { File.Delete(_logFile); } catch { /* best-effort */ }
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private async Task StartAsync(TimeSpan hold = default) =>
        await _ramp.StartAsync(new TemperatureRampPlan(Points, hold), heaterCurrentlyOn: true);

    [AvaloniaFact]
    public async Task The_strip_is_warm_up_then_a_block_per_segment()
    {
        await StartAsync(TimeSpan.FromMinutes(5));

        await Wait.ForAsync(() => { Pump(); return _vm.Segments.Count > 0; }, "the strip to be built");

        // Warm-up, two segments, and the hold - the shape of the run, not just its numbers.
        Assert.Equal(4, _vm.Segments.Count);
        Assert.Equal(2, _vm.SegmentCount);
    }

    [AvaloniaFact]
    public async Task Without_a_hold_the_strip_has_no_hold_block()
    {
        await StartAsync();

        await Wait.ForAsync(() => { Pump(); return _vm.Segments.Count > 0; }, "the strip to be built");

        Assert.Equal(3, _vm.Segments.Count);
    }

    [AvaloniaFact]
    public async Task Warm_up_is_the_active_block_until_the_device_arrives()
    {
        await StartAsync();
        await Wait.ForAsync(() => { Pump(); return _vm.Segments.Count > 0; }, "the strip to be built");

        Assert.True(_vm.IsWarmingUp);
        Assert.True(_vm.Segments[0].IsActive);

        _device.ReportTemperature(180);

        await Wait.ForAsync(() => { Pump(); return !_vm.IsWarmingUp; }, "warm-up to finish");
        Assert.True(_vm.Segments[0].IsComplete);
        Assert.True(_vm.Segments[1].IsActive);
    }

    [AvaloniaFact]
    public async Task The_segment_detail_names_the_curve_and_where_it_is_heading()
    {
        await StartAsync();
        _device.ReportTemperature(180);

        await Wait.ForAsync(() => { Pump(); return _vm.SegmentDetail.Length > 0; }, "the segment detail");

        Assert.Contains("200", _vm.SegmentDetail);
    }

    [AvaloniaFact]
    public async Task Pausing_and_resuming_go_through_to_the_controller()
    {
        await StartAsync();
        _device.ReportTemperature(180);
        await Wait.ForAsync(() => { Pump(); return !_vm.IsWarmingUp; }, "the ramp to be running");

        _vm.TogglePauseCommand.Execute(null);
        await Wait.ForAsync(() => { Pump(); return _vm.IsPaused; }, "the pause to be reported back");
        Assert.True(_ramp.IsPaused);

        _vm.TogglePauseCommand.Execute(null);
        await Wait.ForAsync(() => { Pump(); return !_vm.IsPaused; }, "the resume to be reported back");
        Assert.False(_ramp.IsPaused);
    }

    [AvaloniaFact]
    public async Task Stopping_stops_the_ramp()
    {
        await StartAsync();
        await Wait.ForAsync(() => { Pump(); return _vm.Segments.Count > 0; }, "the ramp to be running");

        _vm.StopRampCommand.Execute(null);

        Assert.False(_ramp.IsRunning);
    }

    [AvaloniaFact]
    public async Task The_measured_value_and_the_heater_come_from_the_device()
    {
        await StartAsync();

        _device.ReportTemperature(176.4);
        _device.ReportHeater(true);
        Pump();

        Assert.Equal("176", _vm.MeasuredText);
        Assert.True(_vm.IsHeaterOn);
    }
}
