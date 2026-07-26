using CommunityToolkit.Mvvm.ComponentModel;
using Vulcano.Core.Models;

namespace Vulcano.App.ViewModels;

/// <summary>
/// One of the four curve chips in the segment inspector. A view model rather than the bare enum so
/// the chip knows whether it is the one currently in use - and so the name the design uses
/// ("ease-in-out") lives somewhere other than a converter.
/// </summary>
public partial class CurveOptionViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public CurveOptionViewModel(CurveKind kind)
    {
        Kind = kind;
    }

    public CurveKind Kind { get; }

    public string Name => CurveNames.Of(Kind);
}

/// <summary>The four curve names exactly as the design writes them, in one place.</summary>
public static class CurveNames
{
    public static string Of(CurveKind kind) => kind switch
    {
        CurveKind.Exponential => "exponential",
        CurveKind.Steep => "steep",
        CurveKind.EaseInOut => "ease-in-out",
        _ => "linear",
    };
}
