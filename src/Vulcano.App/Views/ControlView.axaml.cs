using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using SkiaSharp;
using Vulcano.App.ViewModels;

namespace Vulcano.App.Views;

public partial class ControlView : UserControl
{
    public ControlView()
    {
        InitializeComponent();

        // The chart paints with SkiaSharp, which knows nothing about resource dictionaries, so the
        // token colours have to be resolved here and handed over - once on load, and again whenever
        // the theme flips, since a SolidColorPaint does not follow a DynamicResource.
        ActualThemeVariantChanged += (_, _) => ApplyChartPalette();
        DataContextChanged += (_, _) => ApplyChartPalette();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        ApplyChartPalette();
    }

    private void ApplyChartPalette()
    {
        if (DataContext is not ControlViewModel viewModel) return;

        viewModel.Chart.ApplyPalette(new ChartPalette(
            Measured: ResolveColor("Info", SKColors.SteelBlue),
            Plan: ResolveColor("Accent", SKColors.Orange),
            Labels: ResolveColor("Text.Faint", SKColors.Gray),
            Separators: ResolveColor("Border", SKColors.DimGray)));
    }

    private SKColor ResolveColor(string brushKey, SKColor fallback)
    {
        if (this.TryFindResource(brushKey, ActualThemeVariant, out var value) &&
            value is ISolidColorBrush brush)
        {
            var c = brush.Color;
            return new SKColor(c.R, c.G, c.B, c.A);
        }

        return fallback;
    }
}
