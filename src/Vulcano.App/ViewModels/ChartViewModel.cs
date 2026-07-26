using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.App.ViewModels;

/// <summary>
/// The colours the chart draws with. Passed in from the view rather than read here, because they
/// are theme resources and only a control can resolve those - and they change under us when the
/// desktop flips between light and dark.
/// </summary>
public readonly record struct ChartPalette(
    SKColor Measured,
    SKColor Plan,
    SKColor Labels,
    SKColor Separators);

/// <summary>
/// The temperature chart: what the device actually reported, and what a running ramp asked for.
/// Replaces the OxyPlot setup that used to live inside MainViewModel, tracker workaround included.
/// </summary>
public partial class ChartViewModel : ObservableObject, IDisposable
{
    private readonly VolcanoDeviceOrchestrator _device;
    private readonly AppSettings _settings;

    private readonly ObservableCollection<DateTimePoint> _measured = new();
    private readonly ObservableCollection<DateTimePoint> _plan = new();

    private readonly LineSeries<DateTimePoint> _measuredSeries;
    private readonly LineSeries<DateTimePoint> _planSeries;
    private readonly DateTimeAxis _xAxis;
    private readonly Axis _yAxis;

    public ChartViewModel(VolcanoDeviceOrchestrator device, AppSettings settings)
    {
        _device = device;
        _settings = settings;

        _measuredSeries = new LineSeries<DateTimePoint>
        {
            Name = Strings.Get("Chart.Measured"),
            Values = _measured,
            Fill = null,
            // All three: GeometrySize alone still leaves a marker outline at every sample, and at
            // one sample a second there are a lot of them.
            GeometrySize = 0,
            GeometryFill = null,
            GeometryStroke = null,
            LineSmoothness = 0.2,
        };

        _planSeries = new LineSeries<DateTimePoint>
        {
            Name = Strings.Get("Chart.Plan"),
            Values = _plan,
            Fill = null,
            // All three: GeometrySize alone still leaves a marker outline at every sample, and at
            // one sample a second there are a lot of them.
            GeometrySize = 0,
            GeometryFill = null,
            GeometryStroke = null,
            LineSmoothness = 0,
        };

        // DateTimeAxis rather than a plain Axis with a hand-written labeler: the axis range is
        // padded past the data, and turning a raw tick value from outside DateTime's range into a
        // DateTime throws inside the draw - which shows up as a chart that renders nothing at all
        // rather than as an error.
        _xAxis = new DateTimeAxis(TimeSpan.FromMinutes(1), date => date.ToString("HH:mm"))
        {
            TextSize = 11,
        };

        _yAxis = new Axis
        {
            Labeler = value => value.ToString("0"),
            MinStep = 10,
            TextSize = 11,
        };

        Series = [_measuredSeries, _planSeries];
        XAxes = [_xAxis];
        YAxes = [_yAxis];

        _device.CurrentTemperatureChanged += OnCurrentTemperatureChanged;
        _device.ProgressChanged += OnRampProgressChanged;
        _device.Completed += OnRampEnded;
        _device.Stopped += OnRampEnded;
    }

    public ISeries[] Series { get; }

    public Axis[] XAxes { get; }

    public Axis[] YAxes { get; }

    /// <summary>How far back the chart reaches, so the empty stretch on the left is explained.</summary>
    public string HistoryNote => Strings.Get("Chart.History", Formatting.Minutes(_settings.HistoryRetentionMinutes));

    /// <summary>
    /// False until the first reading arrives. Worth a property of its own: a time axis with no data
    /// to bound it invents a range - it draws a full grid labelled from midnight of year one, which
    /// looks like a broken chart rather than an empty one.
    /// </summary>
    public bool HasData => _measured.Count > 0 || _plan.Count > 0;

    /// <summary>
    /// Called by the view on load and whenever the theme variant changes. Paints are SkiaSharp
    /// objects, so they cannot come from a resource dictionary and have to be rebuilt by hand.
    /// </summary>
    public void ApplyPalette(ChartPalette palette)
    {
        _measuredSeries.Stroke = new SolidColorPaint(palette.Measured, 2);
        _planSeries.Stroke = new SolidColorPaint(palette.Plan, 2.5f);

        var labels = new SolidColorPaint(palette.Labels);
        var separators = new SolidColorPaint(palette.Separators) { StrokeThickness = 1 };

        _xAxis.LabelsPaint = labels;
        _xAxis.SeparatorsPaint = separators;
        _yAxis.LabelsPaint = labels;
        _yAxis.SeparatorsPaint = separators;
    }

    private void OnCurrentTemperatureChanged(object? sender, double celsius) =>
        Dispatcher.UIThread.Post(() => Append(_measured, celsius));

    private void OnRampProgressChanged(object? sender, RampProgressEventArgs e) =>
        Dispatcher.UIThread.Post(() => Append(_plan, e.CurrentComputedTarget));

    private void OnRampEnded(object? sender, EventArgs e) => ClearPlan();

    private void OnRampEnded(object? sender, double resetTemperatureCelsius) => ClearPlan();

    /// <summary>The planned line belongs to one run; leaving it behind would suggest a ramp is
    /// still going.</summary>
    private void ClearPlan() => Dispatcher.UIThread.Post(_plan.Clear);

    private void Append(ObservableCollection<DateTimePoint> points, double celsius)
    {
        var wasEmpty = !HasData;

        points.Add(new DateTimePoint(DateTime.Now, celsius));
        Trim(points);

        if (wasEmpty) OnPropertyChanged(nameof(HasData));
    }

    private void Trim(ObservableCollection<DateTimePoint> points)
    {
        var cutoff = DateTime.Now - TimeSpan.FromMinutes(_settings.HistoryRetentionMinutes);

        while (points.Count > 0 && points[0].DateTime < cutoff)
        {
            points.RemoveAt(0);
        }
    }

    public void Dispose()
    {
        _device.CurrentTemperatureChanged -= OnCurrentTemperatureChanged;
        _device.ProgressChanged -= OnRampProgressChanged;
        _device.Completed -= OnRampEnded;
        _device.Stopped -= OnRampEnded;
    }
}
