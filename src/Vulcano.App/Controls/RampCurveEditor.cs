using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Vulcano.App.ViewModels;
using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.App.Controls;

/// <summary>
/// The ramp drawn as a curve you can edit: rings on the points, a dashed tail for the hold, the
/// curve method written on each segment, a ghost point where a click would insert one.
///
/// Drawn by hand rather than through the charting library. Everything here is bespoke - ring radii,
/// the dashed hold, per-segment labels, drag and insert hit-testing - and none of it is what a
/// chart control is for. The read-only chart on the Control tab is LiveCharts; this is not a chart.
/// </summary>
public class RampCurveEditor : Control
{
    private const double PadLeft = 46;
    private const double PadRight = 14;
    private const double PadTop = 12;
    private const double PadBottom = 24;

    private const double PointRadius = 7;
    private const double SelectedPointRadius = 8;
    private const double GrabRadius = 12;

    /// <summary>How much headroom to leave above and below the ramp so the curve is not glued to
    /// the frame.</summary>
    private const double RangePaddingCelsius = 15;

    public static readonly StyledProperty<ObservableCollection<RampPointViewModel>?> PointsProperty =
        AvaloniaProperty.Register<RampCurveEditor, ObservableCollection<RampPointViewModel>?>(nameof(Points));

    public static readonly StyledProperty<int> HoldMinutesProperty =
        AvaloniaProperty.Register<RampCurveEditor, int>(nameof(HoldMinutes));

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<RampCurveEditor, int>(
            nameof(SelectedIndex), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    private int _dragIndex = -1;
    private double? _ghostMinutes;

    static RampCurveEditor()
    {
        AffectsRender<RampCurveEditor>(PointsProperty, HoldMinutesProperty, SelectedIndexProperty);
    }

    public RampCurveEditor()
    {
        ClipToBounds = true;
    }

    public ObservableCollection<RampPointViewModel>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public int HoldMinutes
    {
        get => GetValue(HoldMinutesProperty);
        set => SetValue(HoldMinutesProperty, value);
    }

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != PointsProperty) return;

        if (change.OldValue is ObservableCollection<RampPointViewModel> old)
        {
            old.CollectionChanged -= OnPointsCollectionChanged;
            foreach (var point in old) point.PropertyChanged -= OnPointChanged;
        }

        if (change.NewValue is ObservableCollection<RampPointViewModel> added)
        {
            added.CollectionChanged += OnPointsCollectionChanged;
            foreach (var point in added) point.PropertyChanged += OnPointChanged;
        }
    }

    private void OnPointsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var point in e.OldItems?.OfType<RampPointViewModel>() ?? []) point.PropertyChanged -= OnPointChanged;
        foreach (var point in e.NewItems?.OfType<RampPointViewModel>() ?? []) point.PropertyChanged += OnPointChanged;
        InvalidateVisual();
    }

    private void OnPointChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    // --- Interaction ---

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (Points is not { Count: > 1 } points) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var position = e.GetPosition(this);
        var hit = FindPointAt(position);

        if (hit >= 0)
        {
            SelectedIndex = hit;
            _dragIndex = hit;
            e.Pointer.Capture(this);
            InvalidateVisual();
            return;
        }

        // Not on a point: if the click is near the curve, that is a request for a point there.
        if (_ghostMinutes is { } minutes)
        {
            InsertPointAt(minutes);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (Points is not { Count: > 1 }) return;

        var position = e.GetPosition(this);

        if (_dragIndex >= 0)
        {
            DragTo(position);
            return;
        }

        var previousGhost = _ghostMinutes;
        _ghostMinutes = FindPointAt(position) >= 0 ? null : GhostMinutesNear(position);

        Cursor = new Cursor(_ghostMinutes is not null || FindPointAt(position) >= 0
            ? StandardCursorType.Hand
            : StandardCursorType.Arrow);

        if (previousGhost != _ghostMinutes) InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_dragIndex < 0) return;

        _dragIndex = -1;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _ghostMinutes = null;
        InvalidateVisual();
    }

    private void DragTo(Point position)
    {
        if (Points is not { } points || _dragIndex < 0 || _dragIndex >= points.Count) return;

        var layout = CreateLayout(points);
        var point = points[_dragIndex];

        var celsius = Math.Round(layout.ToCelsius(position.Y));
        point.Celsius = Math.Clamp(celsius, RampValidation.MinCelsius, RampValidation.MaxCelsius);

        // The first point defines minute zero; moving it in time would only shift the whole ramp.
        if (_dragIndex > 0)
        {
            var minutes = (int)Math.Round(layout.ToMinutes(position.X));
            var lower = points[_dragIndex - 1].TimeMinutes + 1;
            var upper = _dragIndex < points.Count - 1 ? points[_dragIndex + 1].TimeMinutes - 1 : int.MaxValue;
            point.TimeMinutes = Math.Clamp(minutes, lower, Math.Max(lower, upper));
        }

        InvalidateVisual();
    }

    private void InsertPointAt(double minutes)
    {
        if (Points is not { Count: > 1 } points) return;

        var index = 0;
        while (index < points.Count - 1 && points[index + 1].TimeMinutes < minutes) index++;

        var whole = (int)Math.Round(minutes);
        if (whole <= points[index].TimeMinutes || whole >= points[index + 1].TimeMinutes) return;

        var plan = BuildPlan(points);
        var celsius = plan?.GetTargetTemperature(TimeSpan.FromMinutes(whole)) ?? points[index].Celsius;

        points.Insert(index + 1, new RampPointViewModel(
            new RampPoint(whole, Math.Round(celsius), points[index].CurveToNext)));

        SelectedIndex = index + 1;
        _ghostMinutes = null;
    }

    private int FindPointAt(Point position)
    {
        if (Points is not { Count: > 0 } points) return -1;

        var layout = CreateLayout(points);

        for (var i = 0; i < points.Count; i++)
        {
            var centre = layout.ToPixels(points[i].TimeMinutes, points[i].Celsius);
            if (Distance(centre, position) <= GrabRadius) return i;
        }

        return -1;
    }

    /// <summary>The time under the cursor if it is close enough to the curve to mean "insert here",
    /// otherwise null.</summary>
    private double? GhostMinutesNear(Point position)
    {
        if (Points is not { Count: > 1 } points) return null;
        if (BuildPlan(points) is not { } plan) return null;

        var layout = CreateLayout(points);
        if (position.X < layout.PlotLeft || position.X > layout.PlotRight) return null;

        var minutes = layout.ToMinutes(position.X);
        if (minutes <= 0 || minutes >= plan.Duration.TotalMinutes) return null;

        var celsius = plan.GetTargetTemperature(TimeSpan.FromMinutes(minutes));
        var onCurve = layout.ToPixels(minutes, celsius);

        return Math.Abs(onCurve.Y - position.Y) <= 10 ? minutes : null;
    }

    private static double Distance(Point a, Point b) =>
        Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static TemperatureRampPlan? BuildPlan(ObservableCollection<RampPointViewModel> points) =>
        TemperatureRampPlan.TryCreate(
            points.Select(p => p.ToPoint()).ToList(), TimeSpan.Zero, out var plan, out _)
            ? plan
            : null;

    // --- Drawing ---

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (Points is not { Count: > 1 } points) return;

        var layout = CreateLayout(points);
        var palette = ResolvePalette();

        DrawGrid(context, layout, palette);

        var plan = BuildPlan(points);
        if (plan is not null)
        {
            DrawCurve(context, layout, plan, palette);
            DrawHold(context, layout, points, palette);
            DrawSegmentLabels(context, layout, points, palette);
        }

        DrawGhost(context, layout, plan, palette);
        DrawPoints(context, layout, points, palette);
    }

    private void DrawGrid(DrawingContext context, Layout layout, Palette palette)
    {
        var pen = new Pen(palette.Grid, 1);

        // Four horizontal rules plus their temperature labels; enough to read a value off, few
        // enough not to compete with the curve.
        for (var i = 0; i <= 4; i++)
        {
            var celsius = layout.MinCelsius + ((layout.MaxCelsius - layout.MinCelsius) * i / 4.0);
            var y = layout.ToPixels(0, celsius).Y;
            context.DrawLine(pen, new Point(layout.PlotLeft, y), new Point(layout.PlotRight, y));

            var text = FormatText(Math.Round(celsius).ToString("0", CultureInfo.CurrentCulture), palette, 10);
            context.DrawText(text, new Point(layout.PlotLeft - text.Width - 8, y - (text.Height / 2)));
        }
    }

    private void DrawCurve(DrawingContext context, Layout layout, TemperatureRampPlan plan, Palette palette)
    {
        var samples = RampCurveSampler.Sample(plan);
        if (samples.Count < 2) return;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(layout.ToPixels(samples[0].Minutes, samples[0].Celsius), false);
            foreach (var sample in samples.Skip(1))
            {
                ctx.LineTo(layout.ToPixels(sample.Minutes, sample.Celsius));
            }
            ctx.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(palette.Accent, 2.5) { LineJoin = PenLineJoin.Round }, geometry);
    }

    private void DrawHold(
        DrawingContext context, Layout layout, ObservableCollection<RampPointViewModel> points, Palette palette)
    {
        if (HoldMinutes <= 0) return;

        var last = points[^1];
        var from = layout.ToPixels(last.TimeMinutes, last.Celsius);
        var to = layout.ToPixels(last.TimeMinutes + HoldMinutes, last.Celsius);

        var pen = new Pen(palette.Accent, 2.5)
        {
            DashStyle = new DashStyle([5, 4], 0),
        };

        context.DrawLine(pen, from, to);

        var text = FormatText($"hold {Formatting.Minutes(HoldMinutes)}", palette, 10);
        context.DrawText(text, new Point(((from.X + to.X) / 2) - (text.Width / 2), from.Y - text.Height - 8));
    }

    private void DrawSegmentLabels(
        DrawingContext context, Layout layout, ObservableCollection<RampPointViewModel> points, Palette palette)
    {
        for (var i = 0; i < points.Count - 1; i++)
        {
            var from = points[i];
            var to = points[i + 1];

            var midMinutes = (from.TimeMinutes + to.TimeMinutes) / 2.0;
            var midCelsius = (from.Celsius + to.Celsius) / 2.0;
            var centre = layout.ToPixels(midMinutes, midCelsius);

            var text = FormatText(CurveName(from.CurveToNext), palette, 10);
            var origin = new Point(centre.X - (text.Width / 2), centre.Y + 10);

            // A pill behind the label, so it stays readable where it crosses the curve.
            var background = new Rect(origin.X - 6, origin.Y - 2, text.Width + 12, text.Height + 4);
            context.DrawRectangle(palette.LabelBackground, null, background, 999, 999);
            context.DrawText(text, origin);
        }
    }

    private void DrawGhost(DrawingContext context, Layout layout, TemperatureRampPlan? plan, Palette palette)
    {
        if (_ghostMinutes is not { } minutes || plan is null) return;

        var celsius = plan.GetTargetTemperature(TimeSpan.FromMinutes(minutes));
        var centre = layout.ToPixels(minutes, celsius);

        var pen = new Pen(palette.Accent, 1.5)
        {
            DashStyle = new DashStyle([3, 3], 0),
        };

        context.DrawEllipse(null, pen, centre, PointRadius, PointRadius);
    }

    private void DrawPoints(
        DrawingContext context, Layout layout, ObservableCollection<RampPointViewModel> points, Palette palette)
    {
        for (var i = 0; i < points.Count; i++)
        {
            var centre = layout.ToPixels(points[i].TimeMinutes, points[i].Celsius);
            var isSelected = i == SelectedIndex;
            var stroke = points[i].HasError ? palette.Error : palette.Accent;

            if (isSelected)
            {
                context.DrawEllipse(stroke, new Pen(palette.Text, 2), centre, SelectedPointRadius, SelectedPointRadius);
            }
            else
            {
                context.DrawEllipse(palette.Surface, new Pen(stroke, 2.5), centre, PointRadius, PointRadius);
            }

            var text = FormatText(Formatting.Minutes(points[i].TimeMinutes), palette, 10);
            context.DrawText(text, new Point(centre.X - (text.Width / 2), layout.PlotBottom + 6));
        }
    }

    private static string CurveName(CurveKind curve) => curve switch
    {
        CurveKind.Exponential => "exponential",
        CurveKind.Steep => "steep",
        CurveKind.EaseInOut => "ease-in-out",
        _ => "linear",
    };

    private FormattedText FormatText(string text, Palette palette, double size) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(palette.MonoFont), size, palette.Faint);

    // --- Layout and palette ---

    private readonly record struct Layout(
        double PlotLeft, double PlotTop, double PlotRight, double PlotBottom,
        double TotalMinutes, double MinCelsius, double MaxCelsius)
    {
        public Point ToPixels(double minutes, double celsius)
        {
            var x = PlotLeft + ((PlotRight - PlotLeft) * (TotalMinutes <= 0 ? 0 : minutes / TotalMinutes));
            var span = MaxCelsius - MinCelsius;
            var y = PlotBottom - ((PlotBottom - PlotTop) * (span <= 0 ? 0 : (celsius - MinCelsius) / span));
            return new Point(x, y);
        }

        public double ToMinutes(double x) =>
            (x - PlotLeft) / Math.Max(PlotRight - PlotLeft, 1) * TotalMinutes;

        public double ToCelsius(double y) =>
            MinCelsius + ((PlotBottom - y) / Math.Max(PlotBottom - PlotTop, 1) * (MaxCelsius - MinCelsius));
    }

    private Layout CreateLayout(ObservableCollection<RampPointViewModel> points)
    {
        var min = points.Min(p => p.Celsius) - RangePaddingCelsius;
        var max = points.Max(p => p.Celsius) + RangePaddingCelsius;

        // Clamped to what the device accepts, so the axis never offers a temperature that could
        // not be set anyway.
        return new Layout(
            PadLeft, PadTop, Math.Max(Bounds.Width - PadRight, PadLeft + 1), Math.Max(Bounds.Height - PadBottom, PadTop + 1),
            Math.Max(points[^1].TimeMinutes + HoldMinutes, 1),
            Math.Max(min, RampValidation.MinCelsius),
            Math.Min(max, RampValidation.MaxCelsius));
    }

    private readonly record struct Palette(
        IBrush Accent, IBrush Text, IBrush Faint, IBrush Grid, IBrush Surface, IBrush Error,
        IBrush LabelBackground, FontFamily MonoFont);

    private Palette ResolvePalette() => new(
        Brush("Accent", Brushes.Orange),
        Brush("Text", Brushes.White),
        Brush("Text.Faint", Brushes.Gray),
        Brush("Border", Brushes.DimGray),
        Brush("Bg.Deep", Brushes.Black),
        Brush("Error", Brushes.Red),
        Brush("Bg.Panel", Brushes.Black),
        this.TryFindResource("Font.Mono", out var font) && font is FontFamily family
            ? family
            : FontFamily.Default);

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;
}
