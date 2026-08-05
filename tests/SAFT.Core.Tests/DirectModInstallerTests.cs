using System.Text;
using SAFT.Core;

namespace SAFT.Core.Tests;

public class DirectModInstallerTests
{
    private static (string Name, Func<Stream> OpenContent) File_(string name, string content) =>
        (name, () => new MemoryStream(Encoding.ASCII.GetBytes(content)));

    private static string BuildGameRoot()
    {
        var gameRoot = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(gameRoot, "models"));
        File.WriteAllText(Path.Combine(gameRoot, "gta_sa.exe"), "stub");

        // banshee.dff gets one sector (2048 bytes) of allocated space — plenty of room to shrink
        // into, but a same-or-smaller replacement patches in place; something bigger than one
        // sector forces a rebuild.
        ImgArchive.Write(Path.Combine(gameRoot, "models", "gta3.img"), new[]
        {
            File_("banshee.dff", "original car model"),
            File_("banshee.txd", "original car texture"),
            File_("untouched.col", "should never change"),
        });

        return gameRoot;
    }

    [Fact]
    public void Plan_matches_by_name_and_flags_only_oversized_replacements_for_rebuild()
    {
        var gameRoot = BuildGameRoot();
        var modSource = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(modSource, "banshee.dff"), "small replacement, fits fine");
        File.WriteAllText(Path.Combine(modSource, "banshee.txd"), new string('X', ImgEntry.SectorSize + 500)); // bigger than 1 allocated sector
        File.WriteAllText(Path.Combine(modSource, "brand_new.dff"), "no original entry for this name");

        var plan = DirectModInstaller.Plan(gameRoot, modSource);

        Assert.Equal(2, plan.Matches.Count);
        Assert.Single(plan.Unmatched);
        Assert.Equal("brand_new.dff", plan.Unmatched[0]);

        var dffMatch = plan.Matches.Single(m => m.FileName == "banshee.dff");
        var txdMatch = plan.Matches.Single(m => m.FileName == "banshee.txd");
        Assert.False(dffMatch.RequiresRebuild);
        Assert.True(txdMatch.RequiresRebuild);
        Assert.True(plan.AnyArchiveNeedsRebuild);
    }

    [Fact]
    public void Apply_patches_in_place_without_touching_unrelated_entries_when_nothing_needs_rebuild()
    {
        var gameRoot = BuildGameRoot();
        var archivePath = Path.Combine(gameRoot, "models", "gta3.img");
        var originalBytes = File.ReadAllBytes(archivePath);

        var modSource = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(modSource, "banshee.dff"), "MODDED"); // shorter than original

        var plan = DirectModInstaller.Plan(gameRoot, modSource);
        Assert.False(plan.AnyArchiveNeedsRebuild);

        var result = DirectModInstaller.Apply(plan, backupOutputFolder: null);

        var summary = Assert.Single(result.Archives);
        Assert.False(summary.Rebuilt);
        Assert.Equal(1, summary.FilesReplaced);

        // File is the exact same length as before (patch in place never resizes the archive).
        Assert.Equal(originalBytes.Length, new FileInfo(archivePath).Length);

        using var archive = ImgArchive.Open(archivePath);
        Assert.Equal(
            new[] { "banshee.dff", "banshee.txd", "untouched.col" },
            archive.Entries.Select(e => e.Name));

        using var dff = archive.OpenEntry(archive.Entries[0]);
        using var dffReader = new StreamReader(dff, Encoding.ASCII);
        Assert.StartsWith("MODDED", dffReader.ReadToEnd());

        using var untouched = archive.OpenEntry(archive.Entries[2]);
        using var untouchedReader = new StreamReader(untouched, Encoding.ASCII);
        Assert.StartsWith("should never change", untouchedReader.ReadToEnd());
    }

    [Fact]
    public void Apply_rebuilds_the_archive_when_a_replacement_is_too_big_to_patch()
    {
        var gameRoot = BuildGameRoot();
        var archivePath = Path.Combine(gameRoot, "models", "gta3.img");

        var modSource = TestScratch.NewDir();
        var bigContent = new string('X', ImgEntry.SectorSize + 500);
        File.WriteAllText(Path.Combine(modSource, "banshee.txd"), bigContent);

        var plan = DirectModInstaller.Plan(gameRoot, modSource);
        Assert.True(plan.AnyArchiveNeedsRebuild);

        var summary = Assert.Single(DirectModInstaller.Apply(plan, backupOutputFolder: null).Archives);
        Assert.True(summary.Rebuilt);

        using var archive = ImgArchive.Open(archivePath);
        Assert.Equal(
            new[] { "banshee.dff", "banshee.txd", "untouched.col" },
            archive.Entries.Select(e => e.Name));

        using var txd = archive.OpenEntry(archive.Entries[1]);
        using var txdReader = new StreamReader(txd, Encoding.ASCII);
        Assert.StartsWith(bigContent, txdReader.ReadToEnd());

        // Untouched entries must have been carried over from the live archive, not lost.
        using var dff = archive.OpenEntry(archive.Entries[0]);
        using var dffReader = new StreamReader(dff, Encoding.ASCII);
        Assert.StartsWith("original car model", dffReader.ReadToEnd());
    }

    [Fact]
    public void Apply_backs_up_original_entry_content_before_replacing_when_a_folder_is_given()
    {
        var gameRoot = BuildGameRoot();
        var modSource = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(modSource, "banshee.dff"), "MODDED");

        var backupFolder = TestScratch.NewDir();
        var plan = DirectModInstaller.Plan(gameRoot, modSource);
        DirectModInstaller.Apply(plan, backupOutputFolder: backupFolder);

        var backedUpFile = Path.Combine(backupFolder, "models", "gta3.img", "dff", "banshee.dff");
        Assert.True(File.Exists(backedUpFile));
        Assert.StartsWith("original car model", File.ReadAllText(backedUpFile));
    }

    [Fact]
    public void Plan_reports_exactly_which_archives_need_a_rebuild_and_the_total_archive_count()
    {
        var gameRoot = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(gameRoot, "models"));
        File.WriteAllText(Path.Combine(gameRoot, "gta_sa.exe"), "stub");

        ImgArchive.Write(Path.Combine(gameRoot, "models", "gta3.img"), new[] { File_("banshee.dff", "original") });
        ImgArchive.Write(Path.Combine(gameRoot, "models", "cutscene.img"), new[] { File_("csm16.dff", "original") });

        var modSource = TestScratch.NewDir();
        // Fits fine — gta3.img should NOT need a rebuild.
        File.WriteAllText(Path.Combine(modSource, "banshee.dff"), "small");
        // Too big — cutscene.img SHOULD need a rebuild.
        File.WriteAllText(Path.Combine(modSource, "csm16.dff"), new string('X', ImgEntry.SectorSize + 500));

        var plan = DirectModInstaller.Plan(gameRoot, modSource);

        Assert.Equal(2, plan.TotalArchivesInGame);
        Assert.Equal(new[] { Path.Combine("models", "cutscene.img") }, plan.ArchivesNeedingRebuild);
    }

    [Fact]
    public void Plan_matches_audio_by_package_bank_sound_path_not_bare_filename()
    {
        var gameRoot = BuildGameRoot();
        var pcmA = new byte[] { 1, 0, 2, 0, 3, 0 };
        var pcmB = new byte[] { 4, 0, 5, 0 };
        SyntheticAudio.AddSfxPackage(gameRoot, "GENRL", (22050, pcmA), (16000, pcmB));

        var modSource = TestScratch.NewDir();
        var bankDir = Path.Combine(modSource, "GENRL", "Bank_001");
        Directory.CreateDirectory(bankDir);
        var replacementPcm = new byte[] { 9, 0, 9, 0 }; // same length as original sound_001 (6 bytes) -> shorter, fits
        using (var f = File.Create(Path.Combine(bankDir, "sound_001.wav")))
            WavPcm.WriteMono16Wav(f, replacementPcm, 22050);

        var plan = DirectModInstaller.Plan(gameRoot, modSource);

        var match = Assert.Single(plan.AudioMatches);
        Assert.Equal("GENRL/Bank_001/sound_001.wav", match.MatchKey);
        Assert.True(match.Fits);
        Assert.Empty(plan.AudioUnmatched);
    }

    [Fact]
    public void Plan_flags_oversized_audio_replacement_as_not_fitting_instead_of_matching_the_wrong_sound()
    {
        var gameRoot = BuildGameRoot();
        SyntheticAudio.AddSfxPackage(gameRoot, "GENRL", (22050, new byte[] { 1, 0, 2, 0 })); // sound_001: 4 bytes allocated

        var modSource = TestScratch.NewDir();
        var bankDir = Path.Combine(modSource, "GENRL", "Bank_001");
        Directory.CreateDirectory(bankDir);
        var tooBig = new byte[100]; // way more than the 4 allocated bytes
        using (var f = File.Create(Path.Combine(bankDir, "sound_001.wav")))
            WavPcm.WriteMono16Wav(f, tooBig, 22050);

        var plan = DirectModInstaller.Plan(gameRoot, modSource);

        var match = Assert.Single(plan.AudioMatches);
        Assert.False(match.Fits);
        Assert.Empty(plan.AudioMatchesThatFit);
        Assert.Single(plan.AudioMatchesTooLarge);
    }

    [Fact]
    public void Apply_patches_audio_pcm_in_place_and_can_back_up_the_original_first()
    {
        var gameRoot = BuildGameRoot();
        var originalPcm = new byte[] { 1, 0, 2, 0, 3, 0 }; // 6 bytes allocated
        SyntheticAudio.AddSfxPackage(gameRoot, "GENRL", (22050, originalPcm));

        var modSource = TestScratch.NewDir();
        var bankDir = Path.Combine(modSource, "GENRL", "Bank_001");
        Directory.CreateDirectory(bankDir);
        var replacementPcm = new byte[] { 9, 0, 9, 0 }; // shorter than original — should zero-pad the rest
        using (var f = File.Create(Path.Combine(bankDir, "sound_001.wav")))
            WavPcm.WriteMono16Wav(f, replacementPcm, 22050);

        var backupFolder = TestScratch.NewDir();
        var plan = DirectModInstaller.Plan(gameRoot, modSource);
        var result = DirectModInstaller.Apply(plan, backupOutputFolder: backupFolder);

        var summary = Assert.Single(result.Audio);
        Assert.True(summary.BackedUp);

        var backedUpPath = Path.Combine(backupFolder, "audio", "sfx", "GENRL", "Bank_001", "sound_001.wav");
        var (backedUpPcm, _) = WavPcm.ReadMono16Wav(backedUpPath);
        Assert.Equal(originalPcm, backedUpPcm);

        using var packageStream = File.OpenRead(Path.Combine(gameRoot, "audio", "sfx", "GENRL"));
        var bank = SfxBank.Read(packageStream, 0, new FileInfo(Path.Combine(gameRoot, "audio", "sfx", "GENRL")).Length);
        packageStream.Position = bank.GetPcmOffset(0);
        var patched = new byte[bank.GetPcmLength(0)]; // still 6 bytes — patch never resizes the slot
        packageStream.ReadExactly(patched);

        Assert.Equal(new byte[] { 9, 0, 9, 0, 0, 0 }, patched); // new content + zero padding
    }

    [Fact]
    public void Plan_matches_streamed_audio_by_station_track_path()
    {
        var gameRoot = BuildGameRoot();
        SyntheticAudio.AddStreamStation(gameRoot, "AA", SyntheticAudio.BuildOggLikePayload(1, 64));

        var modSource = TestScratch.NewDir();
        var stationDir = Path.Combine(modSource, "AA");
        Directory.CreateDirectory(stationDir);
        File.WriteAllBytes(Path.Combine(stationDir, "Track_001.ogg"), SyntheticAudio.BuildOggLikePayload(2, 32)); // shorter, fits

        var plan = DirectModInstaller.Plan(gameRoot, modSource);

        var match = Assert.Single(plan.StreamMatches);
        Assert.Equal("AA/Track_001.ogg", match.MatchKey);
        Assert.True(match.Fits);
        Assert.Empty(plan.StreamUnmatched);
    }

    [Fact]
    public void Plan_flags_oversized_stream_replacement_as_not_fitting()
    {
        var gameRoot = BuildGameRoot();
        SyntheticAudio.AddStreamStation(gameRoot, "AA", SyntheticAudio.BuildOggLikePayload(1, 32));

        var modSource = TestScratch.NewDir();
        var stationDir = Path.Combine(modSource, "AA");
        Directory.CreateDirectory(stationDir);
        File.WriteAllBytes(Path.Combine(stationDir, "Track_001.ogg"), SyntheticAudio.BuildOggLikePayload(2, 500)); // way bigger

        var plan = DirectModInstaller.Plan(gameRoot, modSource);

        var match = Assert.Single(plan.StreamMatches);
        Assert.False(match.Fits);
        Assert.Empty(plan.StreamMatchesThatFit);
        Assert.Single(plan.StreamMatchesTooLarge);
    }

    [Fact]
    public void Apply_patches_stream_payload_updates_declared_length_and_can_back_up_the_original()
    {
        var gameRoot = BuildGameRoot();
        var originalPayload = SyntheticAudio.BuildOggLikePayload(1, 64);
        SyntheticAudio.AddStreamStation(gameRoot, "AA", originalPayload);

        var modSource = TestScratch.NewDir();
        var stationDir = Path.Combine(modSource, "AA");
        Directory.CreateDirectory(stationDir);
        var replacementPayload = SyntheticAudio.BuildOggLikePayload(2, 20); // shorter than original 64 bytes
        File.WriteAllBytes(Path.Combine(stationDir, "Track_001.ogg"), replacementPayload);

        var backupFolder = TestScratch.NewDir();
        var plan = DirectModInstaller.Plan(gameRoot, modSource);
        var result = DirectModInstaller.Apply(plan, backupOutputFolder: backupFolder);

        var summary = Assert.Single(result.Streams);
        Assert.True(summary.BackedUp);

        var backedUpBytes = File.ReadAllBytes(Path.Combine(backupFolder, "audio", "streams", "AA", "Track_001.ogg"));
        Assert.Equal(originalPayload, backedUpBytes);

        var stationPath = Path.Combine(gameRoot, "audio", "streams", "AA");
        using var stream = File.OpenRead(stationPath);

        // Declared length must now reflect the NEW (shorter) size, not the old allocation.
        var slot = StreamIndex.FindActiveLengthSlot(stream, 0);
        Assert.Equal(0, slot);

        stream.Position = StreamIndex.TrackHeaderSize;
        var encryptedPayload = new byte[64]; // original allocated space, still 64 bytes total
        stream.ReadExactly(encryptedPayload);
        var decrypted = StreamXor.Transform(encryptedPayload, StreamIndex.TrackHeaderSize);

        var expected = new byte[64];
        replacementPayload.CopyTo(expected, 0); // rest stays zero (decrypted)
        Assert.Equal(expected, decrypted);

        // The station file's total size must never change — patch-in-place only.
        Assert.Equal(StreamIndex.TrackHeaderSize + 64, new FileInfo(stationPath).Length);
    }

    [Fact]
    public void Apply_rejects_a_stream_replacement_that_does_not_look_like_a_real_Ogg_file()
    {
        var gameRoot = BuildGameRoot();
        SyntheticAudio.AddStreamStation(gameRoot, "AA", SyntheticAudio.BuildOggLikePayload(1, 64));

        var modSource = TestScratch.NewDir();
        var stationDir = Path.Combine(modSource, "AA");
        Directory.CreateDirectory(stationDir);
        File.WriteAllBytes(Path.Combine(stationDir, "Track_001.ogg"), new byte[] { 1, 2, 3, 4 }); // not an Ogg file

        var plan = DirectModInstaller.Plan(gameRoot, modSource);
        Assert.Throws<InvalidDataException>(() => DirectModInstaller.Apply(plan, backupOutputFolder: null));
    }
}
