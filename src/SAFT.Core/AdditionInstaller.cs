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

    /// <param name="archiveReplacements">
    /// Entry name -> file to write into it. Files the direct installer would otherwise have rebuilt
    /// the whole archive for on its own, folded into this one rewrite.
    /// </param>
    /// <param name="entriesToDrop">
    /// Entries to leave OUT of the rebuilt archive — the previously installed copy of this mod, when
    /// this is a reinstall. Handed over by <see cref="AdditionUninstaller.Remove"/> so that removing
    /// the old copy and adding the new one cost one rewrite between them rather than one each.
    /// </param>
    /// <param name="collisionRecordsToPrune">
    /// Bundle name -> record names to take out of it, the collision half of the same handover.
    /// </param>
    public static AdditionInstallResult Apply(
        string gameRoot,
        AdditionPlan plan,
        string modName,
        IProgress<AdditionProgress>? progress = null,
        IReadOnlyDictionary<string, string>? archiveReplacements = null,
        IReadOnlySet<string>? entriesToDrop = null,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? collisionRecordsToPrune = null,
        StorageSpeed? speed = null,
        Action<string>? onStep = null)
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

        var notes = AppendAssetsToArchive(
            gameRoot, plan, record, progress, archiveReplacements, entriesToDrop, collisionRecordsToPrune,
            speed, onStep);
        WriteMapData(gameRoot, rewritten, record, progress);

        // Anything the handed-over removal couldn't do is reported alongside the addition's own
        // problems rather than being swallowed — it is the uninstaller's "skipped" list arriving by
        // a different route.
        var problems = notes.Count == 0 ? rewritten.Problems : rewritten.Problems.Concat(notes).ToList();
        return new AdditionInstallResult(record, problems);
    }

    /// <summary>
    /// Rewrites the target archive with the mod's new entries appended. There's no way to add an
    /// entry in place — the directory table sits at the front and every offset after it would shift
    /// — so this is a full rebuild, which is why adding is slower than replacing.
    /// </summary>
    private static IReadOnlyList<string> AppendAssetsToArchive(
        string gameRoot, AdditionPlan plan, AddedMod record, IProgress<AdditionProgress>? progress,
        IReadOnlyDictionary<string, string>? archiveReplacements = null,
        IReadOnlySet<string>? entriesToDrop = null,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? collisionRecordsToPrune = null,
        StorageSpeed? speed = null,
        Action<string>? onStep = null)
    {
        var notes = new List<string>();
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
        // Replacements handed over by the direct installer count as work to do here, even when the
        // mod adds no assets of its own — skipping out early would silently drop them.
        var replacements = archiveReplacements ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Same for a reinstall's handed-over removals: they are the whole reason this rewrite is
        // happening when the new copy of the mod adds nothing the old one didn't. Leaving them out of
        // this test would drop the removal on the floor and leave the old copy installed.
        var drops = entriesToDrop ?? (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prunes = collisionRecordsToPrune
                     ?? new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);

        // An asset the mod defines can already be in the archive, left there by an earlier round of
        // the same mod that did not come all the way out. It is still the mod's file - that is what
        // lets a game in that state heal on the next install rather than staying stuck - but a
        // SECOND entry under a name the directory table already carries is a corrupt archive, not a
        // tidy-up: the game reads whichever it finds first, and the uninstall removes one and leaves
        // the other. The old copy is dropped in the same pass that writes the new one.
        var alreadyPresent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var current = ImgArchive.Open(archivePath);
            foreach (var entry in current.Entries) alreadyPresent.Add(entry.Name);
        }
        catch (Exception ex)
        {
            // Unreadable here means the write below fails with a better message than anything this
            // could say. Nothing has been touched yet.
            onStep?.Invoke($"additions: could not read the archive's current entries ({ex.GetType().Name})");
        }

        var supersede = assets.Where(a => alreadyPresent.Contains(a.FileName)).Select(a => a.FileName).ToList();
        if (supersede.Count > 0)
        {
            onStep?.Invoke($"additions: replacing {supersede.Count} leftover copy/copies of this mod's own asset(s)");
            var widened = new HashSet<string>(drops, StringComparer.OrdinalIgnoreCase);
            foreach (var name in supersede) widened.Add(name);
            drops = widened;
        }
        if (assets.Count == 0 && plan.Collision.Count == 0 && replacements.Count == 0
            && drops.Count == 0 && prunes.Count == 0) return notes;

        // EXPERIMENTAL fast path. Everything below it is the rebuild that has always been here, and it
        // stays the fallback: if the archive cannot take this job in place, or anything about it looks
        // unfamiliar, this returns false having touched nothing and the rebuild runs exactly as before.
        if (UseInPlaceEditing &&
            TryInPlace(archivePath, plan, record, assets, replacements, drops, prunes, notes, onStep))
        {
            return notes;
        }

        var files = new List<(string Name, Func<Stream> OpenContent)>();
        var rebuiltPath = archivePath + ".saft-tmp";
        var dropped = 0;
        var bundleChanged = false;

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
                var merging = plan.Collision.Count > 0 &&
                              entry.Name.Equals(SharedCollisionBundle, StringComparison.OrdinalIgnoreCase);
                var pruning = prunes.TryGetValue(entry.Name, out var recordsToPrune);

                // The merged collision bundle is small enough to hold in memory, which keeps the
                // whole job to ONE rebuild rather than a second pass for collision.
                if (merging || pruning)
                {
                    var before = AdditionUninstaller.ReadEntry(existing, entry);
                    var bundle = before;

                    // ORDER MATTERS, and it is the one real hazard in the reinstall fold. A reinstall
                    // prunes the old copy's records and merges the new copy's records into the same
                    // bundle, and the two sets have the SAME NAMES. Merge first and every new record
                    // looks like a duplicate of one already there, so nothing is added — and then the
                    // prune takes the old ones out, leaving the mod with no collision at all and
                    // every placed object crashing the game on load. Pruning first is what makes the
                    // merge see a bundle the mod is genuinely absent from.
                    if (pruning)
                    {
                        bundle = AdditionUninstaller.PruneCollision(
                            bundle, entry.Name, recordsToPrune!, notes, out _);
                    }
                    if (merging) bundle = MergeCollision(bundle, entry.Name, plan.Collision, record);

                    if (!ReferenceEquals(bundle, before)) bundleChanged = true;
                    files.Add((entry.Name, () => new MemoryStream(bundle, writable: false)));
                }
                else if (replacements.TryGetValue(entry.Name, out var replacementPath))
                {
                    // A file the direct installer would otherwise have rewritten this whole archive
                    // for on its own. Streamed from the mod folder as this one rebuild goes past.
                    //
                    // Checked BEFORE the drop below: an entry that is being written with new content
                    // is not one to delete. In practice the caller sorts this out first — a name in
                    // both sets is either re-added as an addition or kept as a replacement, never
                    // handed over as both — but if it ever were, keeping the file beats losing it.
                    files.Add((entry.Name, () => File.OpenRead(replacementPath)));
                }
                else if (drops.Contains(entry.Name))
                {
                    // The previously installed copy. Left out of the rebuild, which is how removal
                    // works here too — an archive has no way to excise an entry in place.
                    dropped++;
                }
                else
                {
                    var carried = entry;
                    files.Add((entry.Name, () => existing.OpenEntry(carried)));
                }
            }

            // Nothing this rewrite was asked to do turned out to be a change: the entries handed over
            // for removal had already gone, and the records to prune with them. Writing 940 MB to
            // produce a byte-identical archive is the most expensive kind of nothing, so don't — the
            // uninstaller has always had the same guard on its own rebuild.
            if (assets.Count == 0 && replacements.Count == 0 && dropped == 0 && !bundleChanged)
                return notes;

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
                (done, total) => progress?.Report(new AdditionProgress("Reading the existing archive", done, total)),
                speed is null ? null : speed.Sample);
        }

        // Swapped only after the source archive is closed: the crash-safe rename needs both files
        // free. The original is renamed aside, never deleted, and restored if anything goes wrong.
        FileReplace.MoveOver(rebuiltPath, archivePath);
        return notes;
    }

    /// <summary>
    /// Whether to try editing archives in place instead of rebuilding them. EXPERIMENTAL — see
    /// <see cref="ImgArchiveEditor"/> for what it trades away. Turn it off to get 2.0 Stable's
    /// behaviour back exactly.
    /// </summary>
    public static bool UseInPlaceEditing { get; set; } = true;

    /// <summary>
    /// Does the whole job by editing the archive in place: drop the old copy's entries, repoint the
    /// shared collision bundle at new contents, append the new entries. A few megabytes written
    /// instead of 940.
    ///
    /// Feasibility is settled BEFORE anything is computed or recorded, because the fallback rebuild
    /// does its own bookkeeping and would double-count anything written down here first. After that
    /// point every step is one the archive can take.
    /// </summary>
    private static bool TryInPlace(
        string archivePath, AdditionPlan plan, AddedMod record, IReadOnlyList<NewAsset> assets,
        IReadOnlyDictionary<string, string> replacements, IReadOnlySet<string> drops,
        IReadOnlyDictionary<string, IReadOnlySet<string>> prunes, List<string> notes,
        Action<string>? onStep)
    {
        List<ImgEntry> entries;
        try
        {
            entries = ImgArchive.ReadDirectory(archivePath).ToList();
            if (new FileInfo(archivePath).Length % ImgEntry.SectorSize != 0) return false;
        }
        catch
        {
            return false;
        }

        var names = new HashSet<string>(entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        var dropsPresent = drops.Where(names.Contains).ToList();

        // Removing frees slots, so a reinstall that takes seven out and puts seven back needs none of
        // its own - which is exactly the case worth being fast.
        var slotsAfterRemoval = ImgArchiveEditor.SpareSlots(archivePath) + dropsPresent.Count;
        if (assets.Count > slotsAfterRemoval)
        {
            onStep?.Invoke(
                $"archive: {assets.Count} new entry/entries needs more room than the directory has " +
                $"({slotsAfterRemoval} free), rebuilding instead");
            return false;
        }

        var survivingNames = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        survivingNames.ExceptWith(dropsPresent);

        foreach (var asset in assets)
            if (survivingNames.Contains(asset.FileName)) return false;
        foreach (var name in replacements.Keys)
            if (!survivingNames.Contains(name)) return false;

        var bundleNames = new List<string>();
        if (plan.Collision.Count > 0 && survivingNames.Contains(SharedCollisionBundle))
            bundleNames.Add(SharedCollisionBundle);
        foreach (var bundle in prunes.Keys)
            if (survivingNames.Contains(bundle) && !bundleNames.Contains(bundle, StringComparer.OrdinalIgnoreCase))
                bundleNames.Add(bundle);
        if (plan.Collision.Count > 0 && !bundleNames.Contains(SharedCollisionBundle, StringComparer.OrdinalIgnoreCase))
            return false;   // the mod has collision but the bundle is missing: not a job for this path

        // Past this line the archive is being changed, and every step has been checked as possible.
        var updates = new List<(string Name, Func<Stream> OpenContent)>();

        if (bundleNames.Count > 0)
        {
            using var archive = ImgArchive.Open(archivePath);
            foreach (var bundleName in bundleNames)
            {
                var entry = archive.Entries.First(e => e.Name.Equals(bundleName, StringComparison.OrdinalIgnoreCase));
                var before = AdditionUninstaller.ReadEntry(archive, entry);
                var bundle = before;

                // Prune before merge, for the same reason as the rebuild path: the old copy's records
                // and the new copy's share names, and merging first makes the new ones look like
                // duplicates of records about to be deleted.
                if (prunes.TryGetValue(bundleName, out var recordsToPrune))
                    bundle = AdditionUninstaller.PruneCollision(bundle, bundleName, recordsToPrune, notes, out _);
                if (plan.Collision.Count > 0 && bundleName.Equals(SharedCollisionBundle, StringComparison.OrdinalIgnoreCase))
                    bundle = MergeCollision(bundle, bundleName, plan.Collision, record);

                // Compared by CONTENT, and by the same measure of content used everywhere else here:
                // trailing zeros ignored, because what comes back out of an archive carries the
                // sector padding that was added on the way in, and what has just been rebuilt in
                // memory does not. Comparing raw bytes calls those two different and appends a
                // duplicate copy of an unchanged bundle - growing the archive to achieve nothing. The
                // rebuild path gets away without this check because rebuilding unchanged contents
                // produces an identical file anyway; editing in place has no such luxury.
                if (AdditionsManifest.ComputeSha256(bundle) != AdditionsManifest.ComputeSha256(before))
                    updates.Add((bundleName, () => new MemoryStream(bundle, writable: false)));
            }
        }

        foreach (var (name, path) in replacements)
            updates.Add((name, () => File.OpenRead(path)));

        if (dropsPresent.Count == 0 && updates.Count == 0 && assets.Count == 0)
        {
            onStep?.Invoke("archive: nothing actually changed, leaving it alone");
            return true;
        }

        onStep?.Invoke(
            $"archive: editing in place - {dropsPresent.Count} entry/entries out, {updates.Count} replaced, " +
            $"{assets.Count} added (no rebuild)");

        if (dropsPresent.Count > 0 &&
            ImgArchiveEditor.TryRemove(archivePath, dropsPresent, out _) == ImgArchiveEditor.Outcome.NotPossible)
            return false;

        if (updates.Count > 0 &&
            ImgArchiveEditor.TryReplace(archivePath, updates) == ImgArchiveEditor.Outcome.NotPossible)
            return false;

        if (assets.Count > 0)
        {
            var additions = assets
                .Select(a => (a.FileName, (Func<Stream>)(() => File.OpenRead(a.SourcePath))))
                .ToList();

            if (ImgArchiveEditor.TryAppend(archivePath, additions) == ImgArchiveEditor.Outcome.NotPossible)
                return false;

            foreach (var asset in assets)
            {
                record.ArchiveEntries.Add(new AddedArchiveEntry
                {
                    ArchiveRelativePath = DefaultArchiveRelativePath,
                    EntryName = asset.FileName,
                    Sha256 = AdditionsManifest.ComputeSha256(asset.SourcePath),
                });
            }
        }

        var dead = ImgArchiveEditor.DeadBytes(archivePath) / (1024.0 * 1024.0);
        onStep?.Invoke($"archive: edited in place; {dead:0.0} MB of the archive is now unused space");
        return true;
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
        byte[] original, string bundleName, IReadOnlyList<ColRecord> additions, AddedMod record)
    {
        var existing = new HashSet<string>(
            ColBundle.Read(original).Select(r => r.Name), StringComparer.OrdinalIgnoreCase);

        var wanted = additions.Where(r => !existing.Contains(r.Name)).ToList();
        if (wanted.Count == 0) return original;

        foreach (var added in wanted)
        {
            record.Collisions.Add(new AddedCollision
            {
                BundleName = bundleName,
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
