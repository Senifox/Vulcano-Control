using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Vulcano.App.Converters;

/// <summary>
/// A double shown without decimals. Every number the user adjusts in this app - degrees, minutes,
/// kelvin - is a whole one, and "195,0 °C" suggests a precision the device does not offer.
/// </summary>
public sealed class WholeNumberConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d ? Math.Round(d).ToString("0", culture) : value?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        double.TryParse(value?.ToString(), NumberStyles.Any, culture, out var parsed) ? parsed : null;
}
