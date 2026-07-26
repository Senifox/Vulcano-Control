namespace Vulcano.Core.Services;

/// <summary>
/// One segment measured against what the device can do.
/// <paramref name="Needed"/> is how long it would take at full drive, or falling freely; the segment
/// is out of reach when that is longer than the time the ramp gives it.
/// </summary>
public readonly record struct SegmentFeasibility(
    int SegmentNumber,
    double FromCelsius,
    double ToCelsius,
    TimeSpan Allowed,
    TimeSpan Needed)
{
    public bool IsReachable => Needed <= Allowed;

    /// <summary>True for a segment that asks the device to lose heat, which is where ramps usually
    /// come unstuck: there is no active cooling, so falling takes minutes where climbing takes
    /// seconds.</summary>
    public bool IsCooling => ToCelsius < FromCelsius;
}

/// <summary>
/// Checks a ramp against <see cref="DevicePerformance"/> and reports which segments the device could
/// not follow.
///
/// Advisory on purpose, and separate from <see cref="RampValidation"/> for that reason: validation
/// says what cannot be built, this says what will not happen as drawn. The numbers behind it are
/// measurements from one device in one room, so refusing to start a ramp on their say-so would
/// eventually block a run that was perfectly fine. A ramp that asks for too much still runs - the
/// device simply arrives late, which for a climb is often exactly what somebody meant by drawing it
/// steep.
/// </summary>
public static class RampFeasibility
{
    /// <summary>
    /// A segment is only worth mentioning when it is properly out of reach rather than a few seconds
    /// short - fifteen per cent over is within the spread of the measurements themselves.
    /// </summary>
    private const double Tolerance = 1.15;

    /// <summary>
    /// And it has to be short by something worth saying out loud. Without this a one-minute segment
    /// twelve seconds beyond the device produced "would need about 1 min instead of 1 min", which is
    /// both nonsense to read and not worth interrupting anybody for.
    /// </summary>
    private static readonly TimeSpan WorthMentioning = TimeSpan.FromSeconds(30);

    public static IReadOnlyList<SegmentFeasibility> Check(TemperatureRampPlan plan)
    {
        var results = new List<SegmentFeasibility>(plan.SegmentCount);

        for (var i = 0; i < plan.SegmentCount; i++)
        {
            var from = plan.Points[i];
            var to = plan.Points[i + 1];

            results.Add(new SegmentFeasibility(
                SegmentNumber: i + 1,
                FromCelsius: from.Celsius,
                ToCelsius: to.Celsius,
                Allowed: TimeSpan.FromMinutes(to.TimeMinutes - from.TimeMinutes),
                Needed: DevicePerformance.EstimateDuration(from.Celsius, to.Celsius)));
        }

        return results;
    }

    /// <summary>The segments worth warning about, in the order they run.</summary>
    public static IReadOnlyList<SegmentFeasibility> OutOfReach(TemperatureRampPlan plan) =>
        Check(plan)
            .Where(s => s.Needed > s.Allowed * Tolerance && s.Needed - s.Allowed >= WorthMentioning)
            .ToArray();
}
