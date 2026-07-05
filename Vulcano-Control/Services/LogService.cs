using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Vulcano_Control.Models;

namespace Vulcano_Control.Services;

/// <summary>
/// Central, timestamped log of everything the app does, especially all communication with the
/// Volcano. Held in-memory for the LogWindow to bind to, and mirrored to a local file next to
/// the executable that is overwritten on every launch (no cross-session history).
/// </summary>
public sealed class LogService
{
    private static readonly string LogFilePath = Path.Combine(AppContext.BaseDirectory, "Vulcano-Control.log");

    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly object _fileLock = new();

    public ObservableCollection<LogEntry> Entries { get; } = new();

    public LogService()
    {
        try
        {
            File.WriteAllText(LogFilePath, string.Empty);
        }
        catch
        {
            // Best-effort; failing to reset the log file should not crash the app.
        }

        Log("Programm gestartet.");
    }

    public void Log(string message)
    {
        var entry = new LogEntry(DateTime.Now, message);

        lock (_fileLock)
        {
            try
            {
                File.AppendAllText(LogFilePath, $"[{entry.Timestamp:HH:mm:ss}] {entry.Message}" + Environment.NewLine);
            }
            catch
            {
                // Best-effort; a locked/unwritable log file should not crash the app.
            }
        }

        // Log() can be called concurrently from background BLE threads and the UI thread;
        // BeginInvoke calls from different threads are not guaranteed to reach the dispatcher
        // queue in timestamp order, so insert at the correct sorted position instead of
        // blindly appending, to keep the displayed list chronological. Newest entries go to
        // the top (index 0), so the latest activity is always visible without scrolling.
        _dispatcher.BeginInvoke(() =>
        {
            var index = 0;
            while (index < Entries.Count && Entries[index].Timestamp >= entry.Timestamp)
            {
                index++;
            }
            Entries.Insert(index, entry);
        });
    }
}
