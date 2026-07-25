using Vulcano.Core.Models;

namespace Vulcano.Core.Services;

/// <summary>
/// Pure, UI- and BLE-agnostic calculation of the target temperature at any point along a
/// multi-point ramp. Immutable once constructed.
///
/// The ramp is a list of time/temperature points with a curve per segment: between point i and
/// point i+1 the temperature follows point i's <see cref="RampPoint.CurveToNext"/>. Minute zero is
/// the first point - warming the device up to it happens before the clock starts and is not part
/// of <see cref="Duration"/>.
/// </summary>
public sealed class TemperatureRampPlan
{
    public IReadOnlyList<RampPoint> Points { get; }

    /// <summary>How long the last point's temperature is held after the curve is done.</summary>
    public TimeSpan HoldDuration { get; }

    /// <summary>Length of the curve itself, i.e. the last point's time. Excludes warm-up and hold.</summary>
    public TimeSpan Duration { get; }

    public double StartTemperatureCelsius => Points[0].Celsius;
    public double EndTemperatureCelsius => Points[^1].Celsius;

    /// <summary>Number of curve segments - one less than the number of points.</summary>
    public int SegmentCount => Points.Count - 1;

    public TemperatureRampPlan(IReadOnlyList<RampPoint> points, TimeSpan holdDuration)
    {
        var errors = RampValidation.Validate(points, (int)Math.Round(holdDuration.TotalMinutes));
        if (errors.Count > 0)
        {
            throw new ArgumentException(
                $"Invalid ramp: {string.Join(", ", errors.Select(e => e.Issue))}", nameof(points));
        }

        Points = points.ToArray();
        HoldDuration = holdDuration;
        Duration = TimeSpan.FromMinutes(Points[^1].TimeMinutes);
    }

    public TemperatureRampPlan(RampProfile profile)
        : this(profile.Points, TimeSpan.FromMinutes(profile.HoldMinutes))
    {
    }

    /// <summary>Creates a plan, or returns the reasons why it cannot be created.</summary>
    public static bool TryCreate(
        IReadOnlyList<RampPoint> points,
        TimeSpan holdDuration,
        out TemperatureRampPlan? plan,
        out IReadOnlyList<RampValidationError> errors)
    {
        errors = RampValidation.Validate(points, (int)Math.Round(holdDuration.TotalMinutes));
        if (errors.Count > 0)
        {
            plan = null;
            return false;
        }

        plan = new TemperatureRampPlan(points, holdDuration);
        return true;
    }

    /// <summary>
    /// Returns the target temperature (°C) at the given elapsed time.
    /// Elapsed is clamped to [0, Duration].
    /// </summary>
    public double GetTargetTemperature(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero) return StartTemperatureCelsius;
        if (elapsed >= Duration) return EndTemperatureCelsius;

        var index = GetSegmentIndex(elapsed);
        var from = Points[index];
        var to = Points[index + 1];

        var spanMinutes = (double)(to.TimeMinutes - from.TimeMinutes);
        var t = Math.Clamp((elapsed.TotalMinutes - from.TimeMinutes) / spanMinutes, 0.0, 1.0);

        return from.Celsius + (to.Celsius - from.Celsius) * Ease(t, from.CurveToNext);
    }

    /// <summary>
    /// Index of the segment the given time falls in, from 0 to <see cref="SegmentCount"/> - 1.
    /// A time exactly on a point belongs to the segment starting there; the very end of the ramp
    /// belongs to the last segment.
    /// </summary>
    public int GetSegmentIndex(TimeSpan elapsed)
    {
        var minutes = elapsed.TotalMinutes;

        for (var i = 0; i < SegmentCount; i++)
        {
            if (minutes < Points[i + 1].TimeMinutes) return i;
        }

        return SegmentCount - 1;
    }

    /// <summary>When the given segment ends - used by "skip segment" to know where to jump to.</summary>
    public TimeSpan GetSegmentEnd(int segmentIndex)
    {
        var index = Math.Clamp(segmentIndex, 0, SegmentCount - 1);
        return TimeSpan.FromMinutes(Points[index + 1].TimeMinutes);
    }

    /// <summary>True once elapsed has reached or passed <see cref="Duration"/>.</summary>
    public bool IsComplete(TimeSpan elapsed) => elapsed >= Duration;

    private static double Ease(double t, CurveKind curve) => curve switch
    {
        CurveKind.Linear => t,
        CurveKind.Exponential => Math.Pow(t, 3.0),
        CurveKind.Steep => Math.Pow(t, 5.0),
        CurveKind.EaseInOut => (3.0 * t * t) - (2.0 * t * t * t),
        _ => t
    };
}
