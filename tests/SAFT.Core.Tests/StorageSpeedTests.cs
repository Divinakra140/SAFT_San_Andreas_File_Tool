using SAFT.Core;

namespace SAFT.Core.Tests;

/// <summary>
/// The storage warning has one job and one danger. The job: say plainly when the drive underneath has
/// slowed to a crawl, because from the outside that is indistinguishable from SAFT having hung. The
/// danger: crying wolf. A warning that fires on a healthy install teaches people to ignore it, and
/// then it is worse than not having it.
/// </summary>
public class StorageSpeedTests
{
    private const long Mb = 1024 * 1024;

    /// <summary>
    /// Feeds samples with controlled timing. Real writes report bytes as they land; these tests need
    /// the same shape without waiting minutes, so each step sleeps only as long as the window demands.
    /// </summary>
    /// <summary>
    /// Feeds samples with controlled timing. The judgement only applies to writes big enough to say
    /// anything about a drive, so these deliberately move hundreds of megabytes - a small archive full
    /// of small entries is limited by per-file overhead, not by the card, and judging those is what
    /// made the first version report a healthy card as failing.
    /// </summary>
    private static StorageSpeed Feed(params (long Megabytes, int Milliseconds)[] steps)
    {
        var speed = new StorageSpeed();
        long total = 0;
        var at = TimeSpan.Zero;

        foreach (var (megabytes, milliseconds) in steps)
        {
            at += TimeSpan.FromMilliseconds(milliseconds);
            total += megabytes * Mb;
            speed.SampleAt(total, at);
        }
        return speed;
    }

    [Fact]
    public void SaysNothingWhenTheDriveIsKeepingUp()
    {
        // 400 MB in 11 seconds is about 36 MB/s — a healthy card, and nobody should be told anything.
        var speed = Feed((400, 11000));

        Assert.False(speed.IsDegraded);
        Assert.Null(speed.Warning());
    }

    [Fact]
    public void SaysSomethingWhenTheDriveHasCollapsed()
    {
        // 300 MB in 100 seconds is 3 MB/s. That is the shape of the real measurement: a 940 MB archive
        // that had been taking 30 seconds took 183.
        var speed = Feed((300, 100000));

        Assert.True(speed.IsDegraded);
        var warning = speed.Warning();
        Assert.NotNull(warning);

        // It must name the SD card, not "the card" — the people this is written for read that as
        // their graphics card, and telling them their GPU slowed down is worse than saying nothing.
        Assert.Contains("SD card", warning);
        Assert.DoesNotContain("your card", warning, StringComparison.OrdinalIgnoreCase);

        // And it has to answer the two questions anyone actually has: why did this happen, and how
        // long do I wait. A number they can act on beats "for a while".
        Assert.Contains("five to ten minutes", warning);
        Assert.Contains("not SAFT", warning);
    }

    [Fact]
    public void IgnoresStretchesTooShortToMeanAnything()
    {
        // A single stalled flush is not a failing drive. Judging a window this small would make the
        // warning fire on healthy installs, which is the one way this feature can do harm.
        var speed = Feed((1, 50), (1, 50), (1, 50));

        Assert.False(speed.IsDegraded);
        Assert.Null(speed.Warning());
        Assert.Equal(0, speed.AverageBytesPerSecond);
        Assert.Contains("too short to measure", speed.Describe());
    }

    [Fact]
    public void OneBadStretchIsEnoughEvenIfTheRestWasFine()
    {
        // The real failure is a write that starts fine and collapses part way through - a big install
        // that begins at full speed and finishes at walking pace. Averaging that away would hide
        // exactly the case worth reporting.
        var speed = Feed((400, 11000), (100, 60000));

        Assert.True(speed.IsDegraded);
        Assert.True(speed.AverageBytesPerSecond > speed.SlowestBytesPerSecond);
    }

    [Fact]
    public void ReportsItselfEvenWhenNothingIsWrong()
    {
        // The log gets a line either way. Half the value of this is being able to look back at a
        // session afterwards and see what the drive was actually doing at the time.
        var speed = Feed((400, 11000));

        Assert.Contains("MB/s average", speed.Describe());
        Assert.DoesNotContain("DEGRADED", speed.Describe());
    }

    [Fact]
    public void SaysNothingAboutASmallWriteHoweverSlowItLooks()
    {
        // The false alarm that made it onto a real screen: two mods installed, a healthy SD card, and
        // SAFT announcing 0.5 MB/s. The rate was real - a small archive of small entries spends its
        // time opening files, not filling the card - but the conclusion was wrong. Nothing short
        // enough to be dominated by per-file overhead gets an opinion.
        var speed = Feed((20, 40000));   // 20 MB in 40 seconds: 0.5 MB/s, and meaningless

        Assert.False(speed.IsDegraded);
        Assert.Null(speed.Warning());
    }

    [Fact]
    public void TheWarningIsShortEnoughToFitOnAWinlatorScreen()
    {
        // 960x544 is the resolution SAFT is used at, and a dialog that overflows it puts its own OK
        // button off the bottom of the display where it cannot be pressed. That happened. Length is a
        // correctness property here, not a matter of taste.
        var warning = Feed((300, 100000)).Warning();

        Assert.NotNull(warning);
        Assert.True(warning!.Length < 400, $"warning is {warning.Length} characters, too long for the screen");
        Assert.True(warning.Split("\n\n").Length <= 3, "more than three short paragraphs will not fit");
    }
}
