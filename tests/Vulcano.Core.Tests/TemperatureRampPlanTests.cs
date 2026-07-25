using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.Core.Tests;

public class TemperatureRampPlanTests
{
    private static readonly RampPoint[] ThreePoints =
    [
        new(0, 180, CurveKind.Linear),
        new(10, 200, CurveKind.Linear),
        new(30, 220, CurveKind.Linear),
    ];

    [Fact]
    public void Duration_is_the_last_point_and_excludes_the_hold()
    {
        var plan = new TemperatureRampPlan(ThreePoints, TimeSpan.FromMinutes(5));

        Assert.Equal(TimeSpan.FromMinutes(30), plan.Duration);
        Assert.Equal(TimeSpan.FromMinutes(5), plan.HoldDuration);
        Assert.Equal(2, plan.SegmentCount);
    }

    [Fact]
    public void Endpoints_return_their_exact_point_temperatures()
    {
        var plan = new TemperatureRampPlan(ThreePoints, TimeSpan.Zero);

        Assert.Equal(180, plan.GetTargetTemperature(TimeSpan.Zero));
        Assert.Equal(200, plan.GetTargetTemperature(TimeSpan.FromMinutes(10)));
        Assert.Equal(220, plan.GetTargetTemperature(TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void Time_outside_the_ramp_is_clamped_to_the_endpoints()
    {
        var plan = new TemperatureRampPlan(ThreePoints, TimeSpan.Zero);

        Assert.Equal(180, plan.GetTargetTemperature(TimeSpan.FromMinutes(-5)));
        Assert.Equal(220, plan.GetTargetTemperature(TimeSpan.FromMinutes(99)));
    }

    [Fact]
    public void Each_segment_interpolates_within_its_own_two_points()
    {
        var plan = new TemperatureRampPlan(ThreePoints, TimeSpan.Zero);

        // Halfway through the first segment (0-10 min, 180-200 °C) on a linear curve.
        Assert.Equal(190, plan.GetTargetTemperature(TimeSpan.FromMinutes(5)), 6);

        // Halfway through the second segment (10-30 min, 200-220 °C) - a global interpolation
        // across the whole ramp would give a different number here, which is the whole point.
        Assert.Equal(210, plan.GetTargetTemperature(TimeSpan.FromMinutes(20)), 6);
    }

    [Fact]
    public void The_curve_belongs_to_the_segment_that_starts_at_the_point()
    {
        RampPoint[] points =
        [
            new(0, 100, CurveKind.Steep),   // steep from 0 to 10
            new(10, 150, CurveKind.Linear), // linear from 10 to 20
            new(20, 200),
        ];
        var plan = new TemperatureRampPlan(points, TimeSpan.Zero);

        // Steep is t^5, so halfway through the first segment barely moved: 100 + 50 * 0.5^5.
        Assert.Equal(101.5625, plan.GetTargetTemperature(TimeSpan.FromMinutes(5)), 6);

        // The second segment is linear regardless of what the first one did.
        Assert.Equal(175, plan.GetTargetTemperature(TimeSpan.FromMinutes(15)), 6);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 0)]
    [InlineData(10, 1)]  // exactly on a point belongs to the segment starting there
    [InlineData(25, 1)]
    [InlineData(30, 1)]  // the very end stays in the last segment
    public void GetSegmentIndex_maps_a_time_to_its_segment(double minutes, int expected)
    {
        var plan = new TemperatureRampPlan(ThreePoints, TimeSpan.Zero);

        Assert.Equal(expected, plan.GetSegmentIndex(TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void GetSegmentEnd_returns_the_next_points_time()
    {
        var plan = new TemperatureRampPlan(ThreePoints, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromMinutes(10), plan.GetSegmentEnd(0));
        Assert.Equal(TimeSpan.FromMinutes(30), plan.GetSegmentEnd(1));
    }

    [Fact]
    public void An_invalid_ramp_is_refused_rather_than_silently_repaired()
    {
        RampPoint[] backwards = [new(0, 180), new(10, 200), new(5, 220)];

        Assert.Throws<ArgumentException>(() => new TemperatureRampPlan(backwards, TimeSpan.Zero));

        Assert.False(TemperatureRampPlan.TryCreate(backwards, TimeSpan.Zero, out var plan, out var errors));
        Assert.Null(plan);
        Assert.Contains(errors, e => e.Issue == RampValidationIssue.TimeNotIncreasing);
    }
}
