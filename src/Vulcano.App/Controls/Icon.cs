using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Vulcano.App.Controls;

/// <summary>
/// A Lucide outline icon. Lucide draws with strokes rather than fills, so this is a stroked
/// <see cref="Avalonia.Controls.Shapes.Path"/> rather than a PathIcon, and it takes its colour from
/// <see cref="TemplatedControl.Foreground"/> - which means an icon inside a button simply inherits
/// whatever that button's text colour is, in either theme, without a second set of resources.
/// </summary>
public class Icon : TemplatedControl
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    /// <summary>Stroke width on the 24x24 grid the geometries are drawn on. The design asks for
    /// 1.7; Lucide ships 2.</summary>
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(StrokeThickness), 1.7);

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }
}
