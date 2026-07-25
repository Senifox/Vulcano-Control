namespace Vulcano.Core.Services;

/// <summary>One charted point of a planned ramp.</summary>
public readonly record struct RampSample(double Minutes, double Celsius);

/// <summary>
/// Turns a <see cref="TemperatureRampPlan"/> into points for the chart. Samples per segment rather
/// than across the whole ramp, so every point sits exactly on its own curve and the segment
/// boundaries land on the plan's points instead of near them. Pure and UI-agnostic.
/// </summary>
public static class RampCurveSampler
{
    public const int DefaultSamplesPerSegment = 24;

    /// <summary>
    /// Samples the curve from minute 0 to the last point, inclusive. A linear segment contributes
    /// just its two endpoints - there is nothing in between a straight line needs.
    /// </summary>
    public static IReadOnlyList<RampSample> Sample(
        TemperatureRampPlan plan, int samplesPerSegment = DefaultSamplesPerSegment)
    {
        if (samplesPerSegment < 2) samplesPerSegment = 2;

        var samples = new List<RampSample>(plan.SegmentCount * samplesPerSegment);

        for (var segment = 0; segment < plan.SegmentCount; segment++)
        {
            var from = plan.Points[segment];
            var to = plan.Points[segment + 1];
            var steps = from.CurveToNext == Models.CurveKind.Linear ? 1 : samplesPerSegment - 1;

            // Skip each segment's first point except on the very first segment: it is the previous
            // segment's last point and would otherwise be charted twice.
            var start = segment == 0 ? 0 : 1;

            for (var step = start; step <= steps; step++)
            {
                var minutes = from.TimeMinutes + (to.TimeMinutes - from.TimeMinutes) * (step / (double)steps);
                samples.Add(new RampSample(minutes, plan.GetTargetTemperature(TimeSpan.FromMinutes(minutes))));
            }
        }

        return samples;
    }

    /// <summary>
    /// The flat tail after the last point, drawn dashed in the editor. Empty when nothing is held.
    /// </summary>
    public static IReadOnlyList<RampSample> SampleHold(TemperatureRampPlan plan)
    {
        if (plan.HoldDuration <= TimeSpan.Zero) return Array.Empty<RampSample>();

        var end = plan.Duration.TotalMinutes;
        return new[]
        {
            new RampSample(end, plan.EndTemperatureCelsius),
            new RampSample(end + plan.HoldDuration.TotalMinutes, plan.EndTemperatureCelsius),
        };
    }
}
