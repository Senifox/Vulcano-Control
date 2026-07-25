using Avalonia;
using Avalonia.Platform;
using Avalonia.Styling;
using Vulcano.Core.Models;

namespace Vulcano.App.Services;

/// <summary>
/// Applies the chosen <see cref="AppTheme"/> to the application. Replaces the WPF version's
/// dictionary swapping, its <c>UsesNativeFluentTheme</c> special case and the DWM P/Invoke that
/// used to colour the title bar - Avalonia draws everything itself, including the title bar, so
/// switching the theme variant is the whole job.
/// </summary>
public sealed class ThemeManager
{
    private AppTheme _current = AppTheme.System;

    public ThemeManager()
    {
        var platform = Application.Current?.PlatformSettings;
        if (platform is not null)
        {
            // Only matters while following the system: the desktop can flip light/dark under us.
            platform.ColorValuesChanged += (_, _) =>
            {
                if (_current == AppTheme.System) Apply(AppTheme.System);
            };
        }
    }

    public void Apply(AppTheme theme)
    {
        _current = theme;

        if (Application.Current is not { } app) return;

        app.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ResolveSystemVariant(),
        };
    }

    /// <summary>
    /// The desktop's preference, falling back to dark. Deliberately not ThemeVariant.Default:
    /// a Linux desktop that reports no preference at all would otherwise land on light, and this
    /// app is a dark-first design.
    /// </summary>
    private static ThemeVariant ResolveSystemVariant()
    {
        var values = Application.Current?.PlatformSettings?.GetColorValues();
        return values?.ThemeVariant == PlatformThemeVariant.Light
            ? ThemeVariant.Light
            : ThemeVariant.Dark;
    }
}
