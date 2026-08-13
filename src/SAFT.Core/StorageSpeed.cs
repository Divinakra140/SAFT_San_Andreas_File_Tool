using System.Diagnostics;

namespace SAFT.Core;

/// <summary>
/// Watches how fast bytes are actually reaching the disk while an archive is being written, so SAFT
/// can say plainly when the storage underneath it has slowed down.
///
/// This exists because of a measured session on a real device: six archive rebuilds at 28–40 seconds
/// each, and then the seventh took 183 seconds for the same 940 MB — 5.1 MB/s. Listing the game
/// folder went from 300 ms to 10.5 seconds for the same 425 files, and Winlator's own file browser
/// was crawling too, with SAFT closed. Nothing was wrong with SAFT. An SD card absorbs writes into a
/// small fast cache, and once a few gigabytes have gone through it, the card falls back to erasing
/// before every write and stays slow for a while afterwards, whatever is running.
///
/// Anything user-facing here says "SD card" rather than "card": the people SAFT is written for read
/// "card" as the graphics card, and telling them their GPU has slowed down would be worse than
/// saying nothing at all.
///
/// From the outside that is indistinguishable from SAFT hanging, and someone watching a progress bar
/// crawl has no way to tell the difference. Naming it is the whole point.
///
/// It measures nothing extra: the bytes are counted by the write loop that was already running, and
/// the clock is a Stopwatch. It never changes what is written, in what order, or whether anything is
/// written at all — it only reads two numbers that already exist.
/// </summary>
public sealed class StorageSpeed
{
    /// <summary>
    /// The line below which sustained writing is considered degraded rather than merely modest.
    ///
    /// Deliberately low. A healthy card in this application manages 25–35 MB/s; a slow-but-fine one
    /// still clears 15. Sitting under 8 MB/s for seconds on end is not "a cheap card", it is a card
    /// that has stopped keeping up, and the honest thing is to say so rather than to nag anyone whose
    /// storage is simply unremarkable.
    /// </summary>
    private const long DegradedBytesPerSecond = 8L * 1024 * 1024;

    /// <summary>
    /// The smallest stretch worth judging. Short samples are noise — a single stalled flush, a
    /// scheduler hiccup — and calling those a degraded card would make the warning worthless.
    /// </summary>
    private static readonly TimeSpan MinimumWindow = TimeSpan.FromSeconds(10);
    private const long MinimumWindowBytes = 4L * 1024 * 1024;

    /// <summary>
    /// How much has to be written before throughput means anything at all.
    ///
    /// The first version judged any write, and cried wolf on the very first install: it reported
    /// 0.5 MB/s after two mods, on a card that was perfectly healthy. The number was real and the
    /// conclusion was nonsense, because a small archive full of small entries is not bandwidth-bound
    /// at all - the time goes on opening and reading thousands of individual files, not on the card
    /// accepting bytes. Dividing bytes by seconds there measures the wrong thing.
    ///
    /// Only a large, sustained write says anything about the card, so nothing under this is judged.
    /// </summary>
    private const long MinimumBytesToJudge = 200L * 1024 * 1024;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastBytes;
    private TimeSpan _lastAt = TimeSpan.Zero;

    private long _worstBytesPerSecond = long.MaxValue;
    private long _totalBytes;
    private TimeSpan _totalTime = TimeSpan.Zero;

    /// <summary>
    /// Feeds in the running total of bytes written so far. Safe to call as often as the write loop
    /// likes; windows shorter than <see cref="MinimumWindow"/> are accumulated rather than judged.
    /// </summary>
    public void Sample(long totalBytesWritten) => SampleAt(totalBytesWritten, _clock.Elapsed);

    /// <summary>
    /// The same, at a stated moment. Only the tests use it, so that proving a ninety-second collapse
    /// does not cost ninety seconds of waiting - the thresholds here are measured in tens of seconds,
    /// and a test suite that sleeps through them is a test suite nobody runs.
    /// </summary>
    internal void SampleAt(long totalBytesWritten, TimeSpan now)
    {
        var bytes = totalBytesWritten - _lastBytes;
        var span = now - _lastAt;

        if (span < MinimumWindow || bytes < MinimumWindowBytes) return;

        var rate = (long)(bytes / span.TotalSeconds);
        if (rate < _worstBytesPerSecond) _worstBytesPerSecond = rate;

        _totalBytes += bytes;
        _totalTime += span;
        _lastBytes = totalBytesWritten;
        _lastAt = now;
    }

    /// <summary>Whether any measured stretch of this write ran below the degraded line.</summary>
    public bool IsDegraded =>
        _totalBytes >= MinimumBytesToJudge &&
        _worstBytesPerSecond != long.MaxValue &&
        _worstBytesPerSecond < DegradedBytesPerSecond;

    /// <summary>Average across every judged window, or 0 if nothing ran long enough to judge.</summary>
    public long AverageBytesPerSecond =>
        _totalTime > TimeSpan.Zero ? (long)(_totalBytes / _totalTime.TotalSeconds) : 0;

    public long SlowestBytesPerSecond => _worstBytesPerSecond == long.MaxValue ? 0 : _worstBytesPerSecond;

    /// <summary>One line for the activity log, whether or not anything was wrong.</summary>
    public string Describe() =>
        AverageBytesPerSecond == 0
            ? "write speed: too short to measure"
            : $"write speed: {Mb(AverageBytesPerSecond)} MB/s average, {Mb(SlowestBytesPerSecond)} MB/s at its slowest" +
              (IsDegraded ? " - DEGRADED" : "");

    /// <summary>
    /// What to tell the user, in their terms, or null when there is nothing worth saying. Written to
    /// be read by someone who just watched a progress bar crawl and wants to know whose fault it was.
    /// </summary>
    public string? Warning() =>
        !IsDegraded
            ? null
            : $"Your SD card slowed to {Mb(SlowestBytesPerSecond)} MB/s while this was being written. " +
              "A healthy one manages 25 MB/s or more.\n\n" +
              "This is the card, not SAFT - everything installed correctly, it just took longer than " +
              "it should have.\n\n" +
              "Leave the card alone for five to ten minutes and it will clear itself. The README " +
              "explains why this happens.";

    private static string Mb(long bytesPerSecond) => (bytesPerSecond / (1024.0 * 1024.0)).ToString("0.0");
}
