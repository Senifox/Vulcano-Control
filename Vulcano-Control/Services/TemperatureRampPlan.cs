using Vulcano_Control.Models;

namespace Vulcano_Control.Services;

/// <summary>
/// Pure, UI- and BLE-agnostic calculation of the target temperature at any point
/// along a temperature ramp. Immutable once constructed.
/// </summary>
public sealed class TemperatureRampPlan
{
    public double StartTemperatureCelsius { get; }
    public double EndTemperatureCelsius { get; }
    public TimeSpan Duration { get; }
    public InterpolationMethod Method { get; }

    public TemperatureRampPlan(
        double startTemperatureCelsius,
        double endTemperatureCelsius,
        TimeSpan duration,
        InterpolationMethod method)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        StartTemperatureCelsius = startTemperatureCelsius;
        EndTemperatureCelsius = endTemperatureCelsius;
        Duration = duration;
        Method = method;
    }

    /// <summary>
    /// Returns the target temperature (°C) at the given elapsed time.
    /// Elapsed is clamped to [0, Duration].
    /// </summary>
    public double GetTargetTemperature(TimeSpan elapsed)
    {
        var t = Math.Clamp(elapsed.TotalSeconds / Duration.TotalSeconds, 0.0, 1.0);
        var eased = Ease(t, Method);
        return StartTemperatureCelsius + (EndTemperatureCelsius - StartTemperatureCelsius) * eased;
    }

    /// <summary>True once elapsed has reached or passed Duration.</summary>
    public bool IsComplete(TimeSpan elapsed) => elapsed >= Duration;

    private static double Ease(double t, InterpolationMethod method) => method switch
    {
        InterpolationMethod.Linear => t,
        InterpolationMethod.Exponential => Math.Pow(t, 3.0),
        InterpolationMethod.EaseInOut => (3.0 * t * t) - (2.0 * t * t * t),
        _ => t
    };
}
