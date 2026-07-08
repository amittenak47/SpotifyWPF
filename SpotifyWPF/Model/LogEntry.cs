using System;

namespace SpotifyWPF.Model
{
    /// <summary>
    /// A single activity log entry. Verbose entries are only shown when the
    /// "Verbose" log filter is selected.
    /// </summary>
    public class LogEntry
    {
        public LogEntry(DateTime timestamp, string message, bool isVerbose)
        {
            Timestamp = timestamp;
            Message = message;
            IsVerbose = isVerbose;
        }

        public DateTime Timestamp { get; }

        public string Message { get; }

        public bool IsVerbose { get; }

        public string FormattedMessage => $"[{Timestamp:HH:mm:ss}] {(IsVerbose ? "[Verbose] " : string.Empty)}{Message}";
    }
}
