using Xunit;

namespace SAFT.Core.Tests;

/// <summary>
/// Covers the demand-driven SFX slot lookup that replaced the full 61,993-slot index. The important
/// property is that looking up a key returns exactly what a full scan of the game would have put
/// under that key — the whole change is only safe if those two agree.
/// </summary>
public class AudioSlotLookupTests
{
    [Theory]
    [InlineData("GENRL/Bank_003/sound_047.wav", "GENRL", 3, 47)]
    [InlineData("SPC_PA/Bank_001/sound_001.wav", "SPC_PA", 1, 1)]
    [InlineData("FEET/Bank_120/sound_200.wav", "FEET", 120, 200)]
    public void ParsesAKeyIntoItsThreeParts(string key, string expectedPackage, int expectedBank, int expectedSound)
    {
        Assert.True(DirectModInstaller.TryParseAudioKey(key, out var package, out var bank, out var sound));
        Assert.Equal(expectedPackage, package);
        Assert.Equal(expectedBank, bank);
        Assert.Equal(expectedSound, sound);
    }

    [Theory]
    [InlineData("sound_001.wav")]                      // no package or bank segment
    [InlineData("GENRL/sound_001.wav")]                // missing the bank segment
    [InlineData("mod/GENRL/Bank_003/sound_047.wav")]   // too many segments
    [InlineData("GENRL/Folder/sound_047.wav")]         // bank segment isn't a bank
    [InlineData("GENRL/Bank_003/noise.wav")]           // sound segment isn't a slot
    [InlineData("GENRL/Bank_xxx/sound_047.wav")]       // bank number isn't a number
    [InlineData("GENRL/Bank_003/sound_xxx.wav")]       // sound number isn't a number
    public void RejectsAnythingThatIsNotASlotKey(string key)
    {
        // A .wav the user dropped somewhere other than the extracted layout. It must fall through to
        // being reported as unmatched, never be guessed at.
        Assert.False(DirectModInstaller.TryParseAudioKey(key, out _, out _, out _));
    }

    [Fact]
    public void FindsARealSlotAndReportsItsOriginalLength()
    {
        var gameRoot = TestScratch.NewDir();

        var first = new byte[64];
        var second = new byte[128];
        SyntheticAudio.AddSfxPackage(gameRoot, "GENRL", (22050, first), (22050, second));

        var found = DirectModInstaller.LookUpAudioSlots(
            gameRoot, new[] { "GENRL/Bank_001/sound_002.wav" });

        var slot = Assert.Single(found);
        Assert.Equal("GENRL/Bank_001/sound_002.wav", slot.Key);
        Assert.Equal(1, slot.Value.SoundIndex);
        Assert.Equal(second.Length, slot.Value.OriginalPcmLength);
    }

    [Fact]
    public void ReturnsNothingForSlotsTheGameDoesNotHave()
    {
        var gameRoot = TestScratch.NewDir();
        SyntheticAudio.AddSfxPackage(gameRoot, "GENRL", (22050, new byte[64]));

        var found = DirectModInstaller.LookUpAudioSlots(gameRoot, new[]
        {
            "NOSUCH/Bank_001/sound_001.wav",  // package the game doesn't have
            "GENRL/Bank_099/sound_001.wav",   // bank past the end of the package
            "GENRL/Bank_001/sound_099.wav",   // slot past the end of the bank
        });

        Assert.Empty(found);
    }

    [Fact]
    public void ReadsOneBankOnceForManySoundsInIt()
    {
        var gameRoot = TestScratch.NewDir();
        SyntheticAudio.AddSfxPackage(gameRoot, "GENRL",
            (22050, new byte[16]), (22050, new byte[32]), (22050, new byte[48]));

        var found = DirectModInstaller.LookUpAudioSlots(gameRoot, new[]
        {
            "GENRL/Bank_001/sound_001.wav",
            "GENRL/Bank_001/sound_003.wav",
            "GENRL/Bank_001/sound_001.wav", // duplicate — must not confuse the grouping
        });

        Assert.Equal(2, found.Count);
        Assert.Equal(16, found["GENRL/Bank_001/sound_001.wav"].OriginalPcmLength);
        Assert.Equal(48, found["GENRL/Bank_001/sound_003.wav"].OriginalPcmLength);
    }

    /// <summary>
    /// The equivalence check that actually justifies the change, run against a real install: scan
    /// every slot the old way, then ask the new lookup for each one and require an identical answer.
    /// Opt-in via SAFT_REAL_GAME because it needs a real game folder, which no other test does.
    /// </summary>
    [Fact]
    public void MatchesAFullScanOfARealGame()
    {
        var gameRoot = Environment.GetEnvironmentVariable("SAFT_REAL_GAME");
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot)) return;

        // The full scan, exactly as the old BuildAudioIndex did it.
        var expected = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var packages = SfxIndex.Load(gameRoot);
        foreach (var pkg in packages)
        {
            using var stream = File.OpenRead(pkg.AbsolutePath);
            for (var bankNum = 1; bankNum <= pkg.Banks.Count; bankNum++)
            {
                var (offset, length) = pkg.Banks[bankNum - 1];
                var bank = SfxBank.Read(stream, offset, length);
                for (var soundIdx = 0; soundIdx < bank.Sounds.Count; soundIdx++)
                    expected[$"{pkg.Name}/Bank_{bankNum:D3}/sound_{soundIdx + 1:D3}.wav"] = bank.GetPcmLength(soundIdx);
            }
        }

        Assert.NotEmpty(expected);

        var actual = DirectModInstaller.LookUpAudioSlots(gameRoot, expected.Keys);

        Assert.Equal(expected.Count, actual.Count);
        foreach (var (key, originalPcmLength) in expected)
        {
            Assert.True(actual.TryGetValue(key, out var slot), $"lookup missed {key}");
            Assert.Equal(originalPcmLength, slot.OriginalPcmLength);
        }
    }
}
