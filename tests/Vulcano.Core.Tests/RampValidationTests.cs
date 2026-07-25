using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.Core.Tests;

public class RampValidationTests
{
    [Fact]
    public void A_two_point_ramp_starting_at_zero_is_valid()
    {
        RampPoint[] points = [new(0, 185), new(40, 225)];

        Assert.True(RampValidation.IsValid(points, holdMinutes: 5));
    }

    [Fact]
    public void A_single_point_is_not_a_ramp()
    {
        var errors = RampValidation.Validate([new(0, 185)], 0);

        Assert.Contains(errors, e => e.Issue == RampValidationIssue.TooFewPoints);
    }

    [Fact]
    public void The_first_point_has_to_sit_at_minute_zero()
    {
        var errors = RampValidation.Validate([new(5, 185), new(40, 225)], 0);

        Assert.Contains(errors, e => e.Issue == RampValidationIssue.FirstPointNotAtZero && e.PointIndex == 0);
    }

    [Fact]
    public void Two_points_at_the_same_minute_are_refused()
    {
        var errors = RampValidation.Validate([new(0, 185), new(10, 200), new(10, 220)], 0);

        Assert.Contains(errors, e => e.Issue == RampValidationIssue.TimeNotIncreasing && e.PointIndex == 2);
    }

    [Theory]
    [InlineData(39.9)]
    [InlineData(230.1)]
    public void Temperatures_outside_the_devices_range_are_refused(double celsius)
    {
        var errors = RampValidation.Validate([new(0, 185), new(40, celsius)], 0);

        Assert.Contains(errors, e => e.Issue == RampValidationIssue.TemperatureOutOfRange && e.PointIndex == 1);
    }

    [Theory]
    [InlineData(40.0)]
    [InlineData(230.0)]
    public void The_range_bounds_themselves_are_allowed(double celsius)
    {
        Assert.True(RampValidation.IsValid([new(0, 185), new(40, celsius)], 0));
    }

    [Fact]
    public void A_negative_hold_is_refused()
    {
        var errors = RampValidation.Validate([new(0, 185), new(40, 225)], -1);

        Assert.Contains(errors, e => e.Issue == RampValidationIssue.NegativeHold);
    }
}
