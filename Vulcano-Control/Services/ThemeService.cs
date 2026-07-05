using System.Linq;
using System.Windows;
using Vulcano_Control.Models;

namespace Vulcano_Control.Services;

/// <summary>
/// Applies Light/Dark mode and persists the choice. On Windows 11+ this uses the native
/// WPF Fluent theme's built-in ThemeMode (net9+ API), which themes standard controls correctly.
/// On Windows 10, ThemeMode is known to render incorrectly (see dotnet/wpf#10096), so a
/// hand-rolled ResourceDictionary swap is used there instead.
/// </summary>
public sealed class ThemeService
{
    /// <summary>True on Windows 11+ (build 22000+), where the native Fluent ThemeMode API works correctly.</summary>
    public static bool UsesNativeFluentTheme { get; } = Environment.OSVersion.Version.Build >= 22000;

    private readonly SettingsService _settingsService;
    private AppTheme _currentTheme;

    public AppTheme CurrentTheme => _currentTheme;

    public ThemeService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _currentTheme = _settingsService.Load().Theme;
    }

    /// <summary>Applies the persisted theme. Call once at startup, before MainWindow is shown.</summary>
    public void ApplyStartupTheme() => ApplyTheme(_currentTheme);

    /// <summary>Applies and persists a new theme choice (e.g. from the View menu).</summary>
    public void SetTheme(AppTheme theme)
    {
        if (theme == _currentTheme) return;

        _currentTheme = theme;
        ApplyTheme(theme);
        _settingsService.Save(new AppSettings { Theme = theme });
    }

    private static void ApplyTheme(AppTheme theme)
    {
        if (UsesNativeFluentTheme)
        {
            ApplyFluentThemeMode(theme);
        }
        else
        {
            ApplyCustomTheme(theme);
        }
    }

#pragma warning disable WPF0001 // ThemeMode is an experimental API as of .NET 9/10 WPF.
    private static void ApplyFluentThemeMode(AppTheme theme) =>
        Application.Current.ThemeMode = theme == AppTheme.Dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001

    private static void ApplyCustomTheme(AppTheme theme)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;

        if (!dictionaries.Any(d => d.Source?.OriginalString.EndsWith("ControlStyles.xaml") == true))
        {
            dictionaries.Add(new ResourceDictionary { Source = new Uri("Themes/ControlStyles.xaml", UriKind.Relative) });
        }

        var themeUri = new Uri(
            theme == AppTheme.Dark ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml",
            UriKind.Relative);

        var existing = dictionaries.FirstOrDefault(d =>
            d.Source != null &&
            (d.Source.OriginalString.EndsWith("LightTheme.xaml") || d.Source.OriginalString.EndsWith("DarkTheme.xaml")));

        if (existing != null)
        {
            dictionaries.Remove(existing);
        }

        dictionaries.Add(new ResourceDictionary { Source = themeUri });
    }
}
