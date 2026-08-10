namespace SAFT.Core;

public sealed record AdditionProgress(string Stage, int FilesDone, int FilesTotal);

/// <summary>What actually happened when a mod's additions were installed.</summary>
public sealed record AdditionInstallResult(
    AddedMod Recorded,
    IReadOnlyList<string> Problems);

/// <summary>
/// Installs the ADDITION half of a mod: new assets into an archive, and the .ide/.ipl lines that
/// make the game load them.
///
/// SAFT writes its own map files under data/maps/saft/ and registers them in gta.dat, rather than
/// appending into Rockstar's. That isolation is deliberate — uninstall never performs surgery on a
/// shared file, another mod editing the same file can't be disturbed, and the worst a bug can do is
/// damage a file SAFT created. Same instinct as refusing .scm: prefer the design whose failure mode
/// is small.
/// </summary>
public static class AdditionInstaller
{
    public const string SaftMapFolder = "saft";
    public const string SaftIdeFileName = "saft_additions.ide";
    public const string SaftIplFileName = "saft_additions.ipl";

    /// <summary>Where new models and textures go. Map objects live in the main archive.</summary>
    public static readonly string DefaultArchiveRelativePath = Path.Combine("models", "gta3.img");

    /// <summary>
    /// The collision bundle a mod's .col records are merged into.
    ///
    /// This one is chosen because it is available everywhere on the map. Collision bundles are
    /// normally area-scoped — las_4.col serves part of Los Santos and nothing else — but multiobj.col
    /// carries collision for objects the game spawns by script (beach balls, police stingers), which
    /// can appear anywhere, so the engine keeps it loaded. Confirmed on a real install by placing the
    /// same object in Los Santos, Bone County, San Fierro and Las Venturas at once and loading the
    /// game: all four resolved their collision from this bundle.
    ///
    /// SAFT's instinct elsewhere is to write its own files rather than edit Rockstar's, and a
    /// saft_additions.col would fit that better. It isn't used because a new bundle's scope is
    /// untested — and the promise SAFT makes is that a mod works everywhere, not that it works where
    /// we happened to try it. Removal stays surgical: records come out by name, so Rockstar's own
    /// records are never rewritten.
    /// </summary>
    public const string SharedCollisionBundle = "multiobj.col";

    public static AdditionInstallResult Apply(
        string gameRoot,
        AdditionPlan plan,
        string modName,
        IProgress<AdditionProgress>? progress = null)
    {
        // Throttled at the door, so every progress?.Report below it - including the ones passed
        // down into the private helpers - costs a UI round trip ten times a second rather than
        // once per file. See ThrottledProgress: per-file reporting is what made a full extraction
        // take hours under Winlator.
        progress = new ThrottledProgress<AdditionProgress>(progress);

        var used = ObjectIdAllocator.ScanUsedIds(gameRoot);
        var allocated = ObjectIdAllocator.Allocate(used, plan.Definitions.Count);
        var rewritten = AdditionSnippets.Rewrite(plan.Definitions, plan.Placements, allocated);

        var record = new AddedMod
        {
            Name = modName,
            AddedAtUtc = DateTimeOffset.UtcNow,
            ObjectIds = allocated.ToList(),
        };

        AppendAssetsToArchive(gameRoot, plan, record, progress);
        WriteMapData(gameRoot, rewritten, record, progress);

        return new AdditionInstallResult(record, rewritten.Problems);
    }

    /// <summary>
    /// Rewrites the target archive with the mod's new entries appended. There's no way to add an
    /// entry in place — the directory table sits at the front and every offset after it would shift
    /// — so this is a full rebuild, which is why adding is slower than replacing.
    /// </summary>
    private static void AppendAssetsToArchive(
        string gameRoot, AdditionPlan plan, AddedMod record, IProgress<AdditionProgress>? progress)
    {
        var archivePath = Path.Combine(gameRoot, DefaultArchiveRelativePath);
        if (!File.Exists(archivePath)) throw new FileNotFoundException($"'{archivePath}' was not found.", archivePath);

        // Models and textures go in as their own entries. Collision does NOT: a .col file is a
        // bundle of records, and the game finds a record by searching the bundles it has loaded, so
        // dropping the mod's .col in as a separate entry would leave it unread. Its records have to
        // be merged into a bundle the game already loads.
        var assets = plan.NewAssets
            .Where(a => a.FileName.EndsWith(".dff", StringComparison.OrdinalIgnoreCase)
                     || a.FileName.EndsWith(".txd", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (assets.Count == 0 && plan.Collision.Count == 0) return;

        var files = new List<(string Name, Func<Stream> OpenContent)>();
        var rebuiltPath = archivePath + ".saft-tmp";

        // The old archive stays OPEN while the new one is written, and every entry is streamed
        // straight across. It used to be copied out to the system temp folder first, on the theory
        // that the source couldn't be read while being replaced — but the replacement is a different
        // file, so there was never anything to work around.
        //
        // That staging was expensive and, under Winlator, close to fatal: the temp folder lives
        // inside the Wine container on the device's internal storage, so a 900 MB archive was copied
        // off the SD card onto internal storage, read back, and written out again. On a device kept
        // deliberately empty of internal storage it simply stopped, with 8,200 of 16,320 files
        // copied and no way to tell whether it was working. Streaming touches the card only.
        using (var existing = ImgArchive.Open(archivePath))
        {
            foreach (var entry in existing.Entries)
            {
                // The merged collision bundle is small enough to hold in memory, which keeps the
                // whole job to ONE rebuild rather than a second pass for collision.
                if (plan.Collision.Count > 0 &&
                    entry.Name.Equals(SharedCollisionBundle, StringComparison.OrdinalIgnoreCase))
                {
                    var merged = MergeCollision(existing, entry, plan.Collision, record);
                    files.Add((entry.Name, () => new MemoryStream(merged, writable: false)));
                }
                else
                {
                    var carried = entry;
                    files.Add((entry.Name, () => existing.OpenEntry(carried)));
                }
            }

            foreach (var asset in assets)
            {
                files.Add((asset.FileName, () => File.OpenRead(asset.SourcePath)));
                record.ArchiveEntries.Add(new AddedArchiveEntry
                {
                    ArchiveRelativePath = DefaultArchiveRelativePath,
                    EntryName = asset.FileName,
                    Sha256 = AdditionsManifest.ComputeSha256(asset.SourcePath),
                });
            }

            ImgArchive.Write(rebuiltPath, files,
                (done, total) => progress?.Report(new AdditionProgress("Adding assets to the archive", done, total)),
                (done, total) => progress?.Report(new AdditionProgress("Reading the existing archive", done, total)));
        }

        // Swapped only after the source archive is closed: the crash-safe rename needs both files
        // free. The original is renamed aside, never deleted, and restored if anything goes wrong.
        FileReplace.MoveOver(rebuiltPath, archivePath);
    }

    /// <summary>
    /// Returns the shared collision bundle with this mod's records added, and records each one in
    /// the manifest so it can be taken out again.
    ///
    /// A record whose name is already in the bundle is left alone rather than duplicated: the game
    /// looks collision up by name, so a second record under the same name would be unreachable at
    /// best, and at worst would shadow the original for every other mod using it.
    /// </summary>
    private static byte[] MergeCollision(
        ImgArchive archive, ImgEntry entry, IReadOnlyList<ColRecord> additions, AddedMod record)
    {
        byte[] original;
        using (var stream = archive.OpenEntry(entry))
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            original = buffer.ToArray();
        }

        var existing = new HashSet<string>(
            ColBundle.Read(original).Select(r => r.Name), StringComparer.OrdinalIgnoreCase);

        var wanted = additions.Where(r => !existing.Contains(r.Name)).ToList();
        if (wanted.Count == 0) return original;

        foreach (var added in wanted)
        {
            record.Collisions.Add(new AddedCollision
            {
                BundleName = entry.Name,
                ModelName = added.Name,
                Sha256 = AdditionsManifest.ComputeSha256(added.Bytes),
            });
        }

        return ColBundle.Append(original, wanted);
    }

    /// <summary>
    /// Writes the definition and placement lines into SAFT's own map files, creating and registering
    /// them the first time. Lines are appended, so several mods coexist in one file and the manifest
    /// is what attributes each line back to the mod that added it.
    /// </summary>
    private static void WriteMapData(
        string gameRoot, RewrittenAddition rewritten, AddedMod record, IProgress<AdditionProgress>? progress)
    {
        if (rewritten.IdeLines.Count == 0 && rewritten.IplLines.Count == 0) return;

        progress?.Report(new AdditionProgress("Registering objects with the game", 0, 1));

        var mapFolder = Path.Combine(gameRoot, "data", "maps", SaftMapFolder);
        Directory.CreateDirectory(mapFolder);

        var idePath = Path.Combine(mapFolder, SaftIdeFileName);
        var iplPath = Path.Combine(mapFolder, SaftIplFileName);

        AppendSection(idePath, "objs", rewritten.IdeLines);
        AppendSection(iplPath, "inst", rewritten.IplLines);

        var ideRelative = Path.Combine("data", "maps", SaftMapFolder, SaftIdeFileName);
        var iplRelative = Path.Combine("data", "maps", SaftMapFolder, SaftIplFileName);

        foreach (var line in rewritten.IdeLines)
            record.DataLines.Add(new AddedDataLine { FileRelativePath = ideRelative, Line = line });
        foreach (var line in rewritten.IplLines)
            record.DataLines.Add(new AddedDataLine { FileRelativePath = iplRelative, Line = line });

        RegisterInGtaDat(gameRoot, ideRelative, iplRelative, record);

        progress?.Report(new AdditionProgress("Registering objects with the game", 1, 1));
    }

    /// <summary>
    /// Adds lines inside a named section, creating the file with that section if it doesn't exist
    /// yet. Existing entries are left exactly where they are.
    /// </summary>
    private static void AppendSection(string path, string section, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;

        if (!File.Exists(path))
        {
            var created = new List<string>
            {
                "# Created by SAFT. Objects added by mods live here so that the game's own map files",
                "# are never edited. Removing a mod removes only its own lines from this file.",
                section,
            };
            created.AddRange(lines);
            created.Add("end");
            File.WriteAllLines(path, created);
            return;
        }

        var existing = File.ReadAllLines(path).ToList();
        var endIndex = existing.FindLastIndex(l => l.Trim().Equals("end", StringComparison.OrdinalIgnoreCase));

        if (endIndex < 0)
        {
            existing.Add(section);
            existing.AddRange(lines);
            existing.Add("end");
        }
        else
        {
            existing.InsertRange(endIndex, lines);
        }

        File.WriteAllLines(path, existing);
    }

    /// <summary>
    /// Tells the game to load SAFT's map files. gta.dat lists every .ide/.ipl the game reads; a file
    /// that isn't listed there is simply ignored, however correct its contents.
    ///
    /// WHERE the lines go matters, and this was the cause of a crash that survived several other
    /// explanations. gta.dat groups its entries: every IDE first, then every IPL, exactly as the
    /// file's own opening comment says ("Load IDEs first, then the models and after that the IPLs").
    /// Appending both lines to the end of the file — which is what SAFT used to do — puts the
    /// definition after every placement the game has already processed, and San Andreas crashes on
    /// world load. Verified by moving nothing but these two lines: at the end of the file the game
    /// died instantly, and inside their proper blocks the same object loaded and rendered.
    ///
    /// So each line is inserted after the LAST existing entry of its own kind.
    /// </summary>
    private static void RegisterInGtaDat(string gameRoot, string ideRelative, string iplRelative, AddedMod record)
    {
        var gtaDatPath = Path.Combine(gameRoot, "data", "gta.dat");
        if (!File.Exists(gtaDatPath)) return;

        // The game's own file uses Windows separators regardless of the host platform.
        var ideEntry = "IDE " + ideRelative.Replace(Path.DirectorySeparatorChar, '\\');
        var iplEntry = "IPL " + iplRelative.Replace(Path.DirectorySeparatorChar, '\\');

        var lines = File.ReadAllLines(gtaDatPath).ToList();
        var added = new List<string>();

        // Registering twice would make the game load the same objects twice over, so each entry is
        // added only if it isn't already there — installing a second mod must not duplicate it.
        if (!lines.Any(l => l.Trim().Equals(ideEntry, StringComparison.OrdinalIgnoreCase))) added.Add(ideEntry);
        if (!lines.Any(l => l.Trim().Equals(iplEntry, StringComparison.OrdinalIgnoreCase))) added.Add(iplEntry);
        if (added.Count == 0) return;

        // The IPL goes in first: it belongs further down the file, so inserting it can't shift the
        // position the IDE was measured against.
        if (added.Contains(iplEntry)) InsertAfterLastOfKind(lines, "IPL ", iplEntry);
        if (added.Contains(ideEntry)) InsertAfterLastOfKind(lines, "IDE ", ideEntry);

        File.WriteAllLines(gtaDatPath, lines);

        var gtaDatRelative = Path.Combine("data", "gta.dat");
        foreach (var line in added)
            record.DataLines.Add(new AddedDataLine { FileRelativePath = gtaDatRelative, Line = line });
    }

    /// <summary>
    /// Puts <paramref name="entry"/> directly after the last line starting with <paramref name="keyword"/>,
    /// keeping gta.dat's grouping intact. If the game has no entries of that kind at all there is no
    /// block to join, so it goes at the end.
    /// </summary>
    private static void InsertAfterLastOfKind(List<string> lines, string keyword, string entry)
    {
        var last = -1;
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].TrimStart().StartsWith(keyword, StringComparison.OrdinalIgnoreCase)) last = i;

        if (last < 0) lines.Add(entry);
        else lines.Insert(last + 1, entry);
    }
}
