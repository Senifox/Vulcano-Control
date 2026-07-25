namespace Vulcano.Core.Models;

public enum AppTheme
{
    /// <summary>Follow the desktop's light/dark preference. Falls back to <see cref="Dark"/> when
    /// the platform reports none - common under Linux desktops without a portal setting.</summary>
    System,
    Light,
    Dark
}
