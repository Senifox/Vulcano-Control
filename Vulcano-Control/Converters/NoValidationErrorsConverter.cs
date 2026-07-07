using System.Globalization;
using System.Windows.Data;

namespace Vulcano_Control.Converters;

/// <summary>
/// Combines several <c>Validation.HasError</c> flags (bound via MultiBinding) into a single
/// bool - true only if none of them are set. Used to also gate a command button on a textbox
/// simply being empty/unparsable, which never even reaches the bound ViewModel property (and
/// so isn't visible to the command's own CanExecute check) since WPF's binding engine drops
/// values it can't convert instead of pushing them to the source.
/// </summary>
public sealed class NoValidationErrorsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        !values.Any(v => v is true);

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
