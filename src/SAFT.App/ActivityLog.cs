namespace SAFT.App;

/// <summary>
/// A breadcrumb trail of what SAFT was doing, flushed to disk after every single line.
///
/// This exists for one specific failure that nothing else catches: SAFT disappearing with no
/// message and no crash log. The handlers in <see cref="Program"/> only fire for managed
/// exceptions — if the process is killed outright (which is what an emulated Windows layer running
/// on a phone does under memory pressure, and what a fault inside Wine's own GDI/USER code looks
/// like) then no handler runs and nothing is written after the fact.
///
/// So this is written BEFORE each step rather than after the failure. When SAFT vanishes, the last
/// line in this file names the step it vanished on, which turns "it sometimes crashes" into
/// something with an address. Every line is flushed immediately for the same reason: buffered
/// output is exactly the output a hard kill throws away.
/// </summary>
internal static class ActivityLog
{
    /// <summary>Handlers marshal to the UI thread, but background steps log too.</summary>
    private static readonly object Gate = new();

    /// <summary>
    /// Kept small enough that a user can open it and read the tail, and small enough that it never
    /// becomes a thing quietly eating an SD card. Older sessions are dropped, not merged.
    /// </summary>
    private const long MaxBytes = 256 * 1024;

    private static string Path => System.IO.Path.Combine(Program.ExeFolder, "saft-activity-log.txt");

    /// <summary>
    /// Whether the previous run ended without reaching <see cref="SessionEnded"/>. Read once at
    /// startup, before this session appends anything.
    /// </summary>
    internal static bool PreviousSessionDiedSilently { get; private set; }

    internal static void SessionStarted(string version)
    {
        try
        {
            var path = Path;
            if (File.Exists(path))
            {
                var tail = ReadTail(path);
                PreviousSessionDiedSilently = tail.Length > 0 && !tail.Contains("session ended", StringComparison.Ordinal);

                // Rotation is a plain truncate: the point of this file is the last few dozen lines,
                // so keeping a .1 alongside it would double the clutter for no added evidence.
                if (new FileInfo(path).Length > MaxBytes) File.Delete(path);
            }
        }
        catch
        {
            // Diagnostics must never be the thing that stops the app starting.
        }

        Note($"session started - SAFT {version}, {Environment.OSVersion}, " +
             $"{(Environment.Is64BitProcess ? "64" : "32")}-bit, exe folder {Program.ExeFolder}");

        if (PreviousSessionDiedSilently)
            Note("NOTE: the previous session never logged a clean exit - see the lines above this session header");
    }

    internal static void SessionEnded() => Note("session ended cleanly");

    internal static void Note(string step)
    {
        try
        {
            lock (Gate)
            {
                // AppendAllText opens, writes and closes, so the bytes are on the device before the
                // call returns. That is the entire point here - a StreamWriter left open would lose
                // the last and most important line.
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff}  {step}{Environment.NewLine}");
            }
        }
        catch
        {
            // Read-only media, or no room. Losing the breadcrumb is bad; failing the operation the
            // user actually asked for because the breadcrumb failed would be worse.
        }
    }

    /// <summary>Whatever the last session managed to write, without loading a large file.</summary>
    private static string ReadTail(string path)
    {
        using var stream = File.OpenRead(path);
        var length = (int)Math.Min(stream.Length, 4096);
        stream.Seek(-length, SeekOrigin.End);
        var buffer = new byte[length];
        stream.ReadExactly(buffer);
        return System.Text.Encoding.UTF8.GetString(buffer);
    }
}
