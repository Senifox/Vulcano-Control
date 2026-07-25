using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.Core.Tests;

public class RampCurveSamplerTests
{
    [Fact]
    public void Samples_run_from_zero_to_the_last_point()
    {
        var plan = new TemperatureRampPlan([new(0, 180), new(10, 200), new(30, 220)], TimeSpan.Zero);

        var samples = RampCurveSampler.Sample(plan);

        Assert.Equal(0, samples[0].Minutes);
        Assert.Equal(180, samples[0].Celsius);
        Assert.Equal(30, samples[^1].Minutes);
        Assert.Equal(220, samples[^1].Celsius);
    }

    [Fact]
    public void Point_boundaries_are_hit_exactly_and_only_once()
    {
        var plan = new TemperatureRampPlan([new(0, 180), new(10, 200), new(30, 220)], TimeSpan.Zero);

        var samples = RampCurveSampler.Sample(plan);

        var atBoundary = Assert.Single(samples, s => Math.Abs(s.Minutes - 10) < 1e-9);
        Assert.Equal(200, atBoundary.Celsius, 6);
    }

    [Fact]
    public void A_linear_segment_only_needs_its_endpoints()
    {
        var plan = new TemperatureRampPlan([new(0, 180, CurveKind.Linear), new(10, 200)], TimeSpan.Zero);

        var samples = RampCurveSampler.Sample(plan);

        Assert.Equal(2, samples.Count);
    }

    [Fact]
    public void A_curved_segment_gets_intermediate_samples()
    {
        var plan = new TemperatureRampPlan([new(0, 180, CurveKind.EaseInOut), new(10, 200)], TimeSpan.Zero);

        var samples = RampCurveSampler.Sample(plan, samplesPerSegment: 10);

        Assert.Equal(10, samples.Count);
        Assert.All(samples, s => Assert.InRange(s.Celsius, 180, 200));
    }

    [Fact]
    public void Every_sample_sits_on_the_plans_own_curve()
    {
        var plan = new TemperatureRampPlan(
            [new(0, 100, CurveKind.Steep), new(10, 200, CurveKind.Exponential), new(20, 150)],
            TimeSpan.Zero);

        foreach (var sample in RampCurveSampler.Sample(plan))
        {
            Assert.Equal(
                plan.GetTargetTemperature(TimeSpan.FromMinutes(sample.Minutes)),
                sample.Celsius,
                6);
        }
    }

    [Fact]
    public void The_hold_is_a_flat_tail_after_the_last_point()
    {
        var plan = new TemperatureRampPlan([new(0, 180), new(30, 220)], TimeSpan.FromMinutes(5));

        var hold = RampCurveSampler.SampleHold(plan);

        Assert.Equal(2, hold.Count);
        Assert.Equal(new RampSample(30, 220), hold[0]);
        Assert.Equal(new RampSample(35, 220), hold[1]);
    }

    [Fact]
    public void Without_a_hold_there_is_no_tail()
    {
        var plan = new TemperatureRampPlan([new(0, 180), new(30, 220)], TimeSpan.Zero);

        Assert.Empty(RampCurveSampler.SampleHold(plan));
    }
}
