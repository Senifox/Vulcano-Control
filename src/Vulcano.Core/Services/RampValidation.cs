using Vulcano.Core.Models;

namespace Vulcano.Core.Services;

/// <summary>What is wrong with a ramp. Deliberately a code rather than a message: the editor shows
/// these inline in the user's language, and the core has no business holding UI strings.</summary>
public enum RampValidationIssue
{
    /// <summary>A ramp needs at least a start and an end.</summary>
    TooFewPoints,

    /// <summary>The first point has to sit at minute zero - the warm-up covers everything before it.</summary>
    FirstPointNotAtZero,

    /// <summary>Times must strictly increase; two points at the same minute have no curve between them.</summary>
    TimeNotIncreasing,

    /// <summary>Outside what the device accepts.</summary>
    TemperatureOutOfRange,

    NegativeHold
}

/// <summary>An issue plus the point it belongs to, so the editor can mark the offending row.
/// <see cref="PointIndex"/> is -1 for issues about the ramp as a whole.</summary>
public readonly record struct RampValidationError(RampValidationIssue Issue, int PointIndex = -1);

public static class RampValidation
{
    /// <summary>The device's own accepted range - writing outside it is silently ignored by the
    /// hardware, which looks like the app is broken.</summary>
    public const double MinCelsius = 40.0;
    public const double MaxCelsius = 230.0;

    public static IReadOnlyList<RampValidationError> Validate(
        IReadOnlyList<RampPoint> points,
        int holdMinutes)
    {
        var errors = new List<RampValidationError>();

        if (holdMinutes < 0)
        {
            errors.Add(new RampValidationError(RampValidationIssue.NegativeHold));
        }

        if (points.Count < 2)
        {
            errors.Add(new RampValidationError(RampValidationIssue.TooFewPoints));
            return errors;
        }

        if (points[0].TimeMinutes != 0)
        {
            errors.Add(new RampValidationError(RampValidationIssue.FirstPointNotAtZero, 0));
        }

        for (var i = 0; i < points.Count; i++)
        {
            if (points[i].Celsius < MinCelsius || points[i].Celsius > MaxCelsius)
            {
                errors.Add(new RampValidationError(RampValidationIssue.TemperatureOutOfRange, i));
            }

            if (i > 0 && points[i].TimeMinutes <= points[i - 1].TimeMinutes)
            {
                errors.Add(new RampValidationError(RampValidationIssue.TimeNotIncreasing, i));
            }
        }

        return errors;
    }

    public static bool IsValid(IReadOnlyList<RampPoint> points, int holdMinutes) =>
        Validate(points, holdMinutes).Count == 0;
}
