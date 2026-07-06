namespace Vulcano_Control.Models;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

public sealed record LogEntry(DateTime Timestamp, string Message, LogLevel Level);
