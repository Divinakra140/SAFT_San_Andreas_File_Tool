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

        // What the runtime believes it has to work with. Five crashes have now killed this process
        // without a single managed exception reaching the handlers in Program - no OutOfMemory, no
        // stack trace, nothing - which means the CLR never saw a failure and something outside it
        // ended the process. If that something is a memory ceiling, this is the number that shows it.
        //
        // Written once at startup rather than per line. An earlier attempt logged
        // Process.GetCurrentProcess() on every entry; under Winlator it returned 0 and the build died
        // sooner than before, so this stays to the managed runtime's own view and happens once.
        try
        {
            var info = GC.GetGCMemoryInfo();
            Note($"runtime memory: {info.TotalAvailableMemoryBytes / 1048576.0:N0} MB available to the GC, " +
                 $"{(Environment.Is64BitProcess ? "64" : "32")}-bit address space, " +
                 $"{Environment.ProcessorCount} cpu(s)");
        }
        catch
        {
            // Never let a diagnostic be the reason the app fails to start.
        }

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
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {Memory()}  {step}{Environment.NewLine}");
            }
        }
        catch
        {
            // Read-only media, or no room. Losing the breadcrumb is bad; failing the operation the
            // user actually asked for because the breadcrumb failed would be worse.
        }
    }

    /// <summary>
    /// How much memory the process is holding, stamped on every line.
    ///
    /// Three crashes in one evening landed on three different steps, none of them reproducible on a
    /// 64-bit machine where the same work peaks at 44 MB live. That pattern is what running out of
    /// address space looks like from the outside: the failure lands on whichever allocation happens
    /// to come next, so patching each site in turn never converges. SAFT is a 32-bit process with a
    /// 2 GB ceiling, and until these numbers are on the record, "it ran out of memory" is a theory.
    ///
    /// This deliberately reports ONLY the managed heap, via GC.GetTotalMemory, which is pure runtime
    /// bookkeeping and touches no OS call.
    ///
    /// It used to also report the private working set via Process.GetCurrentProcess(). That was a
    /// mistake twice over. Winlator returned 0 for it, so it carried no information at all — and it
    /// put a partially implemented Win32 call on every single breadcrumb, on a platform where the
    /// crash being investigated is a silent process kill. The build that added it died EARLIER than
    /// the build before it. A diagnostic that may itself be destabilising the thing it is measuring
    /// is worse than no diagnostic.
    /// </summary>
    private static string Memory()
    {
        try
        {
            return $"[gc {GC.GetTotalMemory(false) / 1048576,4:N0}MB]";
        }
        catch
        {
            return "[gc    ?MB]";
        }
    }

    /// <summary>
    /// A once-per-operation census of what the process is actually holding.
    ///
    /// Every breadcrumb carries the live heap, but that number cannot answer the question that
    /// matters: is memory ACCUMULATING, or has the collector simply not bothered yet? Across six
    /// identical operations the live heap climbed 10 -> 32 MB, which is either a real leak or normal
    /// laziness, and those need opposite fixes. Collecting first and reporting the survivors settles
    /// it - if that number climbs, something is genuinely being held.
    ///
    /// MemoryLoadBytes is the other half: what the RUNTIME believes the whole machine is using. SAFT
    /// dies without ever throwing, which is what being killed from outside looks like, so knowing
    /// whether the system was under pressure at the time is worth more than any managed number.
    ///
    /// Once per install, not per step. The lesson from the diagnostic that made things WORSE is that
    /// a measurement taken thousands of times is a change to the thing being measured; taken once, a
    /// forced collection costs a few milliseconds at a point where the user is reading a summary.
    /// </summary>
    public static void Census(string when)
    {
        try
        {
            // NO FORCED COLLECTION. The first version of this called
            // GC.GetTotalMemory(forceFullCollection: true), which runs a full blocking collection -
            // on the UI THREAD, since that is where the summary is built. On a real device that
            // froze the whole container: the log's last line was the one written immediately before
            // this call, and Android put up "Winlator is not responding".
            //
            // Which is the second time a diagnostic in this app has destabilised the thing it was
            // measuring. The numbers below are all cheap reads of state the runtime already keeps.
            // Anything that makes the process DO something to be measured does not belong here.
            var live = GC.GetTotalMemory(false) / 1048576;
            var info = GC.GetGCMemoryInfo();

            // COMMITTED and FRAGMENTED are the numbers that matter, and neither of them is the heap.
            // SAFT is a 32-bit process: it has about 4 GB of ADDRESS SPACE, and the live-object
            // figure everything else reports says nothing about how much of that has been reserved
            // and never handed back. A process can hold 30 MB of objects while sitting on hundreds
            // of megabytes of committed, fragmented space - and each operation here reads the whole
            // game map, which allocates around 85 MB and drops it again. If committed climbs across
            // operations while the heap does not, that is the accumulation, and it explains a death
            // with no managed exception: it is not the CLR failing to allocate, it is the address
            // space running out underneath it.
            Note($"census ({when}): live {live:N0} MB; " +
                 $"committed {info.TotalCommittedBytes / 1048576:N0} MB, fragmented {info.FragmentedBytes / 1048576:N0} MB; " +
                 $"gen0/1/2 {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}; " +
                 $"system using {info.MemoryLoadBytes / 1048576:N0} MB of {info.TotalAvailableMemoryBytes / 1048576:N0} MB");
        }
        catch (Exception ex)
        {
            Note($"census ({when}): unavailable - {ex.GetType().Name}");
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
