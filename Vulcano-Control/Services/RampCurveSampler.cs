namespace Vulcano_Control.Services;

/// <summary>
/// Samples a <see cref="TemperatureRampPlan"/> at evenly spaced points across its full
/// duration, for charting the theoretical (Soll) temperature curve. Pure and UI-agnostic.
/// </summary>
public static class RampCurveSampler
{
    public const int DefaultSampleCount = 80;

    /// <summary>Returns (elapsedMinutes, targetCelsius) samples from t=0 to t=Duration inclusive.</summary>
    public static IReadOnlyList<(double Minutes, double Celsius)> Sample(
        TemperatureRampPlan plan, int sampleCount = DefaultSampleCount)
    {
        if (sampleCount < 2) sampleCount = 2;

        var points = new List<(double, double)>(sampleCount);
        var totalMinutes = plan.Duration.TotalMinutes;

        for (var i = 0; i < sampleCount; i++)
        {
            var fraction = i / (double)(sampleCount - 1);
            var elapsed = TimeSpan.FromMinutes(totalMinutes * fraction);
            points.Add((elapsed.TotalMinutes, plan.GetTargetTemperature(elapsed)));
        }

        return points;
    }
}
