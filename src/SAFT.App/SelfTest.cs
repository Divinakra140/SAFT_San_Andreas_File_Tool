using System.Diagnostics;
using SAFT.Core;

namespace SAFT.App;

/// <summary>
/// A headless loop that runs ONLY the read-only analysis SAFT does before an install, over and over,
/// reporting what the heap is doing each time.
///
/// It exists because of one question that could not be answered from the ordinary log: why does
/// scanning crash when installing never does. Every crash observed so far has landed inside the two
/// phases that read and weigh the whole map, never in the code that writes files. Those phases are
/// pure reads, which makes them the LESS dangerous-looking half of the app, so the reason has to be
/// something other than what they touch on disk.
///
/// The leading suspicion is the Large Object Heap. Any array over 85 KB lives there, it is not
/// compacted by default, and the placement list is ~204 KB on a 32-bit build - built by doubling, so
/// each pass abandons a 128 KB and a 256 KB hole behind it, four times per install. A process can
/// then fail to find a contiguous block while holding only 30 MB live, which would look exactly like
/// this: intermittent, unrelated to the mod, and invisible in a "total memory" figure.
///
/// This loop makes that measurable. If fragmentation is the cause, the numbers climb run over run and
/// it dies at a repeatable-ish iteration. If they stay flat and it dies anyway, the cause is
/// elsewhere and this rules out a whole family of theories in one go.
///
/// Run: SAFT.exe --selftest "C:\path\to\Grand Theft Auto San Andreas" [runs]
/// Writes to saft-selftest-log.txt next to the exe. Touches nothing in the game.
/// </summary>
internal static class SelfTest
{
    private const int DefaultRuns = 20;

    public static int Run(string[] args)
    {
        var gameRoot = args.Length > 1 ? args[1] : null;
        var runs = args.Length > 2 && int.TryParse(args[2], out var n) ? n : DefaultRuns;

        if (gameRoot is null || !Directory.Exists(gameRoot))
        {
            Log($"usage: SAFT.exe --selftest \"<game folder>\" [runs]   (got: {gameRoot ?? "nothing"})");
            return 2;
        }

        Log(new string('=', 78));
        Log($"self test starting - {runs} run(s) against {gameRoot}");
        Log($"{(Environment.Is64BitProcess ? "64" : "32")}-bit process, {Environment.OSVersion}");
        Log(new string('=', 78));
        Log(Header());

        for (var run = 1; run <= runs; run++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                // Exactly what an install does before it writes anything, in the same order.
                var definitions = PlacementDensity.ReadDefinitions(gameRoot);
                Log(Row(run, "definitions", sw, definitions.Count));

                var weights = PlacementDensity.WeighGameAssets(gameRoot, definitions);
                Log(Row(run, "weights", sw, weights.Count));

                var baseline = PlacementDensity.MeasureGameBaseline(gameRoot);
                Log(Row(run, "baseline", sw, baseline.BusiestObjectCount));

                var impact = StreamingImpact.Measure(gameRoot, new Dictionary<string, long>());
                Log(Row(run, "impact", sw, (int)(impact.HeaviestAreaBefore / 1048576)));

                Log(Row(run, "RUN COMPLETE", sw, 0));
            }
            catch (Exception ex)
            {
                // A catchable failure is itself a finding - it means the process is NOT being killed
                // outright, which is the opposite of what has been seen so far.
                Log($"  run {run}: THREW {ex.GetType().Name}: {ex.Message}");
                Log(ex.StackTrace ?? "(no stack)");
                return 1;
            }
        }

        Log("self test finished all runs without dying.");
        return 0;
    }

    private static string Header() =>
        "  run  phase          elapsed     allocated-total   heap-now   gen0/1/2   pause%";

    /// <summary>
    /// Everything here is pure runtime bookkeeping - no OS calls. An earlier attempt to log the
    /// process working set went through Process.GetCurrentProcess(), which Winlator answered with 0
    /// and which put a partially implemented Win32 call on every line of a crash investigation.
    ///
    /// "allocated-total" is cumulative and never goes down; it is the churn. "heap-now" is what is
    /// live. A large gap between them with the gen2 count climbing is the signature of exactly the
    /// fragmentation this is looking for.
    /// </summary>
    private static string Row(int run, string phase, Stopwatch sw, int count)
    {
        var info = GC.GetGCMemoryInfo();
        return $"  {run,3}  {phase,-13} {sw.ElapsedMilliseconds,6:N0}ms  " +
               $"{GC.GetTotalAllocatedBytes() / 1048576.0,10:N0} MB  " +
               $"{GC.GetTotalMemory(false) / 1048576.0,7:N0} MB  " +
               $"{GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}  " +
               $"{info.PauseTimePercentage,5:N1}  n={count:N0}";
    }

    private static string LogPath => Path.Combine(Program.ExeFolder, "saft-selftest-log.txt");

    private static void Log(string line)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {line}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never be the reason a diagnostic run fails.
        }
    }
}
