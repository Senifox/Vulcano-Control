namespace Vulcano.Core.Services;

/// <summary>One thing worth telling somebody who is not looking at the window.</summary>
public readonly record struct NotificationRequest(string Title, string Message);

/// <summary>
/// Tells the person something happened while they were doing something else - a ramp finishing is
/// half an hour away from the click that started it, and nobody watches a progress bar for half an
/// hour.
///
/// Whether that arrives as a system notification or as something the window does itself depends on
/// the platform and, on Windows, on how the app was installed; implementations decide, and say so
/// through <see cref="FellBackToWindow"/> when the operating system would not take it.
/// </summary>
public interface INotifier
{
    /// <summary>Follows the setting, and is checked here rather than at every call site.</summary>
    bool Enabled { get; set; }

    void Notify(string title, string message);

    /// <summary>Raised when the notification could not be handed to the operating system, so the
    /// window has to make itself noticed instead.</summary>
    event EventHandler<NotificationRequest>? FellBackToWindow;
}

/// <summary>Used where notifications have nowhere to go, and by tests.</summary>
public sealed class NullNotifier : INotifier
{
    public bool Enabled { get; set; }

    public void Notify(string title, string message) => FellBackToWindow?.Invoke(this, new(title, message));

    public event EventHandler<NotificationRequest>? FellBackToWindow;
}
