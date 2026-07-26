using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Vulcano.App.Controls;
using Vulcano.App.ViewModels;
using Vulcano.Core.Models;

namespace Vulcano.App.Tests;

/// <summary>
/// The curve editor, driven by an actual pointer.
///
/// This is the hand-drawn control - hit testing, dragging, the ghost point where a click would
/// insert - and none of it goes through a view model, so nothing else in the test suite can reach
/// it. The bug that made this file worth writing lived exactly here: a click inserted a point
/// straight into the collection, so it was never numbered and never watched.
///
/// The pixel arithmetic below mirrors the control's own layout constants. That duplication is the
/// price of testing a control whose whole job is geometry, and it fails loudly rather than quietly
/// if the padding ever changes.
/// </summary>
public class RampCurveEditorTests
{
    private const double PadLeft = 46;
    private const double PadRight = 14;
    private const double PadTop = 12;
    private const double PadBottom = 24;
    private const double RangePadding = 15;

    private const double Width = 600;
    private const double Height = 300;

    private static double PlotLeft => PadLeft;
    private static double PlotRight => Width - PadRight;
    private static double PlotTop => PadTop;
    private static double PlotBottom => Height - PadBottom;

    /// <summary>
    /// A ramp whose points share a temperature, so the curve is a horizontal line exactly halfway up
    /// the plot - the padding above and below is the same, so the line's height needs no arithmetic
    /// beyond a midpoint, and any x on the plot is on the curve.
    /// </summary>
    private static ObservableCollection<RampPointViewModel> FlatRamp() =>
    [
        new(new RampPoint(0, 200, CurveKind.Linear)),
        new(new RampPoint(20, 200, CurveKind.Linear)),
    ];

    private static double FlatCurveY => (PlotTop + PlotBottom) / 2;

    private static Point OnCurveAt(double minutes, double totalMinutes = 20) =>
        new(PlotLeft + ((PlotRight - PlotLeft) * (minutes / totalMinutes)), FlatCurveY);

    private sealed class Harness : IDisposable
    {
        public Harness(ObservableCollection<RampPointViewModel> points)
        {
            Points = points;
            Editor = new RampCurveEditor
            {
                Points = points,
                InsertPointCommand = new RelayCommand<int>(minute => Inserted.Add(minute)),
            };

            Window = new Window
            {
                Width = Width,
                Height = Height,
                SystemDecorations = SystemDecorations.None,
                Content = Editor,
            };

            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public Window Window { get; }
        public RampCurveEditor Editor { get; }
        public ObservableCollection<RampPointViewModel> Points { get; }
        public List<int> Inserted { get; } = new();

        /// <summary>A click is a move and then a press: the control decides where an insert would go
        /// while the pointer travels, which is also what draws the ghost.</summary>
        public void ClickAt(Point position)
        {
            Window.MouseMove(position);
            Dispatcher.UIThread.RunJobs();
            Window.MouseDown(position, MouseButton.Left);
            Window.MouseUp(position, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        public void DragFrom(Point from, Point to)
        {
            Window.MouseMove(from);
            Window.MouseDown(from, MouseButton.Left);
            Window.MouseMove(to);
            Window.MouseUp(to, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose() => Window.Close();
    }

    // --- Inserting ---

    /// <summary>
    /// The control asks for a point rather than making one. It used to build it and push it into the
    /// collection, which meant the view model never numbered it and never noticed it being edited.
    /// </summary>
    [AvaloniaFact]
    public void Clicking_the_curve_asks_for_a_point_and_does_not_make_one()
    {
        using var harness = new Harness(FlatRamp());

        harness.ClickAt(OnCurveAt(10));

        Assert.Equal([10], harness.Inserted);
        Assert.Equal(2, harness.Points.Count);
    }

    [AvaloniaFact]
    public void Clicking_away_from_the_curve_asks_for_nothing()
    {
        using var harness = new Harness(FlatRamp());

        harness.ClickAt(new Point(OnCurveAt(10).X, FlatCurveY - 60));

        Assert.Empty(harness.Inserted);
    }

    [AvaloniaFact]
    public void Clicking_outside_the_plot_asks_for_nothing()
    {
        using var harness = new Harness(FlatRamp());

        harness.ClickAt(new Point(PlotLeft - 20, FlatCurveY));

        Assert.Empty(harness.Inserted);
    }

    /// <summary>There is no room for a point between minute 0 and minute 0, and the ends already
    /// have one.</summary>
    [AvaloniaFact]
    public void Clicking_on_top_of_an_existing_point_asks_for_nothing()
    {
        using var harness = new Harness(FlatRamp());

        harness.ClickAt(OnCurveAt(0));
        harness.ClickAt(OnCurveAt(20));

        Assert.Empty(harness.Inserted);
    }

    // --- Dragging ---

    [AvaloniaFact]
    public void Dragging_a_point_upwards_makes_it_hotter()
    {
        using var harness = new Harness(FlatRamp());
        var last = harness.Points[1];

        harness.DragFrom(OnCurveAt(20), new Point(OnCurveAt(20).X, FlatCurveY - 40));

        Assert.True(last.Celsius > 200, $"expected hotter than 200, got {last.Celsius}");
    }

    [AvaloniaFact]
    public void Dragging_a_middle_point_sideways_moves_it_in_time()
    {
        ObservableCollection<RampPointViewModel> points =
        [
            new(new RampPoint(0, 200, CurveKind.Linear)),
            new(new RampPoint(10, 200, CurveKind.Linear)),
            new(new RampPoint(20, 200, CurveKind.Linear)),
        ];
        using var harness = new Harness(points);
        var middle = points[1];

        harness.DragFrom(OnCurveAt(10), OnCurveAt(14));

        Assert.True(middle.TimeMinutes > 10, $"expected later than 10, got {middle.TimeMinutes}");
        Assert.True(middle.TimeMinutes < 20, "and still before the point after it");
    }

    /// <summary>
    /// Minute zero is where the ramp starts by definition - the warm-up covers everything before it -
    /// so the first point may be dragged warmer or cooler but never later.
    /// </summary>
    [AvaloniaFact]
    public void The_first_point_cannot_be_dragged_out_of_minute_zero()
    {
        using var harness = new Harness(FlatRamp());
        var first = harness.Points[0];

        harness.DragFrom(OnCurveAt(0), OnCurveAt(6));

        Assert.Equal(0, first.TimeMinutes);
    }

    [AvaloniaFact]
    public void Dragging_selects_the_point_being_dragged()
    {
        ObservableCollection<RampPointViewModel> points =
        [
            new(new RampPoint(0, 200, CurveKind.Linear)),
            new(new RampPoint(10, 200, CurveKind.Linear)),
            new(new RampPoint(20, 200, CurveKind.Linear)),
        ];
        using var harness = new Harness(points);

        harness.DragFrom(OnCurveAt(10), OnCurveAt(11));

        Assert.Equal(1, harness.Editor.SelectedIndex);
    }
}
