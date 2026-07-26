using Vulcano.Core.Models;

namespace Vulcano.Core.Services;

/// <summary>
/// Central, timestamped log of everything the app does, especially all communication with the
/// device. Held in memory for the Log tab and mirrored to a file in the app's data directory that
/// is overwritten on every launch (no cross-session history) - one file per running instance, see
/// <see cref="ChooseDefaultLogFile"/>.
///
/// UI-free by design: it raises <see cref="EntryAdded"/> from whatever thread called
/// <see cref="Log"/> - background BLE and relay threads included - and keeps its own snapshot in
/// timestamp order. Marshaling onto the UI thread is the view model's job.
/// </summary>
public sealed class LogService
{
    /// <summary>Held for the lifetime of the process, which is the whole point: it is how a second
    /// instance finds out it is the second one.</summary>
    private static Mutex? _firstInstanceMarker;

    private readonly string _logFilePath;
    private readonly object _lock = new();
    private readonly List<LogEntry> _entries = new();

    /// <summary>Raised for every entry, on the calling thread.</summary>
    public event EventHandler<LogEntry>? EntryAdded;

    public LogService(string? logFilePath = null)
    {
        _logFilePath = logFilePath ?? ChooseDefaultLogFile();

        try
        {
            File.WriteAllText(_logFilePath, string.Empty);
        }
        catch
        {
            // Best-effort; failing to reset the log file should not crash the app.
        }

        Log(Strings.Get("Log.AppStarted"));
    }

    public string LogFilePath => _logFilePath;

    /// <summary>
    /// The app's log file, or one with this process's id appended when another instance is already
    /// running. Wiping the log on launch is deliberate - there is no cross-session history - but two
    /// instances on one machine is not a hypothetical: it is how the LAN relay gets tested, and it
    /// is easy to do by accident. Sharing one path meant the second instance erasing the first one's
    /// log the moment it started and both then interleaving into the same file.
    ///
    /// A named mutex rather than a lock on the file itself: entries are written open-append-close,
    /// so the file cannot be held, and "is another instance running" is the actual question.
    /// </summary>
    private static string ChooseDefaultLogFile()
    {
        var name = "vulcano-control.log";

        try
        {
            var marker = new Mutex(initiallyOwned: true, @"Local\Vulcano-Control.Log", out var isFirst);
            if (isFirst)
            {
                _firstInstanceMarker = marker;
            }
            else
            {
                marker.Dispose();
                name = $"vulcano-control.{Environment.ProcessId}.log";
            }
        }
        catch
        {
            // If the OS will not give us a mutex we are no worse off than before: one shared file.
        }

        return Path.Combine(AppPaths.DataDirectory, name);
    }

    /// <summary>A snapshot of every entry so far, oldest first - lets a view model that attaches
    /// late (the Log tab is built after the first log lines are written) catch up without
    /// missing anything.</summary>
    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_lock)
        {
            return _entries.ToArray();
        }
    }

    public void Log(string message, LogLevel level = LogLevel.Info)
    {
        var entry = new LogEntry(DateTime.Now, message, level);

        lock (_lock)
        {
            // Log() is called concurrently from background threads, so entries can arrive slightly
            // out of order; insert at the sorted position to keep the list chronological.
            var index = _entries.Count;
            while (index > 0 && _entries[index - 1].Timestamp > entry.Timestamp)
            {
                index--;
            }
            _entries.Insert(index, entry);

            try
            {
                File.AppendAllText(
                    _logFilePath,
                    $"[{entry.Timestamp:HH:mm:ss}] [{entry.Level}] {entry.Message}" + Environment.NewLine);
            }
            catch
            {
                // Best-effort; a locked or unwritable log file should not crash the app.
            }
        }

        EntryAdded?.Invoke(this, entry);
    }
}
