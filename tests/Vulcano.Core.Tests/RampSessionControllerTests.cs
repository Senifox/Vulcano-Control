using System.Collections.Concurrent;
using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.Core.Tests;

public sealed class RampSessionControllerTests : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(25);

    private readonly string _logFile = Path.Combine(Path.GetTempPath(), $"vulcano-test-{Guid.NewGuid():N}.log");
    private readonly FakeVolcanoDevice _device = new();
    private readonly ConcurrentQueue<RampProgressEventArgs> _progress = new();
    private readonly LogService _log;
    private readonly RampSessionController _controller;

    private static readonly RampPoint[] Points =
    [
        new(0, 180, CurveKind.Linear),
        new(10, 200, CurveKind.Linear),
        new(30, 220, CurveKind.Linear),
    ];

    public RampSessionControllerTests()
    {
        _log = new LogService(_logFile);
        _controller = new RampSessionController(_device, _log, Tick);
        _controller.ProgressChanged += (_, e) => _progress.Enqueue(e);
    }

    public void Dispose()
    {
        _controller.Dispose();
        try { File.Delete(_logFile); } catch { /* best-effort */ }
    }

    private Task StartAsync() =>
        _controller.StartAsync(new TemperatureRampPlan(Points, TimeSpan.FromMinutes(5)), heaterCurrentlyOn: true);

    private static Task WaitFor(Func<bool> condition, string because) =>
        Wait.ForAsync(condition, because);

    /// <summary>Gets the ramp past warm-up and returns once it is actually running the curve.</summary>
    private async Task RunToRampingAsync()
    {
        var warmedUp = false;
        _controller.WarmupCompleted += (_, _) => warmedUp = true;

        await StartAsync();
        _device.ReportTemperature(180);

        await WaitFor(() => warmedUp, "warm-up to complete");
    }

    [Fact]
    public async Task Starting_writes_the_first_points_temperature()
    {
        await StartAsync();

        Assert.True(_controller.IsRunning);
        Assert.Equal(180, Assert.Single(_device.WrittenTargets));
    }

    [Fact]
    public async Task The_clock_does_not_start_until_the_device_is_at_the_first_point()
    {
        var warmedUp = false;
        _controller.WarmupCompleted += (_, _) => warmedUp = true;

        await StartAsync();
        _device.ReportTemperature(120);

        await WaitFor(() => _progress.Count > 2, "a few ticks to pass");
        Assert.False(warmedUp);
        Assert.All(_progress, p => Assert.True(p.IsWarmingUp));

        _device.ReportTemperature(180);
        await WaitFor(() => warmedUp, "warm-up to complete once the temperature is reached");
    }

    [Fact]
    public async Task Skipping_during_warm_up_starts_the_curve_without_waiting()
    {
        var warmedUp = false;
        _controller.WarmupCompleted += (_, _) => warmedUp = true;

        await StartAsync();
        _device.ReportTemperature(120); // nowhere near the first point
        await WaitFor(() => !_progress.IsEmpty, "the first tick");

        _controller.SkipSegment();

        await WaitFor(() => warmedUp, "the warm-up to be skipped");
    }

    [Fact]
    public async Task Skipping_a_segment_jumps_to_the_end_of_that_segment()
    {
        await RunToRampingAsync();
        await WaitFor(() => _progress.Any(p => !p.IsWarmingUp), "the curve to start");

        _controller.SkipSegment();

        // Segment 0 runs 0-10 min, so skipping it puts the clock at minute 10 - which is where
        // segment 1 begins.
        await WaitFor(
            () => _progress.Any(p => !p.IsWarmingUp && p.SegmentIndex == 1),
            "the ramp to be in the second segment");

        var latest = _progress.Last(p => !p.IsWarmingUp);
        Assert.True(latest.Elapsed >= TimeSpan.FromMinutes(10), $"elapsed was {latest.Elapsed}");
        Assert.Equal(2, latest.SegmentCount);
    }

    [Fact]
    public async Task Pausing_freezes_the_clock_and_resuming_continues_it()
    {
        await RunToRampingAsync();
        await WaitFor(() => _progress.Any(p => !p.IsWarmingUp), "the curve to start");

        _controller.Pause();
        Assert.True(_controller.IsPaused);

        await WaitFor(() => _progress.Count(p => p.IsPaused) >= 2, "two ticks while paused");
        var whilePaused = _progress.Where(p => p.IsPaused).ToList();
        Assert.Equal(whilePaused[0].Elapsed, whilePaused[^1].Elapsed);

        var frozenAt = whilePaused[^1].Elapsed;
        _progress.Clear();
        _controller.Resume();
        Assert.False(_controller.IsPaused);

        await WaitFor(
            () => _progress.Any(p => !p.IsPaused && p.Elapsed > frozenAt),
            "the clock to move again after resuming");
    }

    [Fact]
    public async Task A_paused_ramp_stops_writing_to_the_device()
    {
        await RunToRampingAsync();
        _controller.Pause();

        var writesWhilePaused = _device.WrittenTargets.Count;
        await WaitFor(() => _progress.Count(p => p.IsPaused) >= 3, "several ticks while paused");

        Assert.Equal(writesWhilePaused, _device.WrittenTargets.Count);
    }

    /// <summary>
    /// The device has an auto shut-off of its own - five minutes out of the box, against a ramp that
    /// can run for thirty-five. When it fires mid-ramp the controller switches the heater back on;
    /// without that the ramp would keep counting and pushing targets at a device that had quietly
    /// stopped heating, and the profile would finish on paper only.
    /// </summary>
    [Fact]
    public async Task The_heater_goes_back_on_when_the_device_switches_it_off_mid_ramp()
    {
        await RunToRampingAsync();
        Assert.Empty(_device.WrittenHeaterStates);

        _device.ReportHeater(false);

        await WaitFor(() => _device.WrittenHeaterStates.Count > 0, "the heater to be switched back on");
        Assert.True(_device.WrittenHeaterStates[0]);

        // And only once. The write is assumed to have worked until the device says otherwise, so a
        // notification still in flight cannot turn into one heater write per tick.
        var ticks = _progress.Count;
        await WaitFor(() => _progress.Count >= ticks + 3, "a few more ticks to pass");
        Assert.Single(_device.WrittenHeaterStates);
    }

    [Fact]
    public async Task Losing_the_connection_pauses_the_ramp_instead_of_aborting_it()
    {
        await RunToRampingAsync();

        _device.ReportConnectionState(ConnectionState.Error);

        Assert.True(_controller.IsPaused);
        Assert.True(_controller.IsRunning);
    }

    [Fact]
    public async Task Reconnecting_continues_the_ramp_by_itself()
    {
        await RunToRampingAsync();
        _device.ReportConnectionState(ConnectionState.Error);

        _device.ReportConnectionState(ConnectionState.Connected);

        Assert.False(_controller.IsPaused);
        Assert.True(_controller.IsRunning);
    }

    [Fact]
    public async Task Reconnecting_does_not_undo_a_pause_the_user_asked_for()
    {
        await RunToRampingAsync();
        _controller.Pause();

        _device.ReportConnectionState(ConnectionState.Error);
        _device.ReportConnectionState(ConnectionState.Connected);

        Assert.True(_controller.IsPaused);
    }

    [Fact]
    public async Task The_running_plan_is_available_while_it_runs_and_not_before_or_after()
    {
        Assert.Null(_controller.ActivePlan);

        await StartAsync();
        Assert.NotNull(_controller.ActivePlan);
        Assert.Equal(2, _controller.ActivePlan!.SegmentCount);

        _controller.Stop();
        Assert.Null(_controller.ActivePlan);
    }

    [Fact]
    public async Task Stopping_ends_the_ramp_and_reports_it()
    {
        var stopped = false;
        _controller.Stopped += (_, _) => stopped = true;

        await RunToRampingAsync();
        _controller.Stop();

        Assert.True(stopped);
        Assert.False(_controller.IsRunning);
        Assert.False(_controller.IsPaused);
    }
}
