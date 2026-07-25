using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Vulcano.App.Converters;

/// <summary>
/// True when the bound enum equals the name given as the parameter. Used both ways: to check the
/// right segment button and to show the matching tab, from the same single SelectedTab property.
/// Converting back only reports the parameter when a button is being checked - an unchecked button
/// must not clear the selection, or clicking the active tab would leave nothing selected.
/// </summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is string name && string.Equals(value.ToString(), name, StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is not string name) return null;

        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return enumType.IsEnum && Enum.TryParse(enumType, name, out var parsed) ? parsed : null;
    }
}
