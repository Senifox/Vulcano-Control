namespace Vulcano.Core.Models;

/// <summary>Log severities. Deliberately never translated - they appear verbatim in the exported
/// log file, and a German export would not match what users see in issue reports.</summary>
public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

public sealed record LogEntry(DateTime Timestamp, string Message, LogLevel Level);
