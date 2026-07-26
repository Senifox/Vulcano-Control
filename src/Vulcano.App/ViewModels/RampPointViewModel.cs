using CommunityToolkit.Mvvm.ComponentModel;
using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.App.ViewModels;

/// <summary>
/// One row of the point table, and one ring in the curve editor. Mutable where
/// <see cref="RampPoint"/> is not, because the table edits it in place and the chart has to follow
/// as it is dragged.
/// </summary>
public partial class RampPointViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    private int _timeMinutes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TemperatureText))]
    private double _celsius;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurveText))]
    private CurveKind _curveToNext;

    /// <summary>1-based, as the table's "#" column shows it.</summary>
    [ObservableProperty]
    private int _number;

    /// <summary>The last point has no segment after it, so no curve to choose.</summary>
    [ObservableProperty]
    private bool _isLast;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Set by validation, so the row can mark the value that is wrong.</summary>
    [ObservableProperty]
    private bool _hasError;

    public RampPointViewModel(RampPoint point)
    {
        _timeMinutes = point.TimeMinutes;
        _celsius = point.Celsius;
        _curveToNext = point.CurveToNext;
    }

    public string TimeText => Formatting.Minutes(TimeMinutes);

    public string TemperatureText => Formatting.Celsius(Celsius);

    /// <summary>The curve as the design names it, not as the enum spells it.</summary>
    public string CurveText => CurveNames.Of(CurveToNext);

    public RampPoint ToPoint() => new(TimeMinutes, Celsius, CurveToNext);
}
