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

    /// <summary>
    /// An asset SAFT added is never "backed up", because there is no original of it to back up.
    ///
    /// Reinstalling a mod is what exposes this: the second install finds the first install's assets
    /// already in the archive, correctly calls them replacements, and would file a copy of the MOD
    /// in the backup folder as though it were stock. A restore later reads that folder and believes
    /// it is looking at vanilla files.
    ///
    /// It also started a chain that killed an uninstall outright — the restore plans those names,
    /// the addition removal deletes them first, and the restore then indexes an entry that is gone.
    ///
    /// Distinct from the 1.6 bug of overwriting a good vanilla backup with a modded file: NeedsBackup
    /// has guarded that for a long time, and this is the case where no vanilla file exists at all.
    /// </summary>
    [Fact]
    public void Does_not_back_up_an_asset_that_SAFT_added()
    {
        var gameRoot = BuildGameRoot();
        var backupFolder = TestScratch.NewDir();

        // The record says saftball.dff is SAFT's own addition, sitting where the backups live.
        new AdditionsManifest
        {
            GameRootPath = gameRoot,
            Mods =
            {
                new AddedMod
                {
                    Name = "My Mod",
                    AddedAtUtc = DateTimeOffset.UtcNow,
                    ArchiveEntries =
                    {
                        new AddedArchiveEntry
                        {
                            ArchiveRelativePath = Path.Combine("models", "gta3.img"),
                            EntryName = "saftball.dff",
                            Sha256 = "irrelevant",
                        },
                    },
                },
            },
        }.Save(backupFolder);

        // Put it in the archive, as install #1 would have.
        var archivePath = Path.Combine(gameRoot, "models", "gta3.img");
        using (var archive = ImgArchive.Open(archivePath))
        {
            var files = archive.Entries
                .Select(e => (e.Name, (Func<Stream>)(() => archive.OpenEntry(e))))
                .ToList();
            files.Add(("saftball.dff", () => new MemoryStream(Encoding.ASCII.GetBytes("added by saft"))));
            ImgArchive.Write(archivePath + ".tmp", files);
        }

        File.Delete(archivePath);
        File.Move(archivePath + ".tmp", archivePath);
        ImgArchive.ClearCaches();

        // Install #2 replaces both a real game file and SAFT's own added asset.
        var modSource = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(modSource, "banshee.dff"), "new car");
        File.WriteAllText(Path.Combine(modSource, "saftball.dff"), "newer ball");

        DirectModInstaller.Apply(DirectModInstaller.Plan(gameRoot, modSource), backupFolder);

        // The real original was kept; SAFT's own asset was not filed as though it were stock.
        Assert.True(File.Exists(Path.Combine(backupFolder, "models", "gta3.img", "dff", "banshee.dff")));
        Assert.False(File.Exists(Path.Combine(backupFolder, "models", "gta3.img", "dff", "saftball.dff")));
    }

    /// <summary>
    /// A plan can name an entry that is no longer in the archive by the time it is applied, and that
    /// must not be fatal.
    ///
    /// This is exactly how uninstall works: it plans its restores from the backup folder, then removes
    /// SAFT's added objects, THEN applies the plan. A reinstall will have backed up those added assets
    /// as though they were originals — they were in the archive by then — so the plan asks to restore
    /// something the removal has just taken out.
    ///
    /// It used to index the entry list with the -1 from the failed search, so the entire uninstall died
    /// with "Index was out of range" and nothing at all was restored. Skipping is not just tolerant, it
    /// is the correct answer: putting the entry back would reinstate an asset the user asked to remove.
    /// </summary>
    [Fact]
    public void Skips_a_planned_entry_that_has_since_been_removed_from_the_archive()
    {
        // 'ghost.dff' stands in for an asset SAFT added: in the archive when the plan is made, and
        // backed up as though it were an original, exactly as a reinstall would leave it.
        var gameRoot = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(gameRoot, "models"));
        File.WriteAllText(Path.Combine(gameRoot, "gta_sa.exe"), "stub");

        var archivePath = Path.Combine(gameRoot, "models", "gta3.img");
        ImgArchive.Write(archivePath, new[]
        {
            File_("banshee.dff", "modded car model"),
            File_("ghost.dff", "an asset SAFT added"),
        });

        var backupFolder = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(backupFolder, "banshee.dff"), "vanilla car model");
        File.WriteAllText(Path.Combine(backupFolder, "ghost.dff"), "an asset SAFT added");

        var plan = DirectModInstaller.Plan(gameRoot, backupFolder);
        Assert.Contains(plan.Matches, m => m.EntryName.Equals("ghost.dff", StringComparison.OrdinalIgnoreCase));

        // Rewritten without 'ghost.dff' AFTER the plan was made - standing in for the addition
        // removal that runs between planning and applying.
        using (var archive = ImgArchive.Open(archivePath))
        {
            var kept = archive.Entries
                .Where(e => !e.Name.Equals("ghost.dff", StringComparison.OrdinalIgnoreCase))
                .Select(e => (e.Name, (Func<Stream>)(() => archive.OpenEntry(e))))
                .ToList();
            ImgArchive.Write(archivePath + ".rebuilt", kept);
        }

        File.Delete(archivePath);
        File.Move(archivePath + ".rebuilt", archivePath);
        ImgArchive.ClearCaches();

        var steps = new List<string>();
        var result = DirectModInstaller.Apply(plan, backupOutputFolder: null, onStep: steps.Add);

        // It did not throw, it said which entry it skipped, and the real restore still happened.
        Assert.Contains(steps, s => s.Contains("ghost.dff") && s.Contains("nothing to put back"));

        using var patched = ImgArchive.Open(archivePath);
        var banshee = patched.Entries.Single(e => e.Name == "banshee.dff");
        using var reader = new StreamReader(patched.OpenEntry(banshee));
        Assert.StartsWith("vanilla car model", reader.ReadToEnd());
        Assert.DoesNotContain(patched.Entries, e => e.Name.Equals("ghost.dff", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Uninstalling a heavy pack crashed SAFT. Restoring a small vanilla file into the slot its large
    /// modded replacement had grown to left a multi-megabyte gap to zero-fill, and that fill was one
    /// allocation the size of the gap — 27.6 MB in the measured case, on a 32-bit heap.
    ///
    /// Several megabytes here rather than a token amount: the whole point is that the gap is large.
    /// The assertion is that the gap really is zeroed and the entry really does shrink, so a chunked
    /// fill cannot quietly write the wrong number of bytes.
    /// </summary>
    [Fact]
    public void Restoring_a_small_file_into_a_large_slot_zero_fills_the_whole_gap()
    {
        var gameRoot = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(gameRoot, "models"));
        File.WriteAllText(Path.Combine(gameRoot, "gta_sa.exe"), "stub");

        const int bigEntryBytes = 5 * 1024 * 1024;   // the slot a modded file grew to
        var archivePath = Path.Combine(gameRoot, "models", "gta3.img");
        ImgArchive.Write(archivePath, new[]
        {
            File_("modded.txd", new string('M', bigEntryBytes)),
            File_("after.col", "must not move or change"),
        });

        // The vanilla original coming back out of a backup folder: tiny next to the slot.
        var backupFolder = TestScratch.NewDir();
        const string vanilla = "the small original texture";
        File.WriteAllText(Path.Combine(backupFolder, "modded.txd"), vanilla);

        var plan = DirectModInstaller.Plan(gameRoot, backupFolder);
        Assert.False(plan.AnyArchiveNeedsRebuild); // it fits, so this is the patch-in-place path
        DirectModInstaller.Apply(plan, backupOutputFolder: null);

        using var archive = ImgArchive.Open(archivePath);
        var entry = archive.Entries.Single(e => e.Name == "modded.txd");

        // Shrunk to what the restored file actually needs, not left at the modded length.
        Assert.Equal((vanilla.Length + ImgEntry.SectorSize - 1) / ImgEntry.SectorSize, entry.SizeSectors);

        // Every byte of the old content is gone: the restored text, then zeros to the sector boundary.
        using var restored = archive.OpenEntry(entry);
        var bytes = new byte[entry.ByteSize];
        restored.ReadExactly(bytes);
        Assert.Equal(vanilla, Encoding.ASCII.GetString(bytes, 0, vanilla.Length));
        Assert.All(bytes.Skip(vanilla.Length), b => Assert.Equal(0, b));

        // And the raw file still holds zeros across the whole abandoned gap, not just the new entry.
        using var raw = File.OpenRead(archivePath);
        raw.Position = entry.ByteOffset + vanilla.Length;
        var gap = new byte[bigEntryBytes - vanilla.Length];
        raw.ReadExactly(gap);
        Assert.DoesNotContain((byte)'M', gap);

        var after = archive.Entries.Single(e => e.Name == "after.col");
        using var afterStream = archive.OpenEntry(after);
        using var reader = new StreamReader(afterStream, Encoding.ASCII);
        Assert.StartsWith("must not move or change", reader.ReadToEnd());
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
    public void Patching_in_place_shrinks_the_entry_size_so_the_game_stops_streaming_the_old_length()
    {
        // The size field in the directory is what the game streams by. Leaving it at the old value
        // makes a smaller replacement cost exactly what the file it replaced did — a texture pack
        // installed byte-for-byte correctly changed nothing in game because its entry still claimed
        // 28.8 MB for a 10.4 MB dictionary. Needs a multi-sector entry to catch: a replacement that
        // shrinks within one sector rounds back to the same sector count and hides the bug.
        var gameRoot = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(gameRoot, "models"));
        File.WriteAllText(Path.Combine(gameRoot, "gta_sa.exe"), "stub");

        var archivePath = Path.Combine(gameRoot, "models", "gta3.img");
        ImgArchive.Write(archivePath, new[]
        {
            File_("big.txd", new string('O', ImgEntry.SectorSize * 3)),   // 3 sectors allocated
            File_("untouched.col", "should never change"),
        });

        var modSource = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(modSource, "big.txd"), new string('N', 100));   // fits in 1

        var plan = DirectModInstaller.Plan(gameRoot, modSource);
        Assert.False(plan.AnyArchiveNeedsRebuild);
        DirectModInstaller.Apply(plan, backupOutputFolder: null);

        using var archive = ImgArchive.Open(archivePath);
        var replaced = archive.Entries.Single(e => e.Name == "big.txd");

        Assert.Equal(1, replaced.SizeSectors);
        Assert.Equal(ImgEntry.SectorSize, replaced.ByteSize);

        // The following entry keeps its own offset — shrinking a size field must not shuffle
        // anything, since nothing else in the archive moved.
        var untouched = archive.Entries.Single(e => e.Name == "untouched.col");
        using var reader = new StreamReader(archive.OpenEntry(untouched), Encoding.ASCII);
        Assert.StartsWith("should never change", reader.ReadToEnd());
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
    public void A_second_install_into_the_same_backup_folder_keeps_the_vanilla_copy()
    {
        // The whole point of a backup folder is getting back to stock. Installing a second mod that
        // touches the same file used to back up the FIRST MOD'S file over the vanilla one, because
        // by then that is what's in the game — silently destroying the only way back while still
        // reporting a successful backup.
        var gameRoot = BuildGameRoot();
        var backupFolder = TestScratch.NewDir();

        var firstMod = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(firstMod, "banshee.dff"), "FIRST MOD");
        DirectModInstaller.Apply(DirectModInstaller.Plan(gameRoot, firstMod), backupOutputFolder: backupFolder);

        var secondMod = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(secondMod, "banshee.dff"), "SECOND MOD");
        DirectModInstaller.Apply(DirectModInstaller.Plan(gameRoot, secondMod), backupOutputFolder: backupFolder);

        var backedUpFile = Path.Combine(backupFolder, "models", "gta3.img", "dff", "banshee.dff");
        Assert.StartsWith("original car model", File.ReadAllText(backedUpFile));
    }

    [Fact]
    public void Installing_the_same_mod_twice_does_not_overwrite_its_own_backup()
    {
        var gameRoot = BuildGameRoot();
        var backupFolder = TestScratch.NewDir();
        var modSource = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(modSource, "banshee.dff"), "MODDED");

        DirectModInstaller.Apply(DirectModInstaller.Plan(gameRoot, modSource), backupOutputFolder: backupFolder);
        DirectModInstaller.Apply(DirectModInstaller.Plan(gameRoot, modSource), backupOutputFolder: backupFolder);

        var backedUpFile = Path.Combine(backupFolder, "models", "gta3.img", "dff", "banshee.dff");
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
    public void Plan_and_Apply_skip_a_corrupted_audio_mod_file_without_derailing_other_matches_in_the_same_batch()
    {
        var gameRoot = BuildGameRoot();
        var goodOriginalPcm = new byte[] { 1, 0, 2, 0, 3, 0 };
        var corruptOriginalPcm = new byte[] { 4, 0, 5, 0 };
        SyntheticAudio.AddSfxPackage(gameRoot, "GENRL", (22050, goodOriginalPcm), (22050, corruptOriginalPcm));

        var modSource = TestScratch.NewDir();
        var bankDir = Path.Combine(modSource, "GENRL", "Bank_001");
        Directory.CreateDirectory(bankDir);

        var goodReplacementPcm = new byte[] { 9, 0, 9, 0 };
        using (var f = File.Create(Path.Combine(bankDir, "sound_001.wav")))
            WavPcm.WriteMono16Wav(f, goodReplacementPcm, 22050);

        // A hand-corrupted .wav for sound_002: valid RIFF/WAVE/fmt header, but the 'data' chunk
        // declares an impossible size — exactly the kind of malformed third-party export that used
        // to throw a cryptic OverflowException and (before this fix) abort the whole install.
        using (var f = new FileStream(Path.Combine(bankDir, "sound_002.wav"), FileMode.Create, FileAccess.Write))
        using (var writer = new BinaryWriter(f, System.Text.Encoding.ASCII))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(22050);
            writer.Write(22050 * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(-1); // corrupted: negative declared size
        }

        // Plan() itself reads every matched .wav (to know its size for the fits-in-place check),
        // so a corrupted file is caught right there — reported as unmatched-with-a-reason rather
        // than throwing and aborting the scan of the rest of the mod folder.
        var plan = DirectModInstaller.Plan(gameRoot, modSource);
        Assert.Single(plan.AudioMatches); // only sound_001 made it into a real match
        var unmatchedEntry = Assert.Single(plan.AudioUnmatched);
        Assert.Contains("GENRL/Bank_001/sound_002.wav", unmatchedEntry);
        Assert.Contains("unreadable", unmatchedEntry);

        var result = DirectModInstaller.Apply(plan, backupOutputFolder: null);

        // The good replacement still went through, and nothing about the corrupted file's presence
        // in the mod folder threw or was silently swallowed without a trace.
        var patched = Assert.Single(result.Audio);
        Assert.Equal("GENRL/Bank_001/sound_001.wav", patched.MatchKey);
        Assert.Empty(result.AudioFailed); // this one never got as far as Apply() at all

        using var packageStream = File.OpenRead(Path.Combine(gameRoot, "audio", "sfx", "GENRL"));
        var bank = SfxBank.Read(packageStream, 0, new FileInfo(Path.Combine(gameRoot, "audio", "sfx", "GENRL")).Length);

        packageStream.Position = bank.GetPcmOffset(0);
        var sound1 = new byte[bank.GetPcmLength(0)];
        packageStream.ReadExactly(sound1);
        Assert.Equal(new byte[] { 9, 0, 9, 0, 0, 0 }, sound1); // patched

        packageStream.Position = bank.GetPcmOffset(1);
        var sound2 = new byte[bank.GetPcmLength(1)];
        packageStream.ReadExactly(sound2);
        Assert.Equal(corruptOriginalPcm, sound2); // untouched — never matched, so never touched
    }

    [Fact]
    public void Apply_reports_an_audio_failure_that_happens_after_Plan_already_succeeded()
    {
        // Simulates a match that read fine during Plan() but fails later during Apply() — e.g. the
        // mod file got deleted/became unreadable in between, or (per real-world evidence from
        // testing under Wine) a re-read of the same file some time later can behave differently
        // than the first read did. Apply()'s own try/catch (independent of Plan()'s) is what
        // protects against exactly this category, which Plan()'s check alone cannot.
        var gameRoot = BuildGameRoot();
        var originalPcm = new byte[] { 1, 0, 2, 0, 3, 0 };
        SyntheticAudio.AddSfxPackage(gameRoot, "GENRL", (22050, originalPcm));

        var modSource = TestScratch.NewDir();
        var bankDir = Path.Combine(modSource, "GENRL", "Bank_001");
        Directory.CreateDirectory(bankDir);
        var modFilePath = Path.Combine(bankDir, "sound_001.wav");
        using (var f = File.Create(modFilePath))
            WavPcm.WriteMono16Wav(f, new byte[] { 9, 0, 9, 0 }, 22050);

        var plan = DirectModInstaller.Plan(gameRoot, modSource);
        Assert.Single(plan.AudioMatches); // read fine just now

        File.Delete(modFilePath); // ...and now it's gone before Apply() gets to it

        var result = DirectModInstaller.Apply(plan, backupOutputFolder: null);

        Assert.Empty(result.Audio);
        var failure = Assert.Single(result.AudioFailed);
        Assert.Equal("GENRL/Bank_001/sound_001.wav", failure.MatchKey);
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
    public void Apply_reports_a_stream_replacement_that_does_not_look_like_a_real_Ogg_file_instead_of_throwing()
    {
        var gameRoot = BuildGameRoot();
        SyntheticAudio.AddStreamStation(gameRoot, "AA", SyntheticAudio.BuildOggLikePayload(1, 64));

        var modSource = TestScratch.NewDir();
        var stationDir = Path.Combine(modSource, "AA");
        Directory.CreateDirectory(stationDir);
        File.WriteAllBytes(Path.Combine(stationDir, "Track_001.ogg"), new byte[] { 1, 2, 3, 4 }); // not an Ogg file

        var plan = DirectModInstaller.Plan(gameRoot, modSource);
        // Must not throw and abort — a single bad file is reported, not allowed to derail an
        // install that might otherwise include several other, perfectly good replacements.
        var result = DirectModInstaller.Apply(plan, backupOutputFolder: null);

        Assert.Empty(result.Streams);
        var failure = Assert.Single(result.StreamFailed);
        Assert.Equal("AA/Track_001.ogg", failure.MatchKey);
    }

    [Fact]
    public void Plan_and_Apply_replace_game_files_that_live_outside_the_archives()
    {
        var gameRoot = BuildGameRoot();
        var mapsDir = Path.Combine(gameRoot, "data", "maps", "LA");
        Directory.CreateDirectory(mapsDir);
        File.WriteAllText(Path.Combine(mapsDir, "LAn.ipl"), "original placement data");

        var modSource = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(modSource, "LAn.ipl"), "modded placement data");

        var plan = DirectModInstaller.Plan(gameRoot, modSource);

        // Map placement data isn't in any archive, but it's still a real game file the mod means to
        // replace — it must match, not land in "unmatched" for the user to place by hand.
        Assert.Empty(plan.Unmatched);
        var match = Assert.Single(plan.UnarchivedMatches);
        Assert.Equal("LAn.ipl", match.FileName);

        var result = DirectModInstaller.Apply(plan, backupOutputFolder: null);

        Assert.Single(result.Unarchived);
        Assert.Equal("modded placement data", File.ReadAllText(Path.Combine(mapsDir, "LAn.ipl")));
    }

    [Fact]
    public void Apply_backs_up_an_unarchived_game_file_at_its_original_relative_path()
    {
        var gameRoot = BuildGameRoot();
        var mapsDir = Path.Combine(gameRoot, "data", "maps", "LA");
        Directory.CreateDirectory(mapsDir);
        File.WriteAllText(Path.Combine(mapsDir, "LAn.ipl"), "original placement data");

        var modSource = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(modSource, "LAn.ipl"), "modded placement data");
        var backupFolder = TestScratch.NewDir();

        var plan = DirectModInstaller.Plan(gameRoot, modSource);
        var result = DirectModInstaller.Apply(plan, backupFolder);

        Assert.True(Assert.Single(result.Unarchived).BackedUp);
        // Mirroring the game's own folder layout is what makes the backup usable as an uninstall
        // source later — the Uninstall tab feeds it straight back through this same matcher.
        var backedUp = Path.Combine(backupFolder, "data", "maps", "LA", "LAn.ipl");
        Assert.True(File.Exists(backedUp));
        Assert.Equal("original placement data", File.ReadAllText(backedUp));
    }

    [Fact]
    public void Plan_replaces_both_copies_when_an_archived_and_unarchived_file_hold_identical_content()
    {
        // San Andreas really does this: nodes0.dat … nodes63.dat are shipped byte-for-byte
        // identically both loose in data/Paths/ and inside gta3.img. Updating only one copy risks
        // the game loading the stale other one, so both have to move together.
        var gameRoot = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(gameRoot, "models"));
        File.WriteAllText(Path.Combine(gameRoot, "gta_sa.exe"), "stub");
        ImgArchive.Write(Path.Combine(gameRoot, "models", "gta3.img"), new[] { File_("nodes0.dat", "shared node data") });

        var pathsDir = Path.Combine(gameRoot, "data", "Paths");
        Directory.CreateDirectory(pathsDir);
        File.WriteAllText(Path.Combine(pathsDir, "nodes0.dat"), "shared node data");

        var modSource = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(modSource, "nodes0.dat"), "modded node data");

        var plan = DirectModInstaller.Plan(gameRoot, modSource);

        Assert.Empty(plan.Ambiguous);
        Assert.Single(plan.Matches);            // the archived copy
        Assert.Single(plan.UnarchivedMatches);  // and the loose one alongside it

        DirectModInstaller.Apply(plan, backupOutputFolder: null);
        Assert.Equal("modded node data", File.ReadAllText(Path.Combine(pathsDir, "nodes0.dat")));
    }

    [Fact]
    public void Plan_refuses_to_guess_when_an_archived_and_unarchived_file_of_the_same_name_differ()
    {
        // The arrow.dff / hoop.dff case: same name, genuinely different content, so SAFT can't know
        // which asset the mod meant. It replaces the archived copy (the one reachable by object ID)
        // and reports the loose one instead of silently overwriting it.
        var gameRoot = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(gameRoot, "models"));
        File.WriteAllText(Path.Combine(gameRoot, "gta_sa.exe"), "stub");
        ImgArchive.Write(Path.Combine(gameRoot, "models", "gta3.img"), new[] { File_("arrow.dff", "the streamed arrow model") });

        var genericDir = Path.Combine(gameRoot, "models", "generic");
        Directory.CreateDirectory(genericDir);
        File.WriteAllText(Path.Combine(genericDir, "arrow.dff"), "a completely different arrow model");

        var modSource = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(modSource, "arrow.dff"), "modded arrow");

        var plan = DirectModInstaller.Plan(gameRoot, modSource);

        Assert.Single(plan.Matches);           // archived copy still replaced
        Assert.Empty(plan.UnarchivedMatches);  // loose copy deliberately left alone
        var ambiguous = Assert.Single(plan.Ambiguous);
        Assert.Equal("arrow.dff", ambiguous.FileName);

        DirectModInstaller.Apply(plan, backupOutputFolder: null);
        Assert.Equal("a completely different arrow model", File.ReadAllText(Path.Combine(genericDir, "arrow.dff")));
    }

    [Fact]
    public void ModInstaller_routes_unarchived_game_files_into_an_extracted_install_and_they_survive_rebuild()
    {
        var gameRoot = BuildGameRoot();
        var mapsDir = Path.Combine(gameRoot, "data", "maps", "LA");
        Directory.CreateDirectory(mapsDir);
        File.WriteAllText(Path.Combine(mapsDir, "LAn.ipl"), "original placement data");

        var extractDest = TestScratch.NewDir();
        Extractor.Extract(gameRoot, extractDest);

        var modSource = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(modSource, "LAn.ipl"), "modded placement data");

        var result = ModInstaller.Install(extractDest, modSource);

        // A mod pack must behave the same whichever route the user takes — installing into an
        // extracted copy has to route map data just like the direct installer does.
        Assert.Empty(result.Unmatched);
        Assert.Single(result.UnarchivedRouted);
        Assert.Equal("modded placement data", File.ReadAllText(Path.Combine(extractDest, "data", "maps", "LA", "LAn.ipl")));

        // …and the rebuild has to carry it through to the playable output.
        var rebuildOutput = TestScratch.NewDir();
        Rebuilder.Rebuild(extractDest, rebuildOutput);
        Assert.Equal("modded placement data", File.ReadAllText(Path.Combine(rebuildOutput, "data", "maps", "LA", "LAn.ipl")));
    }

    [Fact]
    public void ModInstaller_compares_the_games_own_copies_not_the_mods_when_deciding_ambiguity()
    {
        // Regression guard: the identical/different decision must be made against the ORIGINAL
        // extracted copies. Comparing after the archive copy has already been overwritten would be
        // comparing the mod against the game, which always differs — silently turning every
        // dual-location file into a false "ambiguous" and quietly skipping it.
        var gameRoot = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(gameRoot, "models"));
        File.WriteAllText(Path.Combine(gameRoot, "gta_sa.exe"), "stub");
        ImgArchive.Write(Path.Combine(gameRoot, "models", "gta3.img"), new[] { File_("nodes0.dat", "shared node data") });

        var pathsDir = Path.Combine(gameRoot, "data", "Paths");
        Directory.CreateDirectory(pathsDir);
        File.WriteAllText(Path.Combine(pathsDir, "nodes0.dat"), "shared node data");

        var extractDest = TestScratch.NewDir();
        Extractor.Extract(gameRoot, extractDest);

        var modSource = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(modSource, "nodes0.dat"), "modded node data");

        var result = ModInstaller.Install(extractDest, modSource);

        Assert.Empty(result.Ambiguous);
        Assert.Single(result.UnarchivedRouted);
        Assert.Equal("modded node data", File.ReadAllText(Path.Combine(extractDest, "data", "Paths", "nodes0.dat")));
        Assert.Equal("modded node data", File.ReadAllText(Path.Combine(extractDest, "models", "gta3.img", "dat", "nodes0.dat")));
    }

    [Fact]
    public void Plan_never_matches_executables_libraries_or_whole_archives()
    {
        var gameRoot = BuildGameRoot();
        File.WriteAllText(Path.Combine(gameRoot, "vorbisFile.dll"), "original library");

        var scriptDir = Path.Combine(gameRoot, "data", "script");
        Directory.CreateDirectory(scriptDir);
        File.WriteAllText(Path.Combine(scriptDir, "main.scm"), "original game script");

        var modSource = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(modSource, "gta_sa.exe"), "a replacement executable");
        File.WriteAllText(Path.Combine(modSource, "vorbisFile.dll"), "a replacement library");
        File.WriteAllText(Path.Combine(modSource, "gta3.img"), "a whole replacement archive");
        File.WriteAllText(Path.Combine(modSource, "main.scm"), "a replacement game script");

        var plan = DirectModInstaller.Plan(gameRoot, modSource);

        // Binaries and whole archives are never eligible: SAFT replaces game assets, not the game
        // itself, and a wholesale .img swap would bypass the entire patch/rebuild path.
        //
        // main.scm is excluded on a stricter principle — SAFT only installs what it can genuinely
        // uninstall. Save files embed the script's global layout and live outside the game folder,
        // so a save written against a modded script stays broken even after the file is restored.
        Assert.Empty(plan.UnarchivedMatches);
        Assert.Equal(3, plan.Unmatched.Count); // exe, dll, img
        var refused = Assert.Single(plan.RefusedScripts); // reported separately, so the app can explain why
        Assert.Equal("main.scm", refused.FileName);
        Assert.Equal(RefusedScriptKind.MainScript, refused.Kind);
        Assert.Equal("original library", File.ReadAllText(Path.Combine(gameRoot, "vorbisFile.dll")));
        Assert.Equal("stub", File.ReadAllText(Path.Combine(gameRoot, "gta_sa.exe")));
        Assert.Equal("original game script", File.ReadAllText(Path.Combine(scriptDir, "main.scm")));
    }

    [Fact]
    public void Plan_refuses_streamed_scripts_inside_script_img_not_just_the_loose_main_scm()
    {
        // Verified against a real save file: San Andreas stores a table of streamed-script records
        // referencing script.img's entries by index (DANCER, PCHAIR, OTBWTCH, PEDROUL …). Replacing
        // one leaves an existing save holding a live reference into bytecode that no longer matches
        // it, and saves live outside the game folder where SAFT's backups can't reach — so these are
        // refused exactly like main.scm, even though they ARE archive entries SAFT could patch.
        var gameRoot = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(gameRoot, "models"));
        Directory.CreateDirectory(Path.Combine(gameRoot, "data", "script"));
        File.WriteAllText(Path.Combine(gameRoot, "gta_sa.exe"), "stub");
        ImgArchive.Write(Path.Combine(gameRoot, "models", "gta3.img"), new[] { File_("banshee.dff", "original car") });
        ImgArchive.Write(Path.Combine(gameRoot, "data", "script", "script.img"), new[]
        {
            File_("dancer.scm", "original streamed script"),
        });

        var modSource = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(modSource, "dancer.scm"), "a modified streamed script");
        File.WriteAllText(Path.Combine(modSource, "banshee.dff"), "modded car");

        File.WriteAllText(Path.Combine(gameRoot, "data", "script", "main.scm"), "the original main script");
        File.WriteAllText(Path.Combine(modSource, "main.scm"), "a modified main script");

        var plan = DirectModInstaller.Plan(gameRoot, modSource);

        // Both are refused, but they're told apart: one is swapped by hand, the other only via
        // extract-and-rebuild, so the app can give the right instructions for each.
        Assert.Equal(2, plan.RefusedScripts.Count);
        Assert.Equal(RefusedScriptKind.StreamedScript, plan.RefusedScripts.Single(s => s.FileName == "dancer.scm").Kind);
        Assert.Equal(RefusedScriptKind.MainScript, plan.RefusedScripts.Single(s => s.FileName == "main.scm").Kind);
        Assert.DoesNotContain(plan.Matches, m => m.FileName.EndsWith(".scm", StringComparison.OrdinalIgnoreCase));
        Assert.Single(plan.Matches); // the car still installs normally

        DirectModInstaller.Apply(plan, backupOutputFolder: null);

        using var scriptImg = ImgArchive.Open(Path.Combine(gameRoot, "data", "script", "script.img"));
        using var entry = scriptImg.OpenEntry(scriptImg.Entries.Single());
        using var reader = new StreamReader(entry);
        Assert.StartsWith("original streamed script", reader.ReadToEnd());
    }
    /// <summary>
    /// The uninstall reads originals out of the backup folder. If the "keep the files I am taking
    /// out" box points at that same folder, the backup pass overwrites a vanilla original with the
    /// modded file, and the restore then puts the mod back into the game believing it is stock. The
    /// user's only good copy is gone and nothing in the log looks wrong.
    ///
    /// Found on a real uninstall, where the backup folder came out holding SAFT's own added assets
    /// filed as vanilla originals. Refused before anything is written.
    /// </summary>
    [Fact]
    public void Refuses_to_back_up_into_the_very_folder_it_is_restoring_from()
    {
        var gameRoot = BuildGameRoot();
        var backupFolder = TestScratch.NewDir();

        DirectModInstaller.Apply(DirectModInstaller.Plan(gameRoot, BuildMod("modded car model")), backupFolder);

        var plan = DirectModInstaller.Plan(gameRoot, backupFolder);
        var refused = Assert.Throws<InvalidOperationException>(
            () => DirectModInstaller.Apply(plan, backupOutputFolder: backupFolder));

        Assert.Contains("same as the folder being restored from", refused.Message);

        // Refused up front, so the vanilla original is still the vanilla original.
        Assert.Equal(
            "original car model",
            File.ReadAllText(Path.Combine(backupFolder, "models", "gta3.img", "dff", "banshee.dff")).TrimEnd('\0'));
    }

    /// <summary>A folder inside the one being restored from is the same trap one level down.</summary>
    [Fact]
    public void Refuses_to_back_up_into_a_folder_inside_the_one_it_is_restoring_from()
    {
        var gameRoot = BuildGameRoot();
        var backupFolder = TestScratch.NewDir();

        DirectModInstaller.Apply(DirectModInstaller.Plan(gameRoot, BuildMod("modded car model")), backupFolder);

        var inside = Path.Combine(backupFolder, "taken-out");
        Directory.CreateDirectory(inside);

        Assert.Throws<InvalidOperationException>(
            () => DirectModInstaller.Apply(DirectModInstaller.Plan(gameRoot, backupFolder), backupOutputFolder: inside));
    }

    /// <summary>
    /// A neighbour whose name merely starts the same way is not inside anything, and refusing it
    /// would block a perfectly ordinary choice of folder.
    /// </summary>
    [Fact]
    public void Allows_a_backup_folder_whose_name_only_starts_like_the_source()
    {
        var gameRoot = BuildGameRoot();
        var backupFolder = Path.Combine(TestScratch.NewDir(), "Backups");
        Directory.CreateDirectory(backupFolder);

        DirectModInstaller.Apply(DirectModInstaller.Plan(gameRoot, BuildMod("modded car model")), backupFolder);

        var neighbour = backupFolder + "2";
        Directory.CreateDirectory(neighbour);

        DirectModInstaller.Apply(DirectModInstaller.Plan(gameRoot, backupFolder), backupOutputFolder: neighbour);

        // The restore ran, so the game holds the vanilla model again.
        using var img = ImgArchive.Open(Path.Combine(gameRoot, "models", "gta3.img"));
        using var entry = img.OpenEntry(img.Entries.First(e => e.Name == "banshee.dff"));
        using var reader = new StreamReader(entry);
        Assert.StartsWith("original car model", reader.ReadToEnd());
    }

    private static string BuildMod(string content)
    {
        var modSource = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(modSource, "banshee.dff"), content);
        return modSource;
    }
}
