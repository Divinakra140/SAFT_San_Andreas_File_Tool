namespace SAFT.App;

internal static class Program
{
    /// <summary>
    /// Where an unexpected failure gets written. Next to the exe, because SAFT is portable and that
    /// is the folder the user can actually find and send on.
    /// </summary>
    private static string CrashLogPath => Path.Combine(ExeFolder, "saft-crash-log.txt");

    /// <summary>
    /// The folder holding SAFT.exe. Resolved from the running process rather than
    /// AppContext.BaseDirectory, which for a single-file publish can point at the temporary
    /// self-extraction folder — a log written there is one nobody will ever find.
    /// </summary>
    internal static string ExeFolder =>
        Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? AppContext.BaseDirectory;

    [STAThread]
    private static void Main()
    {
        // A crash that closes the window with no message is the hardest thing to act on, and under
        // an emulated environment it is also the most likely: the parts of Windows that WinForms
        // leans on are exactly the parts most likely to behave differently there. These handlers
        // catch what the try/catch below cannot - failures on the UI message pump and on background
        // threads - so that "it just closed" becomes a file with a stack trace in it.
        Application.ThreadException += (_, e) => Report(e.Exception, "UI thread");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception, "background thread");
        TaskScheduler.UnobservedTaskException += (_, e) => Report(e.Exception, "background task");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        try
        {
            ActivityLog.SessionStarted(typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown");
            ApplicationConfiguration.Initialize();
            ActivityLog.Note("opening main window");
            Application.Run(new MainForm());
            ActivityLog.SessionEnded();
        }
        catch (Exception ex)
        {
            // A completely silent "the app just doesn't open" is the hardest failure to diagnose —
            // surface whatever actually went wrong, even this early in startup, instead of that.
            Report(ex, "startup");
        }
    }

    private static void Report(Exception? ex, string where)
    {
        if (ex is null) return;

        // Into the breadcrumb file as well as the crash log, so one file holds the whole story: the
        // steps leading up to the failure and the failure itself, in order.
        ActivityLog.Note($"FAILED on the {where}: {ex.GetType().Name}: {ex.Message}");

        var written = false;
        try
        {
            var entry =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  SAFT failed on the {where}{Environment.NewLine}" +
                $"{ex}{Environment.NewLine}{new string('-', 78)}{Environment.NewLine}";
            File.AppendAllText(CrashLogPath, entry);
            written = true;
        }
        catch
        {
            // Read-only media, or no room. The message box below still gets shown, which is the
            // part that matters.
        }

        try
        {
            MessageBox.Show(
                (written ? $"Details were written to:{Environment.NewLine}{CrashLogPath}{Environment.NewLine}{Environment.NewLine}" : "") +
                ex.ToString(),
                "SAFT hit an unexpected problem", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // Nothing further can be done; at least the log attempt above already happened.
        }
    }
}
