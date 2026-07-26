using System.Globalization;

namespace Vulcano.Core.Services;

/// <summary>
/// The two formatting rules the design is strict about, in one place so they cannot drift apart
/// between views: durations never show a leading "00:", and a value and its unit are joined by a
/// narrow no-break space rather than a plain one.
/// </summary>
public static class Formatting
{
    /// <summary>
    /// U+202F. Keeps "195 °C" from breaking across a line and sets the unit slightly tighter than
    /// a word space would.
    /// </summary>
    public const char NarrowNoBreakSpace = ' ';

    /// <summary>
    /// <c>m:ss</c> below an hour, <c>h:mm:ss</c> above it. Never <c>00:06:12</c> - the leading
    /// zeros read as a stopwatch that has not started.
    /// </summary>
    public static string Duration(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;

        var totalHours = (int)value.TotalHours;

        return totalHours > 0
            ? $"{totalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
    }

    public static string Duration(int seconds) => Duration(TimeSpan.FromSeconds(seconds));

    /// <summary>A value and its unit, e.g. <c>195 °C</c> or <c>40 min</c>.</summary>
    public static string WithUnit(string value, string unit) => $"{value}{NarrowNoBreakSpace}{unit}";

    /// <summary>Whole degrees - the device's own resolution is finer, but nobody reads 193.4.</summary>
    public static string Celsius(double value) =>
        WithUnit(Math.Round(value).ToString("0", CultureInfo.CurrentCulture), "°C");

    /// <summary>A temperature difference. Signed, because the sign is the point: <c>+2 K</c>.</summary>
    public static string Kelvin(double delta) =>
        WithUnit(Math.Round(delta).ToString("+0;-0;0", CultureInfo.CurrentCulture), "K");

    public static string Minutes(int value) =>
        WithUnit(value.ToString(CultureInfo.CurrentCulture), "min");
}
