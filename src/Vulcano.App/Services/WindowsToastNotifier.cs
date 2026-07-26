using System;
using System.Security;
using Vulcano.Core.Models;
using Vulcano.Core.Services;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Vulcano.App.Services;

/// <summary>
/// Shows a notification as a Windows toast where that is possible, and hands it to the window where
/// it is not.
///
/// Whether it is possible comes down to <see cref="WindowsAppIdentity"/> having found an identity to
/// send it under, and that is checked before anything is attempted rather than afterwards - because
/// afterwards is impossible. Without an identity the API does not object: CreateToastNotifier
/// succeeds, Setting reports Enabled, Show returns, the Failed event never fires, and nothing appears
/// on screen. A notification that reports success and shows nothing is worse than one that fails, so
/// the question is asked in the one place where it can still be answered.
/// </summary>
public sealed class WindowsToastNotifier : INotifier
{
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

        if (WindowsAppIdentity.RegisteredAppId is not { } appUserModelId)
        {
            _toastsUnavailable = true;
            _log.Log(Strings.Get("Log.Notify.NoIdentity"), LogLevel.Debug);
            return false;
        }

        try
        {
            var notifier = ToastNotificationManager.CreateToastNotifier(appUserModelId);

            // Covers the case the identity check cannot: notifications switched off for the app or
            // for the user. Unlike a missing identity, Windows does admit to this one.
            if (notifier.Setting != NotificationSetting.Enabled)
            {
                _toastsUnavailable = true;
                // The enum name, spelled out: a log line that says "would not show it ()" names no
                // cause at all, which is how the first attempt at this wasted a run.
                _log.Log(
                    Strings.Get("Log.Notify.NoToasts", $"{appUserModelId}: {notifier.Setting:G}"),
                    LogLevel.Debug);
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
            // ramp would be noise. Type and HResult, not just Message - the COM exceptions this API
            // throws often carry no message at all, and "would not show it ()" says nothing.
            _toastsUnavailable = true;
            _log.Log(
                Strings.Get(
                    "Log.Notify.NoToasts",
                    $"{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}".Trim()),
                LogLevel.Debug);
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
