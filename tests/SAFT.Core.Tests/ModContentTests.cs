using Xunit;

namespace SAFT.Core.Tests;

/// <summary>
/// Guards the decision that skips the whole map read. Getting this wrong in the "false" direction
/// silently skips checks the user relies on, so the interesting cases are the ones that must come
/// back true.
/// </summary>
public class ModContentTests
{
    private static string FolderWith(params string[] relativePaths)
    {
        var root = TestScratch.NewDir();
        foreach (var relative in relativePaths)
        {
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, new byte[8]);
        }
        return root;
    }

    [Fact]
    public void ASoundOnlyModDoesNotAffectStreaming()
    {
        var mod = FolderWith(
            "audio/sfx/GENRL/Bank_001/sound_001.wav",
            "audio/sfx/GENRL/Bank_001/sound_002.wav",
            "audio/streams/AA/Track_001.ogg");

        Assert.False(ModContent.AffectsStreaming(mod));
    }

    [Theory]
    [InlineData("saftball.dff")]
    [InlineData("saftball.txd")]
    [InlineData("saft_test.col")]
    [InlineData("dance.ifp")]
    [InlineData("saft_test.ide")]
    [InlineData("saft_test.ipl")]
    public void AnythingThatCanMoveTheMapOrTheWeightDoes(string fileName)
    {
        var mod = FolderWith("audio/sfx/GENRL/Bank_001/sound_001.wav", fileName);
        Assert.True(ModContent.AffectsStreaming(mod));
    }

    [Fact]
    public void FindsRelevantFilesNestedAnywhere()
    {
        // Mod packs nest freely; a .dff three folders down still needs the full analysis.
        var mod = FolderWith("stuff/models/nested/deep/saftball.dff");
        Assert.True(ModContent.AffectsStreaming(mod));
    }

    [Fact]
    public void IgnoresTheSameJunkTheInstallerIgnores()
    {
        // ._ AppleDouble files appear all over a folder copied from a Mac to an SD card — one named
        // "._something.dff" must not drag a sound pack through the entire map read.
        var mod = FolderWith(
            "audio/sfx/GENRL/Bank_001/sound_001.wav",
            "audio/sfx/GENRL/Bank_001/._sound_001.wav",
            "._saftball.dff",
            "readme.txt");

        Assert.False(ModContent.AffectsStreaming(mod));
    }

    [Fact]
    public void AMissingFolderGetsTheFullAnalysisRatherThanASkip()
    {
        // Erring towards true: never skip a user's checks because of an unreadable folder.
        Assert.True(ModContent.AffectsStreaming(Path.Combine(TestScratch.NewDir(), "does-not-exist")));
    }

    [Fact]
    public void TheSkippedVerdictSaysTheChecksWereNotRunRatherThanReportingZeroes()
    {
        var verdict = StreamingAdvice.ComposeWithoutStreamingContent();

        Assert.True(verdict.WithinRange);
        Assert.False(verdict.NeedsConfirmation);
        Assert.Equal(StreamingSeverity.Fine, verdict.Severity);
        Assert.Contains("were not run", verdict.Message);

        // Must not imply SAFT weighed this player's game and found it healthy — it didn't look.
        Assert.DoesNotContain("0.0 MB", verdict.Message);
        Assert.DoesNotContain("unlikely to be related", verdict.Message);
    }
}
