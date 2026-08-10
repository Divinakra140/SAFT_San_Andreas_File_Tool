using System.Diagnostics;

namespace SAFT.Core;

/// <summary>
/// Passes progress updates through no faster than a person can read them, and drops the rest.
///
/// This exists because reporting per file made extraction take HOURS under Winlator. A full
/// extraction is 21,474 files, and every one of them called <see cref="IProgress{T}.Report"/>.
/// That is not a cheap call from a worker thread: it posts to the UI thread's message queue, and
/// the handler on the other side sets two progress bars and a label — three control repaints —
/// before it returns. 21,000 round trips through Wine's GDI is the whole cost, and none of it
/// buys anything, because no one can read a label that changes two hundred times a second.
///
/// The worker never blocks on Report, so the queue simply grew: the UI fell further and further
/// behind the work it was describing, which is also why it looked frozen rather than slow.
///
/// Ten updates a second is far more than enough to look alive. <see cref="ReportNow"/> exists for
/// the updates that must not be dropped — the last one of a stage, where the bar has to actually
/// reach the end instead of stopping at whatever the last tick happened to catch.
/// </summary>
public sealed class ThrottledProgress<T> : IProgress<T>
{
    private static readonly long DefaultIntervalTicks = Stopwatch.Frequency / 10;   // 100ms

    private readonly IProgress<T>? _inner;
    private readonly long _intervalTicks;
    private long _lastReportTicks;

    public ThrottledProgress(IProgress<T>? inner, TimeSpan? interval = null)
    {
        _inner = inner;
        _intervalTicks = interval is { } t ? (long)(t.TotalSeconds * Stopwatch.Frequency) : DefaultIntervalTicks;

        // Timestamped a full interval in the past so the very first update goes straight through and
        // the user sees something immediately rather than after a beat of apparent nothing.
        _lastReportTicks = Stopwatch.GetTimestamp() - _intervalTicks;
    }

    public void Report(T value)
    {
        if (_inner is null) return;

        var now = Stopwatch.GetTimestamp();
        if (now - _lastReportTicks < _intervalTicks) return;

        _lastReportTicks = now;
        _inner.Report(value);
    }

    /// <summary>Passes an update through regardless of timing — for the last one of a stage.</summary>
    public void ReportNow(T value)
    {
        if (_inner is null) return;

        _lastReportTicks = Stopwatch.GetTimestamp();
        _inner.Report(value);
    }
}
