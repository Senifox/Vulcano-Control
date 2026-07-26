using System;
using System.Security;
using Microsoft.Win32;
using Vulcano.Core.Models;
using Vulcano.Core.Services;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Vulcano.App.Services;

/// <summary>
/// Shows a notification as a Windows toast where that is possible, and hands it to the window where
/// it is not.
///
/// The condition is an application identity Windows can attribute the toast to - an AppUserModelID
/// registered under HKCU. Without one, and this was worth finding out the hard way, the API does not
/// object: CreateToastNotifier succeeds, Setting reports Enabled, Show returns, the Failed event
/// never fires, and nothing whatsoever appears on screen. There is no way to detect that
/// after the fact, so the identity is checked before anything is attempted - otherwise the fallback
/// never runs and the notification is simply lost.
///
/// This build has no such registration, so today the window path is the one that runs. Registering
/// the id is a small HKCU key an installer or a first run would write; that is a decision about
/// leaving something behind on the machine, not a technical obstacle.
/// </summary>
public sealed class WindowsToastNotifier : INotifier
{
    /// <summary>Has to match the identity Windows knows the app by, which for a Velopack install is
    /// the shortcut it creates from the assembly.</summary>
    private const string AppUserModelId = "vulcano-control";

    private readonly LogService _log;
    private bool _toastsUnavailable;

    public WindowsToastNotifier(LogService log) => _log = log;

    public bool Enabled { get; set; }

    public event EventHandler<NotificationRequest>? FellBackToWindow;

    public void Notify(string title, string message)
    {
        if (!Enabled) return;

        _log.Log(Strings.Get("Log.Notify", title), LogLevel.Debug);

        if (TryShowToast(title, message)) return;

        if (FellBackToWindow is null)
        {
            // Not a theoretical case worth a shrug: it means the notification was raised, refused by
            // Windows, and then dropped here - the exact silent loss this class exists to avoid.
            _log.Log(Strings.Get("Log.Notify.Nowhere", title), LogLevel.Warning);
            return;
        }

        FellBackToWindow.Invoke(this, new NotificationRequest(title, message));
    }

    private bool TryShowToast(string title, string message)
    {
        if (_toastsUnavailable) return false;

        if (!HasNotificationIdentity())
        {
            _toastsUnavailable = true;
            _log.Log(Strings.Get("Log.Notify.NoIdentity", AppUserModelId), LogLevel.Debug);
            return false;
        }

        try
        {
            var notifier = ToastNotificationManager.CreateToastNotifier(AppUserModelId);

            // Covers the case the identity check cannot: notifications switched off for the app or
            // for the user. Unlike a missing identity, Windows does admit to this one.
            if (notifier.Setting != NotificationSetting.Enabled)
            {
                _toastsUnavailable = true;
                _log.Log(Strings.Get("Log.Notify.NoToasts", notifier.Setting), LogLevel.Debug);
                return false;
            }

            var xml = new XmlDocument();
            xml.LoadXml(
                "<toast><visual><binding template='ToastGeneric'>" +
                $"<text>{Escape(title)}</text><text>{Escape(message)}</text>" +
                "</binding></visual></toast>");

            var toast = new ToastNotification(xml);

            // The platform's own verdict, and the only trustworthy one: Setting says whether the user
            // allows notifications, not whether this app has an identity to send them under. Failed
            // arrives a moment after Show and is what actually distinguishes "displayed" from
            // "accepted and dropped on the floor".
            toast.Failed += (_, args) => OnToastFailed(title, message, args.ErrorCode.HResult);

            notifier.Show(toast);
            return true;
        }
        catch (Exception ex)
        {
            // Once. Every notification after this would fail for the same reason, and a warning per
            // ramp would be noise.
            _toastsUnavailable = true;
            _log.Log(Strings.Get("Log.Notify.NoToasts", ex.Message), LogLevel.Debug);
            return false;
        }
    }

    /// <summary>
    /// Whether Windows has an AppUserModelID registered for this app. Read-only, and nothing is
    /// written here: registering the id is an installer's business, or a question to ask before
    /// leaving a key behind on somebody's machine.
    /// </summary>
    private static bool HasNotificationIdentity()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Windows took the toast and then could not show it. Say it through the window instead,
    /// and stop trying - the reason will not have changed by the next ramp.</summary>
    private void OnToastFailed(string title, string message, int errorCode)
    {
        if (_toastsUnavailable) return;

        _toastsUnavailable = true;
        _log.Log(Strings.Get("Log.Notify.NoToasts", $"0x{errorCode:X8}"), LogLevel.Debug);
        FellBackToWindow?.Invoke(this, new NotificationRequest(title, message));
    }

    /// <summary>The texts are ours, but they carry temperatures and host names, and building XML by
    /// concatenation without this is how a stray ampersand takes out a feature.</summary>
    private static string Escape(string value) => SecurityElement.Escape(value) ?? "";
}
