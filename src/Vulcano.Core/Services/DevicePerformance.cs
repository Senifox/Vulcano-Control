namespace Vulcano.Core.Services;

/// <summary>
/// How fast the device actually changes temperature, measured rather than assumed.
///
/// Recorded on 2026-07-26 from a Volcano Hybrid (VH8H9H7G00) with nothing attached, standing still
/// in a room at 27 °C, by tools/Vulcano.Measure: a climb from 33 °C to 229 °C at full drive in 63
/// seconds, and a free cool-down from 230 °C that took 43 minutes to reach 50 °C.
///
/// That room temperature is part of the cooling figures, and unevenly so. What drives the cooling is
/// the difference to the room, so at 210 °C a seven-degree-cooler room is a four per cent change and
/// makes no odds - but at 45 °C the same seven degrees is nearly forty per cent, and the device would
/// cool noticeably faster. The bands below about 85 °C are therefore the soft ones; everything above
/// is close to room-independent. Nothing here compensates for that, because the app has no way to
/// know how warm the room is: the device reports no temperature at all when it is cold and idle.
///
/// A single Newton exponential towards 27 °C fits at about 19 minutes but only to R² 0.97, with
/// visible systematic curvature - radiation carries the top of the range and convection the bottom.
/// Hence a measured table rather than one constant.
///
/// These describe the device, not a promise: the estimates built on them are for warning somebody
/// that a segment is asking the impossible, not for predicting a run to the second.
/// </summary>
public static class DevicePerformance
{
    /// <summary>
    /// Heating with the target set far above, in K per minute at that temperature. The last entry
    /// looks like a collapse and is one: it was measured while the device converged on its target,
    /// where the controller throttles on purpose. A ramp passing through 225 °C on its way somewhere
    /// higher does better than this says, so estimates near the top are pessimistic.
    /// </summary>
    private static readonly (double Celsius, double RatePerMinute)[] Heating =
    [
        (50, 250),
        (70, 246),
        (90, 232),
        (110, 215),
        (130, 220),
        (150, 207),
        (170, 210),
        (190, 195),
        (210, 172),
        (225, 69),
    ];

    /// <summary>
    /// Cooling with the heater off, in K per minute at that temperature. Strongly temperature
    /// dependent - 27 K/min at 210 °C against 1 K/min at 55 °C - because the device has no active
    /// cooling and loses heat only as fast as the difference to the room allows.
    ///
    /// The band just below 230 measured slower than the one under it, which is the block still
    /// equalising in the first seconds after the heater went off rather than a real slowdown; the
    /// table therefore ends at the highest rate rather than following that dip.
    /// </summary>
    private static readonly (double Celsius, double RatePerMinute)[] Cooling =
    [
        (45, 0.8),
        (55, 1.0),
        (65, 1.5),
        (75, 2.0),
        (85, 2.7),
        (110, 4.4),
        (130, 6.3),
        (150, 8.8),
        (170, 12.0),
        (190, 17.5),
        (210, 26.7),
    ];

    /// <summary>Fastest the device climbs through this temperature, K per minute.</summary>
    public static double HeatingRatePerMinute(double celsius) => Interpolate(Heating, celsius);

    /// <summary>How fast it falls through this temperature with the heater off, K per minute.</summary>
    public static double CoolingRatePerMinute(double celsius) => Interpolate(Cooling, celsius);

    /// <summary>
    /// Roughly how long the device needs to get from one temperature to another, in either
    /// direction. Integrated in small steps because the rate changes a lot across the range - a
    /// single average would be wrong at both ends.
    /// </summary>
    public static TimeSpan EstimateDuration(double fromCelsius, double toCelsius)
    {
        if (Math.Abs(toCelsius - fromCelsius) < 0.01) return TimeSpan.Zero;

        const double step = 0.5;
        var heating = toCelsius > fromCelsius;
        var minutes = 0.0;

        for (var t = Math.Min(fromCelsius, toCelsius); t < Math.Max(fromCelsius, toCelsius); t += step)
        {
            var rate = heating ? HeatingRatePerMinute(t + step / 2) : CoolingRatePerMinute(t + step / 2);
            if (rate <= 0) return TimeSpan.MaxValue;

            minutes += step / rate;
        }

        return TimeSpan.FromMinutes(minutes);
    }

    /// <summary>Linear between the measured points, flat beyond the ends - there is no data out
    /// there, and inventing a trend would be worse than repeating the nearest thing we saw.</summary>
    private static double Interpolate((double Celsius, double RatePerMinute)[] table, double celsius)
    {
        if (celsius <= table[0].Celsius) return table[0].RatePerMinute;
        if (celsius >= table[^1].Celsius) return table[^1].RatePerMinute;

        for (var i = 1; i < table.Length; i++)
        {
            if (celsius > table[i].Celsius) continue;

            var (lowC, lowRate) = table[i - 1];
            var (highC, highRate) = table[i];
            var position = (celsius - lowC) / (highC - lowC);

            return lowRate + position * (highRate - lowRate);
        }

        return table[^1].RatePerMinute;
    }
}
