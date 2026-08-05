namespace SAFT.Core.Tests;

public class ModInstallerAudioTests
{
    private static (string Name, Func<Stream> OpenContent) File_(string name, string content) =>
        (name, () => new MemoryStream(System.Text.Encoding.ASCII.GetBytes(content)));

    private static string BuildGameRoot()
    {
        var gameRoot = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(gameRoot, "models"));
        File.WriteAllText(Path.Combine(gameRoot, "gta_sa.exe"), "stub exe");
        ImgArchive.Write(Path.Combine(gameRoot, "models", "gta3.img"), new[] { File_("a.dff", "original model") });
        return gameRoot;
    }

    [Fact]
    public void Install_routes_a_wav_into_an_unpacked_sound_slot_by_its_full_path()
    {
        var gameRoot = BuildGameRoot();
        SyntheticAudio.AddSfxPackage(gameRoot, "GENRL", (22050, new byte[] { 1, 0, 2, 0, 3, 0 }));

        var extractDest = TestScratch.NewDir();
        Extractor.Extract(gameRoot, extractDest, includeAudio: true);

        var modSource = TestScratch.NewDir();
        var bankDir = Path.Combine(modSource, "GENRL", "Bank_001");
        Directory.CreateDirectory(bankDir);
        var replacementPcm = new byte[] { 9, 0, 9, 0 };
        using (var f = File.Create(Path.Combine(bankDir, "sound_001.wav")))
            WavPcm.WriteMono16Wav(f, replacementPcm, 22050);

        var result = ModInstaller.Install(extractDest, modSource);

        var routed = Assert.Single(result.AudioRouted);
        Assert.Equal("GENRL/Bank_001/sound_001.wav", routed.MatchKey);
        Assert.Empty(result.AudioUnmatched);

        var (installedPcm, installedRate) = WavPcm.ReadMono16Wav(
            Path.Combine(extractDest, "audio", "sfx", "GENRL", "Bank_001", "sound_001.wav"));
        Assert.Equal(replacementPcm, installedPcm);
        Assert.Equal(22050, installedRate);
    }

    [Fact]
    public void Install_routes_an_ogg_into_an_unpacked_track_slot_by_its_full_path()
    {
        var gameRoot = BuildGameRoot();
        SyntheticAudio.AddStreamStation(gameRoot, "AA", SyntheticAudio.BuildOggLikePayload(1, 64));

        var extractDest = TestScratch.NewDir();
        Extractor.Extract(gameRoot, extractDest, includeAudio: true);

        var modSource = TestScratch.NewDir();
        var stationDir = Path.Combine(modSource, "AA");
        Directory.CreateDirectory(stationDir);
        var replacementPayload = SyntheticAudio.BuildOggLikePayload(2, 32);
        File.WriteAllBytes(Path.Combine(stationDir, "Track_001.ogg"), replacementPayload);

        var result = ModInstaller.Install(extractDest, modSource);

        var routed = Assert.Single(result.AudioRouted);
        Assert.Equal("AA/Track_001.ogg", routed.MatchKey);
        Assert.Empty(result.AudioUnmatched);

        var installedBytes = File.ReadAllBytes(Path.Combine(extractDest, "audio", "streams", "AA", "Track_001.ogg"));
        Assert.Equal(replacementPayload, installedBytes);
    }

    [Fact]
    public void Install_reports_audio_as_unmatched_when_the_package_was_not_extracted_with_audio()
    {
        var gameRoot = BuildGameRoot();
        SyntheticAudio.AddSfxPackage(gameRoot, "GENRL", (22050, new byte[] { 1, 0, 2, 0 }));

        var extractDest = TestScratch.NewDir();
        Extractor.Extract(gameRoot, extractDest, includeAudio: false); // audio left compressed, not unpacked

        var modSource = TestScratch.NewDir();
        var bankDir = Path.Combine(modSource, "GENRL", "Bank_001");
        Directory.CreateDirectory(bankDir);
        using (var f = File.Create(Path.Combine(bankDir, "sound_001.wav")))
            WavPcm.WriteMono16Wav(f, new byte[] { 9, 0, 9, 0 }, 22050);

        var result = ModInstaller.Install(extractDest, modSource);

        Assert.Empty(result.AudioRouted);
        var unmatched = Assert.Single(result.AudioUnmatched);
        Assert.Equal("GENRL/Bank_001/sound_001.wav", unmatched);

        // Must not have fabricated the package's unpack folder just because a mod file matched its name.
        Assert.False(Directory.Exists(Path.Combine(extractDest, "audio", "sfx", "GENRL")));
    }

    [Fact]
    public void Install_reports_audio_as_unmatched_when_the_sound_slot_does_not_exist()
    {
        var gameRoot = BuildGameRoot();
        SyntheticAudio.AddSfxPackage(gameRoot, "GENRL", (22050, new byte[] { 1, 0, 2, 0 })); // only sound_001

        var extractDest = TestScratch.NewDir();
        Extractor.Extract(gameRoot, extractDest, includeAudio: true);

        var modSource = TestScratch.NewDir();
        var bankDir = Path.Combine(modSource, "GENRL", "Bank_001");
        Directory.CreateDirectory(bankDir);
        // sound_002 doesn't exist in this bank at all.
        using (var f = File.Create(Path.Combine(bankDir, "sound_002.wav")))
            WavPcm.WriteMono16Wav(f, new byte[] { 9, 0, 9, 0 }, 22050);

        var result = ModInstaller.Install(extractDest, modSource);

        Assert.Empty(result.AudioRouted);
        Assert.Single(result.AudioUnmatched);
    }

    [Fact]
    public void RebuildNewPlayableCopy_reflects_audio_installed_via_ModInstaller()
    {
        var gameRoot = BuildGameRoot();
        SyntheticAudio.AddSfxPackage(gameRoot, "GENRL", (22050, new byte[] { 1, 0, 2, 0, 3, 0 }));

        var extractDest = TestScratch.NewDir();
        Extractor.Extract(gameRoot, extractDest, includeAudio: true);

        var modSource = TestScratch.NewDir();
        var bankDir = Path.Combine(modSource, "GENRL", "Bank_001");
        Directory.CreateDirectory(bankDir);
        var replacementPcm = new byte[] { 9, 0, 9, 0 };
        using (var f = File.Create(Path.Combine(bankDir, "sound_001.wav")))
            WavPcm.WriteMono16Wav(f, replacementPcm, 22050);

        ModInstaller.Install(extractDest, modSource);

        var outputRoot = TestScratch.NewDir();
        Rebuilder.RebuildNewPlayableCopy(extractDest, outputRoot);

        var rebuiltPackagePath = Path.Combine(outputRoot, "audio", "sfx", "GENRL");
        using var stream = File.OpenRead(rebuiltPackagePath);
        var bank = SfxBank.Read(stream, 0, new FileInfo(rebuiltPackagePath).Length);
        stream.Position = bank.GetPcmOffset(0);
        var patched = new byte[bank.GetPcmLength(0)];
        stream.ReadExactly(patched);

        Assert.Equal(new byte[] { 9, 0, 9, 0, 0, 0 }, patched);
    }
}
