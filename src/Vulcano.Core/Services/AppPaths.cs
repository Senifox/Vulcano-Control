using System.Runtime.InteropServices;

namespace Vulcano.Core.Services;

/// <summary>
/// Where the app keeps settings, ramp profiles, the log file and exported logs.
///
/// Never next to the executable: on Windows an installer puts each update in a new versioned
/// folder, so anything written beside the exe is silently left behind on every update, and on
/// Linux the AppImage mount point is read-only and replaced wholesale.
///
/// And never inside the install directory either, which is the harder-won half. The app used to
/// keep its settings in %LocalAppData%\Vulcano-Control, the same folder Velopack installs into -
/// chosen so the WPF version's settings.json would be found rather than reset. That works right up
/// until somebody installs: the installer empties its target directory first, so a fresh install
/// takes every saved ramp profile with it. Updates go through Update.exe and leave the folder
/// alone, which is why the WPF app never suffered for it and why this stayed hidden until the
/// changeover, when running the installer is exactly what everybody does.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Windows: <c>%AppData%\Vulcano-Control</c> - roaming, and nothing installs there.
    /// Linux: <c>$XDG_CONFIG_HOME/vulcano-control</c>, i.e. <c>~/.config/vulcano-control</c>.
    ///
    /// One expression for both because SpecialFolder.ApplicationData already means the right thing
    /// on each: roaming application data on Windows, XDG_CONFIG_HOME on Linux.
    /// </summary>
    public static string DataDirectory { get; } = CreateDataDirectory();

    /// <summary>
    /// Plainly settings.json again. It shared a folder with the WPF version's file of that name and
    /// had to be called something else; in a folder of its own the qualifier has nothing left to
    /// distinguish it from.
    /// </summary>
    public static string SettingsFilePath => Path.Combine(DataDirectory, "settings.json");

    /// <summary>
    /// Where settings used to live, in the order they should be looked for when this app has none of
    /// its own yet: first this app's previous file, then the WPF version's. Read once and never
    /// written - the first save goes to the new home, and the old copies are left where they are for
    /// somebody who wants to go back.
    ///
    /// Empty away from Windows: no earlier version ever ran there.
    /// </summary>
    public static IReadOnlyList<string> PreviousSettingsFilePaths { get; } = FindPreviousSettings();

    private static string CreateDataDirectory()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Vulcano-Control" : "vulcano-control");

        Directory.CreateDirectory(directory);
        return directory;
    }

    private static IReadOnlyList<string> FindPreviousSettings()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return [];

        var old = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vulcano-Control");

        return
        [
            Path.Combine(old, "settings.v2.json"),  // this app, before it moved out of the install folder
            Path.Combine(old, "settings.json"),     // the WPF version
        ];
    }
}
