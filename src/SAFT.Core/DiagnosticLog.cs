namespace SAFT.Core;

/// <summary>
/// Temporary diagnostic logging: appends a timestamped line to a plain text file next to whatever
/// SAFT is currently working on, flushing immediately. Exists because a crash under Winlator has
/// been happening as a native-level fault that never becomes a catchable .NET exception — a UI
/// update posted from a background thread via <see cref="Progress{T}"/> might never actually get
/// painted if the whole process dies before the UI thread's message loop gets to it, but a flushed
/// file write survives even that, and lets us see exactly which step was reached last.
/// </summary>
internal static class DiagnosticLog
{
    public static void Write(string logDirectory, string message)
    {
        try
        {
            var path = Path.Combine(logDirectory, "saft-diagnostic-log.txt");
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
        }
        catch
        {
            // Diagnostic logging must never be the reason the real operation fails.
        }
    }
}
