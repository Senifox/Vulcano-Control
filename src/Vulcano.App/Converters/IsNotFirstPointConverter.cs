using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Vulcano.App.Converters;

/// <summary>
/// False for the first point of a ramp. Its time is what minute zero means, so it is the one value
/// in the table that cannot be edited - moving it would shift the whole ramp rather than the point.
/// </summary>
public sealed class IsNotFirstPointConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int number && number > 1;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
