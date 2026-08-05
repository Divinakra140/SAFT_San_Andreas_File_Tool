namespace SAFT.Core.Tests;

public class ExtractRebuildAudioTests
{
    private static (string Name, Func<Stream> OpenContent) File_(string name, string content) =>
        (name, () => new MemoryStream(System.Text.Encoding.ASCII.GetBytes(content)));

    private static string BuildGameRoot()
    {
        var gameRoot = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(gameRoot, "models"));
        File.WriteAllText(Path.Combine(gameRoot, "gta_sa.exe"), "stub exe");
        File.WriteAllText(Path.Combine(gameRoot, "models", "hud.txd"), "loose texture, not archived");

        ImgArchive.Write(Path.Combine(gameRoot, "models", "gta3.img"), new[] { File_("a.dff", "original model") });
        return gameRoot;
    }

    [Fact]
    public void Extract_without_audio_leaves_sfx_and_stream_packages_as_untouched_loose_files()
    {
        var gameRoot = BuildGameRoot();
        SyntheticAudio.AddSfxPackage(gameRoot, "GENRL", (22050, new byte[] { 1, 0, 2, 0 }));
        SyntheticAudio.AddStreamStation(gameRoot, "AA", SyntheticAudio.BuildOggLikePayload(1, 64));

        var extractDest = TestScratch.NewDir();
        var manifest = Extractor.Extract(gameRoot, extractDest, includeAudio: false);

        Assert.Empty(manifest.UnpackedAudioPackages);
        Assert.Empty(manifest.UnpackedStreamStations);

        // The loose game copy must include hud.txd (not just the archives) ...
        Assert.Equal("loose texture, not archived", File.ReadAllText(Path.Combine(extractDest, "models", "hud.txd")));

        // ... and the SFX/stream package files themselves, copied byte-for-byte, not unpacked.
        var extractedPackage = Path.Combine(extractDest, "audio", "sfx", "GENRL");
        Assert.True(File.Exists(extractedPackage));
        Assert.Equal(File.ReadAllBytes(Path.Combine(gameRoot, "audio", "sfx", "GENRL")), File.ReadAllBytes(extractedPackage));

        var extractedStation = Path.Combine(extractDest, "audio", "streams", "AA");
        Assert.True(File.Exists(extractedStation));
        Assert.Equal(File.ReadAllBytes(Path.Combine(gameRoot, "audio", "streams", "AA")), File.ReadAllBytes(extractedStation));

        Assert.False(Directory.Exists(Path.Combine(extractDest, "audio", "sfx", "GENRL", "Bank_001")));
    }

    [Fact]
    public void Extract_with_audio_unpacks_every_sound_and_track_and_records_them_in_the_manifest()
    {
        var gameRoot = BuildGameRoot();
        var pcmA = new byte[] { 1, 0, 2, 0, 3, 0 };
        var pcmB = new byte[] { 4, 0, 5, 0 };
        SyntheticAudio.AddSfxPackage(gameRoot, "GENRL", (22050, pcmA), (16000, pcmB));
        var oggPayload = SyntheticAudio.BuildOggLikePayload(7, 40);
        SyntheticAudio.AddStreamStation(gameRoot, "AA", oggPayload);

        var extractDest = TestScratch.NewDir();
        var manifest = Extractor.Extract(gameRoot, extractDest, includeAudio: true);

        Assert.Equal(new[] { "GENRL" }, manifest.UnpackedAudioPackages);
        Assert.Equal(new[] { "AA" }, manifest.UnpackedStreamStations);

        // Unpacked into individual files, following the Package/Bank_NNN/sound_NNN.wav convention...
        var sound1 = Path.Combine(extractDest, "audio", "sfx", "GENRL", "Bank_001", "sound_001.wav");
        var sound2 = Path.Combine(extractDest, "audio", "sfx", "GENRL", "Bank_001", "sound_002.wav");
        Assert.True(File.Exists(sound1));
        Assert.True(File.Exists(sound2));
        var (readPcmA, sampleRateA) = WavPcm.ReadMono16Wav(sound1);
        Assert.Equal(pcmA, readPcmA);
        Assert.Equal(22050, sampleRateA);
        var (readPcmB, _) = WavPcm.ReadMono16Wav(sound2);
        Assert.Equal(pcmB, readPcmB);

        // ... and the original (obfuscated) package file must NOT be copied as a loose file too.
        Assert.False(File.Exists(Path.Combine(extractDest, "audio", "sfx", "GENRL")));

        // Streamed track decrypted to a plain, playable Ogg file.
        var track1 = Path.Combine(extractDest, "audio", "streams", "AA", "Track_001.ogg");
        Assert.True(File.Exists(track1));
        Assert.Equal(oggPayload, File.ReadAllBytes(track1));
        Assert.False(File.Exists(Path.Combine(extractDest, "audio", "streams", "AA")));

        // Everything else about the extraction (archives, loose files) still behaves as normal.
        Assert.Equal("loose texture, not archived", File.ReadAllText(Path.Combine(extractDest, "models", "hud.txd")));
        Assert.True(File.Exists(Path.Combine(extractDest, "models", "gta3.img", "dff", "a.dff")));
    }

    [Fact]
    public void RebuildNewPlayableCopy_reconstitutes_an_unpacked_sfx_package_with_edited_sounds_patched_in()
    {
        var gameRoot = BuildGameRoot();
        var originalPcm = new byte[] { 1, 0, 2, 0, 3, 0 }; // 6 bytes allocated
        var untouchedPcm = new byte[] { 4, 0, 5, 0 };
        SyntheticAudio.AddSfxPackage(gameRoot, "GENRL", (22050, originalPcm), (16000, untouchedPcm));
        var originalPackageBytes = File.ReadAllBytes(Path.Combine(gameRoot, "audio", "sfx", "GENRL"));

        var extractDest = TestScratch.NewDir();
        Extractor.Extract(gameRoot, extractDest, includeAudio: true);

        // Edit sound_001 (shorter, fits) and leave sound_002 completely untouched.
        var sound1Path = Path.Combine(extractDest, "audio", "sfx", "GENRL", "Bank_001", "sound_001.wav");
        var replacementPcm = new byte[] { 9, 0, 9, 0 };
        using (var f = File.Create(sound1Path))
            WavPcm.WriteMono16Wav(f, replacementPcm, 22050);

        var outputRoot = TestScratch.NewDir();
        Rebuilder.RebuildNewPlayableCopy(extractDest, outputRoot);

        var rebuiltPackagePath = Path.Combine(outputRoot, "audio", "sfx", "GENRL");
        Assert.True(File.Exists(rebuiltPackagePath));

        using var stream = File.OpenRead(rebuiltPackagePath);
        var bank = SfxBank.Read(stream, 0, new FileInfo(rebuiltPackagePath).Length);

        stream.Position = bank.GetPcmOffset(0);
        var patchedSound1 = new byte[bank.GetPcmLength(0)];
        stream.ReadExactly(patchedSound1);
        Assert.Equal(new byte[] { 9, 0, 9, 0, 0, 0 }, patchedSound1); // replacement + zero padding

        stream.Position = bank.GetPcmOffset(1);
        var untouchedSound2 = new byte[bank.GetPcmLength(1)];
        stream.ReadExactly(untouchedSound2);
        Assert.Equal(untouchedPcm, untouchedSound2); // never edited -> carried over exactly as original

        // The original game install must be completely untouched by a "new folder" rebuild.
        Assert.Equal(originalPackageBytes, File.ReadAllBytes(Path.Combine(gameRoot, "audio", "sfx", "GENRL")));
    }

    [Fact]
    public void RebuildNewPlayableCopy_leaves_an_oversized_sound_replacement_as_the_original()
    {
        var gameRoot = BuildGameRoot();
        SyntheticAudio.AddSfxPackage(gameRoot, "GENRL", (22050, new byte[] { 1, 0, 2, 0 })); // 4 bytes allocated

        var extractDest = TestScratch.NewDir();
        Extractor.Extract(gameRoot, extractDest, includeAudio: true);

        var sound1Path = Path.Combine(extractDest, "audio", "sfx", "GENRL", "Bank_001", "sound_001.wav");
        var tooBig = new byte[200];
        using (var f = File.Create(sound1Path))
            WavPcm.WriteMono16Wav(f, tooBig, 22050);

        var outputRoot = TestScratch.NewDir();
        Rebuilder.RebuildNewPlayableCopy(extractDest, outputRoot);

        var rebuiltPackagePath = Path.Combine(outputRoot, "audio", "sfx", "GENRL");
        using var stream = File.OpenRead(rebuiltPackagePath);
        var bank = SfxBank.Read(stream, 0, new FileInfo(rebuiltPackagePath).Length);
        stream.Position = bank.GetPcmOffset(0);
        var sound1 = new byte[bank.GetPcmLength(0)];
        stream.ReadExactly(sound1);

        Assert.Equal(new byte[] { 1, 0, 2, 0 }, sound1); // too big to fit -> original left in place
    }

    [Fact]
    public void RebuildNewPlayableCopy_reconstitutes_an_unpacked_stream_station_with_an_edited_track()
    {
        var gameRoot = BuildGameRoot();
        var originalPayload = SyntheticAudio.BuildOggLikePayload(1, 64);
        SyntheticAudio.AddStreamStation(gameRoot, "AA", originalPayload);

        var extractDest = TestScratch.NewDir();
        Extractor.Extract(gameRoot, extractDest, includeAudio: true);

        var track1Path = Path.Combine(extractDest, "audio", "streams", "AA", "Track_001.ogg");
        var replacementPayload = SyntheticAudio.BuildOggLikePayload(2, 20); // shorter, fits
        File.WriteAllBytes(track1Path, replacementPayload);

        var outputRoot = TestScratch.NewDir();
        Rebuilder.RebuildNewPlayableCopy(extractDest, outputRoot);

        var rebuiltStationPath = Path.Combine(outputRoot, "audio", "streams", "AA");
        using var stream = File.OpenRead(rebuiltStationPath);
        stream.Position = StreamIndex.TrackHeaderSize;
        var encrypted = new byte[64]; // original allocated space
        stream.ReadExactly(encrypted);
        var decrypted = StreamXor.Transform(encrypted, StreamIndex.TrackHeaderSize);

        var expected = new byte[64];
        replacementPayload.CopyTo(expected, 0);
        Assert.Equal(expected, decrypted);

        var slot = StreamIndex.FindActiveLengthSlot(stream, 0);
        Assert.Equal(0, slot);
    }

    [Fact]
    public void RebuildInPlace_backs_up_an_unpacked_sfx_package_as_dot_bak_before_overwriting_it()
    {
        var gameRoot = BuildGameRoot();
        var originalPcm = new byte[] { 1, 0, 2, 0, 3, 0 };
        SyntheticAudio.AddSfxPackage(gameRoot, "GENRL", (22050, originalPcm));
        var originalPackageBytes = File.ReadAllBytes(Path.Combine(gameRoot, "audio", "sfx", "GENRL"));

        var extractDest = TestScratch.NewDir();
        Extractor.Extract(gameRoot, extractDest, includeAudio: true);

        var sound1Path = Path.Combine(extractDest, "audio", "sfx", "GENRL", "Bank_001", "sound_001.wav");
        using (var f = File.Create(sound1Path))
            WavPcm.WriteMono16Wav(f, new byte[] { 9, 0, 9, 0 }, 22050);

        Rebuilder.RebuildInPlace(extractDest, gameRoot, makeBackups: true);

        var backupPath = Path.Combine(gameRoot, "audio", "sfx", "GENRL.bak");
        Assert.True(File.Exists(backupPath));
        Assert.Equal(originalPackageBytes, File.ReadAllBytes(backupPath));

        using var stream = File.OpenRead(Path.Combine(gameRoot, "audio", "sfx", "GENRL"));
        var bank = SfxBank.Read(stream, 0, new FileInfo(Path.Combine(gameRoot, "audio", "sfx", "GENRL")).Length);
        stream.Position = bank.GetPcmOffset(0);
        var patched = new byte[bank.GetPcmLength(0)];
        stream.ReadExactly(patched);
        Assert.Equal(new byte[] { 9, 0, 9, 0, 0, 0 }, patched);
    }

    [Fact]
    public void RebuildInPlace_carries_edited_loose_files_like_hud_txd_back_into_the_game_folder()
    {
        var gameRoot = BuildGameRoot();

        var extractDest = TestScratch.NewDir();
        Extractor.Extract(gameRoot, extractDest, includeAudio: false);

        File.WriteAllText(Path.Combine(extractDest, "models", "hud.txd"), "MODDED hud icons");

        Rebuilder.RebuildInPlace(extractDest, gameRoot, makeBackups: true);

        Assert.Equal("MODDED hud icons", File.ReadAllText(Path.Combine(gameRoot, "models", "hud.txd")));
    }
}
