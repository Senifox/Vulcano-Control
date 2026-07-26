using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using Microsoft.Win32;
using Vulcano.Core.Models;
using Vulcano.Core.Services;

namespace Vulcano.App.Services;

/// <summary>
/// Works out whether Windows can be asked to show notifications for this app, and sets up the name
/// and icon it should show them under.
///
/// Windows attributes a toast to an AppUserModelID, and for an app that was not installed from a
/// package that id only counts if a Start menu shortcut carries it. Velopack's installer creates
/// exactly such a shortcut, and it carries the pack id - so the pack id is the identity, discovered
/// from the install layout rather than written down here, where it would go stale at the cutover from
/// the preview id to the real one.
///
/// A build running out of bin has no shortcut and therefore no identity. That is not worked around:
/// asking for a toast without one is accepted, drawn nowhere, and reported as a success, so the only
/// safe thing is to know in advance and let the window handle it. This is also why nothing is written
/// to the registry in that case - a copy that cannot use the registration should not leave one behind.
/// </summary>
public static class WindowsAppIdentity
{
    private const string DisplayName = "Vulcano Control";
    private const string IconFileName = "notification-icon.png";

    /// <summary>The id toasts can be sent under, or null when this copy has no notification identity
    /// - which is the normal state of a development build.</summary>
    public static string? RegisteredAppId { get; private set; }

    /// <summary>
    /// Tells Windows which identity this process runs under. Without it the shortcut's id is not
    /// enough, and the difference is invisible: the toast is accepted and never drawn.
    /// </summary>
    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    /// <summary>Runs before any window exists, which is where Windows expects the identity to be
    /// claimed. Idempotent, and quiet on the runs where nothing changed.</summary>
    public static void Register(LogService log)
    {
        if (FindInstalledAppId() is not { } appId)
        {
            log.Log(Strings.Get("Log.Notify.NotInstalled"), LogLevel.Debug);
            return;
        }

        try
        {
            SetCurrentProcessExplicitAppUserModelID(appId);

            using var key = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\AppUserModelId\{appId}", writable: true);
            if (key is null) return;

            var wasRegistered = key.GetValue("DisplayName") as string == DisplayName;

            key.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
            if (EnsureIconOnDisk() is { } iconPath) key.SetValue("IconUri", iconPath, RegistryValueKind.String);

            RegisteredAppId = appId;

            if (!wasRegistered) log.Log(Strings.Get("Log.Notify.Registered", appId));
        }
        catch (Exception ex)
        {
            // Not fatal: notifications fall back to the window, which is where they went before any
            // of this existed.
            log.Log(Strings.Get("Log.Notify.RegisterFailed", ex.Message), LogLevel.Warning);
        }
    }

    /// <summary>
    /// The pack id of the install this copy belongs to, or null when it is not one. Velopack lays an
    /// install out as <c>&lt;PackId&gt;\current\app.exe</c> with <c>Update.exe</c> beside the
    /// <c>current</c> folder, and that shape is what is recognised here - the same shape the Start
    /// menu shortcut's id is derived from.
    ///
    /// A portable copy has that exact shape too and no shortcut at all, which would put us right back
    /// to toasts that report success and show nothing. Velopack marks it with a <c>.portable</c> file,
    /// and that file is the whole difference.
    /// </summary>
    private static string? FindInstalledAppId()
    {
        try
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            if (!string.Equals(current.Name, "current", StringComparison.OrdinalIgnoreCase)) return null;

            var installRoot = current.Parent;
            if (installRoot is null) return null;
            if (!File.Exists(Path.Combine(installRoot.FullName, "Update.exe"))) return null;
            if (File.Exists(Path.Combine(installRoot.FullName, ".portable"))) return null;

            return installRoot.Name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A toast is drawn by Windows, not by us, so it cannot read an icon out of the assembly - it
    /// needs a file. Written once next to the settings, where a cleanup script can find it.
    /// </summary>
    private static string? EnsureIconOnDisk()
    {
        var path = Path.Combine(AppPaths.DataDirectory, IconFileName);
        if (File.Exists(path)) return path;

        try
        {
            using var source = AssetLoader.Open(
                new Uri("avares://vulcano-control/Assets/Icons/vulcano-control-256.png"));
            using var file = File.Create(path);
            source.CopyTo(file);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
