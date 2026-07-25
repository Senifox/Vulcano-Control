namespace Vulcano.Core.Models;

/// <summary>How the temperature travels from one ramp point to the next.</summary>
public enum CurveKind
{
    Linear,
    Exponential,
    Steep,
    EaseInOut
}

/// <summary>
/// One point of a ramp: "at <paramref name="TimeMinutes"/> minutes be at
/// <paramref name="Celsius"/>, and take <paramref name="CurveToNext"/> to get to the point after
/// this one". <paramref name="CurveToNext"/> is ignored on the last point.
///
/// Times are relative to the start of the ramp proper - the warm-up to the first point comes
/// before minute zero and is deliberately not counted.
/// </summary>
public sealed record RampPoint(int TimeMinutes, double Celsius, CurveKind CurveToNext = CurveKind.Linear);

/// <summary>A named, saveable ramp. Points must have strictly increasing times and start at 0.</summary>
public sealed class RampProfile
{
    public string Name { get; set; } = "";

    public List<RampPoint> Points { get; set; } = new();

    /// <summary>How long to hold the last point's temperature once the curve is done.</summary>
    public int HoldMinutes { get; set; }

    public RampProfile Clone() => new()
    {
        Name = Name,
        Points = Points.ToList(),
        HoldMinutes = HoldMinutes,
    };
}
