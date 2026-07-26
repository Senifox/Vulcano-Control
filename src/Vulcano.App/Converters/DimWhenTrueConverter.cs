using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Vulcano.App.Converters;

/// <summary>
/// Fades a block out without disabling it. Used for the parts of the Device tab that belong to the
/// host rather than to us when running as a relay client: they are still true, just not ours.
/// </summary>
public sealed class DimWhenTrueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 0.55 : 1.0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
