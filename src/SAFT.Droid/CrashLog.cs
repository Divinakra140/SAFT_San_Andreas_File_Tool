using Android.Runtime;

namespace SAFT.Droid;

/// <summary>
/// Catches what kills the app and writes it down.
///
/// On Windows, a crash left a log in the game folder and there was something to read afterwards. On
/// Android a crash is a process that stops existing: the app "expands and shoots you back to the
/// home screen", and unless the device is plugged into a computer running logcat, that is the entire
/// available evidence. This project has already lost days to exactly that kind of silence — four
/// crashes whose only trace was where the log stopped.
///
/// So the handlers go in before anything else runs, and the stack trace lands in Download, where a
/// file browser can reach it and a person can send it on.
/// </summary>
internal static class CrashLog
{
    private static string? _folder;

    /// <summary>Where the last crash was written, for showing on screen.</summary>
    public static string? LastPath { get; private set; }

    public static void Install(string? preferredFolder)
    {
        _folder = preferredFolder;

        // The Android-side handler, for exceptions crossing from Java back into managed code.
        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
        {
            Write("UnhandledExceptionRaiser", e.Exception);
            e.Handled = false;
        };

        // And the managed one, for everything else - including exceptions thrown on a Task that
        // nobody awaited, which is most of the work this app does.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("AppDomain", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
            Write("UnobservedTask", e.Exception);
    }

    public static void Write(string source, Exception? ex)
    {
        if (ex is null) return;

        LastPath = null;

        var text =
            $"SAFT crash — {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}{System.Environment.NewLine}" +
            $"source: {source}{System.Environment.NewLine}{System.Environment.NewLine}" +
            Describe(ex);

        // Logcat first, because it always works. It needs a computer and `adb logcat` to read, but it
        // cannot fail for want of a permission, and a crash early enough to matter is usually one
        // that happened before any permission was granted.
        try
        {
            Android.Util.Log.Error("SAFT", text);
        }
        catch
        {
            // Nothing useful to do if even logging fails.
        }

        // EVERY location, not the first that works. Download is the one a person can find; the app's
        // own folder is the one that is always writable. Writing only to the first meant that a crash
        // before all-files access was granted went to a folder nobody would think to look in, and
        // read as "no crash log was created at all".
        foreach (var folder in Candidates())
        {
            try
            {
                if (string.IsNullOrEmpty(folder)) continue;
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, "saft-crash.txt");
                File.WriteAllText(path, text);
                LastPath ??= path;
            }
            catch
            {
                // Try the next place rather than throwing from inside a crash handler, which would
                // replace a diagnosable failure with an undiagnosable one.
            }
        }
    }

    /// <summary>
    /// Download first, because that is the folder every Android file browser opens on. The app's own
    /// external folder is the fallback: always writable, but buried somewhere nobody navigates to by
    /// accident.
    /// </summary>
    private static IEnumerable<string?> Candidates()
    {
        yield return Path.Combine(
            Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath ?? "/sdcard", "Download");
        yield return _folder;
    }

    /// <summary>Unwraps the whole chain — the inner exception is usually the one that says why.</summary>
    public static string Describe(Exception ex)
    {
        var text = "";
        for (var current = ex; current is not null; current = current.InnerException)
        {
            text += $"{current.GetType().FullName}: {current.Message}{System.Environment.NewLine}";
            text += current.StackTrace + System.Environment.NewLine + System.Environment.NewLine;
        }

        return text;
    }
}
