using Vulcano.Core.Models;

namespace Vulcano.Core.Services;

/// <summary>
/// Central, timestamped log of everything the app does, especially all communication with the
/// device. Held in memory for the Log tab and mirrored to a file in the app's data directory that
/// is overwritten on every launch (no cross-session history).
///
/// UI-free by design: it raises <see cref="EntryAdded"/> from whatever thread called
/// <see cref="Log"/> - background BLE and relay threads included - and keeps its own snapshot in
/// timestamp order. Marshaling onto the UI thread is the view model's job.
/// </summary>
public sealed class LogService
{
    private readonly string _logFilePath;
    private readonly object _lock = new();
    private readonly List<LogEntry> _entries = new();

    /// <summary>Raised for every entry, on the calling thread.</summary>
    public event EventHandler<LogEntry>? EntryAdded;

    public LogService(string? logFilePath = null)
    {
        _logFilePath = logFilePath ?? Path.Combine(AppPaths.DataDirectory, "vulcano-control.log");

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
