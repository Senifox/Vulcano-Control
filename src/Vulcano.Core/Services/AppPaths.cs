using System.Runtime.InteropServices;

namespace Vulcano.Core.Services;

/// <summary>
/// Where the app keeps settings, ramp profiles, the log file and exported logs.
///
/// Never next to the executable: on Windows an installer puts each update in a new versioned
/// folder, so anything written beside the exe is silently left behind on every update, and on
/// Linux the AppImage mount point is read-only and replaced wholesale.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Windows: <c>%LocalAppData%\Vulcano-Control</c> - deliberately the same folder the WPF
    /// version used, so an existing settings.json is picked up rather than reset.
    /// Linux: <c>$XDG_CONFIG_HOME/vulcano-control</c>, i.e. <c>~/.config/vulcano-control</c>
    /// (.NET maps SpecialFolder.ApplicationData to XDG_CONFIG_HOME there, while
    /// LocalApplicationData would land in ~/.local/share).
    /// </summary>
    public static string DataDirectory { get; } = CreateDataDirectory();

    public static string SettingsFilePath => Path.Combine(DataDirectory, "settings.json");

    private static string CreateDataDirectory()
    {
        var directory = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Vulcano-Control")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "vulcano-control");

        Directory.CreateDirectory(directory);
        return directory;
    }
}
