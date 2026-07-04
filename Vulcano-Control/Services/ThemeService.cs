using System.Windows;
using Vulcano_Control.Models;

namespace Vulcano_Control.Services;

/// <summary>
/// Applies the WPF Fluent theme's built-in Light/Dark mode (net9+ <see cref="ThemeMode"/> API)
/// and persists the choice. Using the native ThemeMode gives correctly themed standard controls
/// (Button, ComboBox, Slider, GroupBox, ...) instead of hand-rolled brush overrides.
/// </summary>
public sealed class ThemeService
{
    private readonly SettingsService _settingsService;
    private AppTheme _currentTheme;

    public AppTheme CurrentTheme => _currentTheme;

    public ThemeService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _currentTheme = _settingsService.Load().Theme;
    }

    /// <summary>Applies the persisted theme. Call once at startup, before MainWindow is shown.</summary>
    public void ApplyStartupTheme() => ApplyThemeMode(_currentTheme);

    /// <summary>Applies and persists a new theme choice (e.g. from the View menu).</summary>
    public void SetTheme(AppTheme theme)
    {
        if (theme == _currentTheme) return;

        _currentTheme = theme;
        ApplyThemeMode(theme);
        _settingsService.Save(new AppSettings { Theme = theme });
    }

#pragma warning disable WPF0001 // ThemeMode is an experimental API as of .NET 9/10 WPF.
    private static void ApplyThemeMode(AppTheme theme) =>
        Application.Current.ThemeMode = theme == AppTheme.Dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
}
