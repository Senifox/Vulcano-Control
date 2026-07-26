using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.Core.Tests;

/// <summary>
/// The tables are measurements, so the tests that matter most are the ones that ask them to
/// reproduce the run they came from. If a later edit makes the estimates disagree with what the
/// device did on 2026-07-26, the edit is wrong.
/// </summary>
public class DevicePerformanceTests
{
    [Fact]
    public void The_measured_climb_from_cold_is_reproduced()
    {
        // The device went from 33 °C to 229 °C in 63 seconds at full drive.
        var estimate = DevicePerformance.EstimateDuration(33, 229);

        Assert.InRange(estimate.TotalSeconds, 50, 80);
    }

    [Fact]
    public void The_measured_cool_down_is_reproduced()
    {
        // 230 °C to 50 °C took 43 minutes with the heater off.
        var estimate = DevicePerformance.EstimateDuration(230, 50);

        Assert.InRange(estimate.TotalMinutes, 36, 50);
    }

    [Theory]
    [InlineData(230, 100, 12, 17)]  // measured: 14 min
    [InlineData(230, 150, 4, 7)]    // measured: 5.1 min
    [InlineData(230, 200, 0.8, 2)]  // measured: 1.2 min
    public void Cooling_matches_the_run_at_each_milestone(double from, double to, double lowMinutes, double highMinutes)
    {
        var estimate = DevicePerformance.EstimateDuration(from, to);

        Assert.InRange(estimate.TotalMinutes, lowMinutes, highMinutes);
    }

    [Fact]
    public void Cooling_is_slower_the_closer_it_gets_to_the_room()
    {
        // Twenty degrees off the top takes a minute; the same twenty degrees down near the bottom
        // takes the best part of half an hour. This asymmetry is the whole reason for the warning.
        var fromTheTop = DevicePerformance.EstimateDuration(230, 210);
        var nearTheBottom = DevicePerformance.EstimateDuration(70, 50);

        Assert.True(nearTheBottom > fromTheTop * 10, $"{nearTheBottom} should dwarf {fromTheTop}");
    }

    [Fact]
    public void Heating_is_far_faster_than_cooling_over_the_same_span()
    {
        var up = DevicePerformance.EstimateDuration(100, 200);
        var down = DevicePerformance.EstimateDuration(200, 100);

        Assert.True(down > up * 20, $"cooling {down} against heating {up}");
    }

    [Fact]
    public void A_temperature_off_the_end_of_the_table_still_answers()
    {
        Assert.True(DevicePerformance.HeatingRatePerMinute(20) > 0);
        Assert.True(DevicePerformance.HeatingRatePerMinute(300) > 0);
        Assert.True(DevicePerformance.CoolingRatePerMinute(20) > 0);
        Assert.True(DevicePerformance.CoolingRatePerMinute(300) > 0);
    }

    // --- Feasibility ---

    /// <summary>The profile that started this: 185 to 230, down to 40, back to 230, a minute each.</summary>
    [Fact]
    public void The_impossible_profile_is_caught_on_the_segment_that_deserves_it()
    {
        RampPoint[] points =
        [
            new(0, 185, CurveKind.EaseInOut),
            new(1, 230, CurveKind.EaseInOut),
            new(2, 40, CurveKind.Linear),
            new(3, 230, CurveKind.Linear),
        ];
        var plan = new TemperatureRampPlan(points, TimeSpan.Zero);

        var problems = RampFeasibility.OutOfReach(plan);

        // The climb from 185 to 230 is a fortnight's work for this device: about fifteen seconds.
        // The plunge to 40 is the one that cannot happen, and it is the only one flagged.
        var problem = Assert.Single(problems);
        Assert.Equal(2, problem.SegmentNumber);
        Assert.True(problem.IsCooling);
        Assert.True(problem.Needed.TotalMinutes > 30, $"needed {problem.Needed}");
    }

    [Fact]
    public void A_ramp_the_device_can_follow_is_not_complained_about()
    {
        RampPoint[] points = [new(0, 185, CurveKind.Linear), new(35, 225, CurveKind.Linear)];
        var plan = new TemperatureRampPlan(points, TimeSpan.FromMinutes(5));

        Assert.Empty(RampFeasibility.OutOfReach(plan));
    }

    [Fact]
    public void A_segment_that_is_only_just_short_is_left_alone()
    {
        // Fifteen per cent over is inside the spread of the measurements; warning about it would
        // train people to ignore the warning.
        RampPoint[] points = [new(0, 100, CurveKind.Linear), new(1, 220, CurveKind.Linear)];
        var plan = new TemperatureRampPlan(points, TimeSpan.Zero);

        var check = Assert.Single(RampFeasibility.Check(plan));
        Assert.InRange(check.Needed.TotalSeconds, 30, 60);
        Assert.Empty(RampFeasibility.OutOfReach(plan));
    }

    /// <summary>
    /// Reported from a real session: cooling 224 °C to 192 °C in a minute needs about 1:20, and the
    /// warning read "would need about 1 min instead of 1 min". A third over is past the proportional
    /// rule, so only the absolute one silences this - twenty seconds is not worth interrupting
    /// anybody for, and a sentence naming the same figure twice is worse than no sentence.
    /// </summary>
    [Fact]
    public void Being_short_by_a_few_seconds_is_not_worth_a_sentence()
    {
        RampPoint[] points = [new(0, 224, CurveKind.Linear), new(1, 192, CurveKind.Linear)];
        var plan = new TemperatureRampPlan(points, TimeSpan.Zero);

        var check = Assert.Single(RampFeasibility.Check(plan));
        Assert.True(check.Needed > check.Allowed * 1.15, "proportionally this is well over");
        Assert.Empty(RampFeasibility.OutOfReach(plan));
    }

    /// <summary>
    /// The other way round, and the reason both rules exist: cooling 230 °C to 108 °C in eleven
    /// minutes is nearly a minute short in absolute terms but only eight per cent over. Thirty
    /// seconds off a minute is everything; thirty seconds off eleven is nothing.
    /// </summary>
    [Fact]
    public void Being_short_by_a_small_fraction_is_not_worth_a_sentence_either()
    {
        RampPoint[] points = [new(0, 230, CurveKind.Linear), new(11, 108, CurveKind.Linear)];
        var plan = new TemperatureRampPlan(points, TimeSpan.Zero);

        var check = Assert.Single(RampFeasibility.Check(plan));
        Assert.True(check.Needed - check.Allowed > TimeSpan.FromSeconds(30), "absolutely it is short");
        Assert.Empty(RampFeasibility.OutOfReach(plan));
    }

    [Fact]
    public void Every_segment_is_reported_by_Check_whether_reachable_or_not()
    {
        RampPoint[] points =
        [
            new(0, 100, CurveKind.Linear),
            new(5, 150, CurveKind.Linear),
            new(6, 60, CurveKind.Linear),
        ];
        var plan = new TemperatureRampPlan(points, TimeSpan.Zero);

        var all = RampFeasibility.Check(plan);

        Assert.Equal(2, all.Count);
        Assert.True(all[0].IsReachable);
        Assert.False(all[1].IsReachable);
    }
}
