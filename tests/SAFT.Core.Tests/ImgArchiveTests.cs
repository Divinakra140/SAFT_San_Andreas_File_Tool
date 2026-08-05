using System.Text;
using SAFT.Core;

namespace SAFT.Core.Tests;

public class ImgArchiveTests
{
    private static (string Name, Func<Stream> OpenContent) File_(string name, string content) =>
        (name, () => new MemoryStream(Encoding.ASCII.GetBytes(content)));

    [Fact]
    public void Write_then_Open_round_trips_names_and_content_in_order()
    {
        var dir = TestScratch.NewDir();
        var archivePath = Path.Combine(dir, "test.img");

        var contents = new (string Name, string Content)[]
        {
            ("aaa.dff", "hello world"),
            ("bbb.txd", new string('x', 5000)), // spans multiple sectors
            ("ccc.col", ""), // zero-length file
        };

        ImgArchive.Write(archivePath, contents.Select(c => File_(c.Name, c.Content)).ToList());

        using var archive = ImgArchive.Open(archivePath);

        Assert.Equal(3, archive.Entries.Count);
        Assert.Equal(new[] { "aaa.dff", "bbb.txd", "ccc.col" }, archive.Entries.Select(e => e.Name));

        for (var i = 0; i < contents.Length; i++)
        {
            var entry = archive.Entries[i];
            using var stream = archive.OpenEntry(entry);
            using var reader = new StreamReader(stream, Encoding.ASCII);
            var extracted = reader.ReadToEnd();

            var expectedPadded = contents[i].Content.PadRight((int)entry.ByteSize, '\0');
            Assert.Equal(expectedPadded, extracted);
        }
    }

    [Fact]
    public void Entries_are_sector_aligned_and_offsets_are_sequential()
    {
        var dir = TestScratch.NewDir();
        var archivePath = Path.Combine(dir, "test.img");

        ImgArchive.Write(archivePath, new[]
        {
            File_("a.dff", new string('a', 1)),
            File_("b.dff", new string('b', ImgEntry.SectorSize + 1)),
        });

        using var archive = ImgArchive.Open(archivePath);
        var a = archive.Entries[0];
        var b = archive.Entries[1];

        Assert.Equal(1, (int)a.SizeSectors);
        Assert.Equal(2, (int)b.SizeSectors);
        Assert.Equal(a.OffsetSectors + a.SizeSectors, b.OffsetSectors);

        var fileLength = new FileInfo(archivePath).Length;
        Assert.Equal(0, fileLength % ImgEntry.SectorSize);
    }

    [Fact]
    public void IsImgArchive_rejects_non_VER2_files()
    {
        var dir = TestScratch.NewDir();
        var notAnArchive = Path.Combine(dir, "fake.img");
        File.WriteAllText(notAnArchive, "this is not an IMG archive");

        Assert.False(ImgArchive.IsImgArchive(notAnArchive));
    }

    [Fact]
    public void Write_rejects_filenames_longer_than_23_characters()
    {
        var dir = TestScratch.NewDir();
        var archivePath = Path.Combine(dir, "test.img");
        var tooLong = new string('n', 24) + ".dff";

        Assert.Throws<InvalidDataException>(() =>
            ImgArchive.Write(archivePath, new[] { File_(tooLong, "x") }));
    }

    [Fact]
    public void GameScanner_finds_only_genuine_VER2_archives()
    {
        var root = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(root, "models"));

        var realArchive = Path.Combine(root, "models", "gta3.img");
        ImgArchive.Write(realArchive, new[] { File_("x.dff", "content") });

        var fakeArchive = Path.Combine(root, "models", "not_really.img");
        File.WriteAllText(fakeArchive, "garbage, wrong magic");

        var found = GameScanner.FindArchives(root);

        Assert.Single(found);
        Assert.Equal(Path.Combine("models", "gta3.img"), found[0].RelativePath);
    }

    [Fact]
    public void Extract_then_Rebuild_round_trips_a_multi_archive_install()
    {
        var gameRoot = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(gameRoot, "models"));
        File.WriteAllText(Path.Combine(gameRoot, "gta_sa.exe"), "stub");

        ImgArchive.Write(Path.Combine(gameRoot, "models", "gta3.img"), new[]
        {
            File_("bbb_lr_slv1.dff", "model one"),
            File_("bbb_lr_slv1.txd", "texture one"),
            File_("bntl_b_ov.col", "collision one"),
        });

        var extractDest = TestScratch.NewDir();
        var manifest = Extractor.Extract(gameRoot, extractDest);

        Assert.Single(manifest.Archives);
        Assert.Equal(
            new[] { "bbb_lr_slv1.dff", "bbb_lr_slv1.txd", "bntl_b_ov.col" },
            manifest.Archives[0].OriginalEntryOrder);

        var dffPath = Path.Combine(extractDest, "models", "gta3.img", "dff", "bbb_lr_slv1.dff");
        var txdPath = Path.Combine(extractDest, "models", "gta3.img", "txd", "bbb_lr_slv1.txd");
        var colPath = Path.Combine(extractDest, "models", "gta3.img", "col", "bntl_b_ov.col");
        Assert.True(File.Exists(dffPath));
        Assert.True(File.Exists(txdPath));
        Assert.True(File.Exists(colPath));

        // Simulate modding: replace one file, delete one, add a new one.
        File.WriteAllText(dffPath, "MODIFIED model one");
        File.Delete(colPath);
        var newTxdPath = Path.Combine(extractDest, "models", "gta3.img", "txd", "zzz_new.txd");
        File.WriteAllText(newTxdPath, "brand new texture");

        var rebuildOutput = TestScratch.NewDir();
        var summaries = Rebuilder.Rebuild(extractDest, rebuildOutput);

        var summary = Assert.Single(summaries);
        Assert.Equal(Path.Combine("models", "gta3.img"), summary.RelativePath);
        Assert.Equal(2, summary.Kept);   // dff (modified content, same name) + txd
        Assert.Equal(1, summary.Removed); // col
        Assert.Equal(1, summary.Added);   // zzz_new.txd

        using var rebuilt = ImgArchive.Open(Path.Combine(rebuildOutput, "models", "gta3.img"));
        Assert.Equal(
            new[] { "bbb_lr_slv1.dff", "bbb_lr_slv1.txd", "zzz_new.txd" },
            rebuilt.Entries.Select(e => e.Name));

        using var modifiedContent = rebuilt.OpenEntry(rebuilt.Entries[0]);
        using var reader = new StreamReader(modifiedContent, Encoding.ASCII);
        Assert.StartsWith("MODIFIED model one", reader.ReadToEnd());
    }

    [Fact]
    public void Rebuild_ignores_txt_files_and_other_filesystem_clutter()
    {
        var gameRoot = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(gameRoot, "models"));
        File.WriteAllText(Path.Combine(gameRoot, "gta_sa.exe"), "stub");
        ImgArchive.Write(Path.Combine(gameRoot, "models", "gta3.img"), new[] { File_("a.dff", "content") });

        var extractDest = TestScratch.NewDir();
        Extractor.Extract(gameRoot, extractDest);

        var dffDir = Path.Combine(extractDest, "models", "gta3.img", "dff");
        File.WriteAllText(Path.Combine(dffDir, "readme.txt"), "install instructions, not a game file");
        File.WriteAllText(Path.Combine(dffDir, "desktop.ini"), "[.ShellClassInfo]");
        File.WriteAllText(Path.Combine(dffDir, ".DS_Store"), "junk");

        var rebuildOutput = TestScratch.NewDir();
        var summary = Assert.Single(Rebuilder.Rebuild(extractDest, rebuildOutput));

        Assert.Equal(1, summary.Kept);
        Assert.Equal(0, summary.Added); // the .txt/.ini/.DS_Store must not show up as "added" files

        using var rebuilt = ImgArchive.Open(Path.Combine(rebuildOutput, "models", "gta3.img"));
        Assert.Equal(new[] { "a.dff" }, rebuilt.Entries.Select(e => e.Name));
    }

    [Fact]
    public void ModInstaller_routes_matching_files_to_the_right_archive_and_bucket()
    {
        var gameRoot = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(gameRoot, "models"));
        File.WriteAllText(Path.Combine(gameRoot, "gta_sa.exe"), "stub");

        ImgArchive.Write(Path.Combine(gameRoot, "models", "gta3.img"), new[]
        {
            File_("banshee.dff", "original car model"),
            File_("banshee.txd", "original car texture"),
        });
        ImgArchive.Write(Path.Combine(gameRoot, "models", "cutscene.img"), new[]
        {
            File_("csm16.dff", "original cutscene gun model"),
        });

        var extractDest = TestScratch.NewDir();
        Extractor.Extract(gameRoot, extractDest);

        // A pile of mod files dropped in arbitrary per-item subfolders, exactly like a
        // ModLoader-style pack, plus junk that should never end up in the rebuilt archive.
        var modSource = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(modSource, "banshee"));
        File.WriteAllText(Path.Combine(modSource, "banshee", "banshee.dff"), "MODDED car model");
        File.WriteAllText(Path.Combine(modSource, "banshee", "banshee.txd"), "MODDED car texture");
        File.WriteAllText(Path.Combine(modSource, "banshee", "banshe.txt"), "readme, not a game file");
        Directory.CreateDirectory(Path.Combine(modSource, "weapons"));
        File.WriteAllText(Path.Combine(modSource, "weapons", "csm16.dff"), "MODDED cutscene gun model");
        File.WriteAllText(Path.Combine(modSource, "weapons", "desktop.ini"), "[.ShellClassInfo]");
        File.WriteAllText(Path.Combine(modSource, "brand_new_car.dff"), "a new vehicle, not a replacement");

        var result = ModInstaller.Install(extractDest, modSource);

        Assert.Equal(3, result.Routed.Count); // banshee.dff, banshee.txd, csm16.dff
        Assert.Contains(result.Routed, r => r.FileName == "banshee.dff" && r.ArchiveRelativePaths.Single() == Path.Combine("models", "gta3.img"));
        Assert.Contains(result.Routed, r => r.FileName == "csm16.dff" && r.ArchiveRelativePaths.Single() == Path.Combine("models", "cutscene.img"));

        Assert.Single(result.Unmatched);
        Assert.Equal("brand_new_car.dff", result.Unmatched[0]);

        var placedDff = Path.Combine(extractDest, "models", "gta3.img", "dff", "banshee.dff");
        Assert.Equal("MODDED car model", File.ReadAllText(placedDff));

        var rebuildOutput = TestScratch.NewDir();
        var summaries = Rebuilder.Rebuild(extractDest, rebuildOutput);

        var gta3Summary = summaries.Single(s => s.RelativePath == Path.Combine("models", "gta3.img"));
        Assert.Equal(2, gta3Summary.Kept);
        Assert.Equal(0, gta3Summary.Added); // readme.txt / desktop.ini must not leak in as new entries

        using var rebuiltGta3 = ImgArchive.Open(Path.Combine(rebuildOutput, "models", "gta3.img"));
        using var modifiedDff = rebuiltGta3.OpenEntry(rebuiltGta3.Entries.Single(e => e.Name == "banshee.dff"));
        using var reader = new StreamReader(modifiedDff, Encoding.ASCII);
        Assert.StartsWith("MODDED car model", reader.ReadToEnd());
    }

    [Fact]
    public void RebuildNewPlayableCopy_copies_the_whole_game_not_just_archives()
    {
        var gameRoot = TestScratch.NewDir();
        // Deliberately nested, with a space in a folder name, mirroring a real "Grand Theft
        // Auto San Andreas" install path — loose non-archive files at multiple depths.
        Directory.CreateDirectory(Path.Combine(gameRoot, "models"));
        Directory.CreateDirectory(Path.Combine(gameRoot, "audio", "streams", "AA"));
        File.WriteAllText(Path.Combine(gameRoot, "gta_sa.exe"), "stub exe");
        File.WriteAllText(Path.Combine(gameRoot, "audio", "streams", "AA", "song.wav"), "not an archive, just audio");
        File.WriteAllText(Path.Combine(gameRoot, "models", "desktop.ini"), "[.ShellClassInfo]"); // must not be copied

        ImgArchive.Write(Path.Combine(gameRoot, "models", "gta3.img"), new[]
        {
            File_("bbb_lr_slv1.dff", "original model"),
        });

        var extractDest = TestScratch.NewDir();
        Extractor.Extract(gameRoot, extractDest);

        // Mod the extracted content before rebuilding, to confirm the copy step doesn't
        // clobber the freshly rebuilt archive with the stale original.
        File.WriteAllText(Path.Combine(extractDest, "models", "gta3.img", "dff", "bbb_lr_slv1.dff"), "MODDED model");

        var outputRoot = TestScratch.NewDir();
        var summaries = Rebuilder.RebuildNewPlayableCopy(extractDest, outputRoot);

        Assert.True(File.Exists(Path.Combine(outputRoot, "gta_sa.exe")));
        Assert.Equal("not an archive, just audio", File.ReadAllText(Path.Combine(outputRoot, "audio", "streams", "AA", "song.wav")));
        Assert.False(File.Exists(Path.Combine(outputRoot, "models", "desktop.ini")));

        var summary = Assert.Single(summaries);
        Assert.Equal(Path.Combine("models", "gta3.img"), summary.RelativePath);

        using var rebuilt = ImgArchive.Open(Path.Combine(outputRoot, "models", "gta3.img"));
        using var content = rebuilt.OpenEntry(rebuilt.Entries.Single());
        using var reader = new StreamReader(content, Encoding.ASCII);
        Assert.StartsWith("MODDED model", reader.ReadToEnd());
    }

    [Fact]
    public void RebuildEstimator_totals_match_what_a_real_rebuild_actually_produces()
    {
        var gameRoot = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(gameRoot, "models"));
        File.WriteAllText(Path.Combine(gameRoot, "gta_sa.exe"), "stub exe, twenty chars!"); // 24 bytes of "loose" content

        ImgArchive.Write(Path.Combine(gameRoot, "models", "gta3.img"), new[]
        {
            File_("a.dff", "original"),
            File_("b.txd", "original"),
        });

        var extractDest = TestScratch.NewDir();
        Extractor.Extract(gameRoot, extractDest);

        // Grow one file past its original allocation so the rebuilt archive's size actually changes.
        var aPath = Path.Combine(extractDest, "models", "gta3.img", "dff", "a.dff");
        File.WriteAllText(aPath, new string('Z', ImgEntry.SectorSize + 100));

        var estimate = RebuildEstimator.Estimate(extractDest);

        var rebuildOutput = TestScratch.NewDir();
        Rebuilder.RebuildNewPlayableCopy(extractDest, rebuildOutput);

        // Filtered the same way the production code filters: this exFAT test volume creates
        // "._*" AppleDouble sidecar files alongside anything written to it, which aren't real
        // game content and shouldn't count toward either figure.
        var actualNewFolderTotal = Directory.EnumerateFiles(rebuildOutput, "*", SearchOption.AllDirectories)
            .Where(p => !FileFilters.IsIgnoredFile(Path.GetFileName(p)))
            .Sum(p => new FileInfo(p).Length);

        Assert.Equal(estimate.NewFolderTotalBytes, actualNewFolderTotal);
        Assert.Equal(estimate.NewFolderTotalBytes + estimate.OriginalArchivesTotalBytes, estimate.InPlaceWithBackupTotalBytes);
    }

    [Fact]
    public void EstimateExtractedSizeOnDiskBytes_rounds_each_entry_up_to_the_cluster_size()
    {
        var gameRoot = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(gameRoot, "models"));
        File.WriteAllText(Path.Combine(gameRoot, "gta_sa.exe"), "stub");

        // Two tiny entries, each far smaller than a typical large cluster (e.g. exFAT's 128KB-1MB
        // defaults on big removable drives) — this is exactly the scenario that makes "content
        // size" and "size on disk" diverge so much for ~21k small game files.
        ImgArchive.Write(Path.Combine(gameRoot, "models", "gta3.img"), new[]
        {
            File_("a.dff", "x"), // 1 content byte -> 1 sector (2048 bytes) allocated inside the archive
            File_("b.txd", "y"),
        });

        var found = GameScanner.FindArchives(gameRoot);

        // gta_sa.exe is a loose (non-archive) file, so extraction now mirrors it byte-for-byte too —
        // it's not sector-rounded like archive entries, since it's copied as-is, not unpacked.
        const int looseFileBytes = 4; // "stub"

        var contentOnly = Extractor.EstimateExtractedSizeBytes(gameRoot, found, includeAudio: false);
        Assert.Equal(2 * ImgEntry.SectorSize + looseFileBytes, contentOnly); // 2 archive entries (1 sector each) + the loose exe

        const long clusterSize = 131072; // 128KB, a realistic exFAT cluster size on a large SD card
        var onDisk = Extractor.EstimateExtractedSizeOnDiskBytes(gameRoot, found, includeAudio: false, clusterSize);
        Assert.Equal(3 * clusterSize, onDisk); // each of the 3 files wastes up to a full 128KB cluster, however small

        Assert.True(onDisk > contentOnly);

        // A cluster size <= 0 must fall back to the plain content total, not divide by zero.
        Assert.Equal(contentOnly, Extractor.EstimateExtractedSizeOnDiskBytes(gameRoot, found, includeAudio: false, 0));
    }
}
