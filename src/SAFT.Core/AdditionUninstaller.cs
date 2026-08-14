namespace SAFT.Core;

/// <summary>What removing a mod's additions actually did, including anything deliberately left alone.</summary>
/// <param name="DeferredEntryRemovals">
/// Entries that were NOT taken out here because their archive was deferred, keyed by archive relative
/// path. The caller is expected to hand these to <see cref="AdditionInstaller.Apply"/> so they come out
/// during its rewrite instead. They are not counted in <see cref="ArchiveEntriesRemoved"/>: nothing has
/// been removed yet.
/// </param>
/// <param name="DeferredCollisionPrunes">
/// Collision records that still have to come out of a bundle in a deferred archive, keyed by bundle
/// name. Handed over the same way — losing these would leave another mod's or Rockstar's bundle
/// carrying records for a mod that is no longer installed.
/// </param>
public sealed record AdditionRemovalResult(
    IReadOnlyList<string> RemovedMods,
    IReadOnlyList<string> Skipped,
    int ArchiveEntriesRemoved,
    int DataLinesRemoved,
    IReadOnlyList<int> FreedObjectIds,
    IReadOnlyDictionary<string, IReadOnlySet<string>> DeferredEntryRemovals,
    IReadOnlyDictionary<string, IReadOnlySet<string>> DeferredCollisionPrunes);

/// <summary>
/// Takes back out what <see cref="AdditionInstaller"/> put in.
///
/// Two rules govern everything here. Removal is SURGICAL: only this mod's own lines go, so other
/// mods installed since survive untouched — reverting a data file wholesale would silently delete
/// their work. And removal is VERIFIED: anything the user has changed since installing is left
/// alone and reported, because a file that no longer matches what SAFT wrote is no longer SAFT's
/// to delete.
/// </summary>
public static class AdditionUninstaller
{
    /// <param name="deferRebuildsFor">
    /// Archives this removal must NOT rewrite. Everything else it does — the map data, the manifest,
    /// the freed object ids — happens as usual; what would have been rewritten out of those archives
    /// comes back in the result for the caller to hand to whoever IS rewriting them. Reinstalling used
    /// to write models\gta3.img out twice, 940 MB each time, because removing the old copy and adding
    /// the new one each rebuilt it in full. Same fold as the replacement one, from the other side.
    /// </param>
    /// <param name="onStep">
    /// Phase-level breadcrumbs for the activity log. Removal used to run from its first line to its
    /// archive rewrite without saying anything, and a run died inside that silence - so the log's
    /// last word was the caller announcing removal was about to start, and everything from the
    /// manifest read to the verification was one indistinguishable gap.
    /// </param>
    public static AdditionRemovalResult Remove(
        string gameRoot,
        AdditionsManifest manifest,
        IEnumerable<string> modNames,
        IProgress<AdditionProgress>? progress = null,
        IReadOnlySet<string>? deferRebuildsFor = null,
        Action<string>? onStep = null,
        StorageSpeed? speed = null)
    {
        // Throttled at the door, exactly as every other writer in SAFT does it — this was the ONE
        // path that never got it. Removing a mod rewrites the whole archive, reporting once per
        // entry, so on a real device it marshalled 16,316 progress updates onto the UI thread
        // through Wine and crawled: about 300 entries a minute, where the same archive had been
        // rewritten in 36 seconds minutes earlier. That is roughly 285 KB/s, which no SD card
        // produces — it was never the card, it was this. See ThrottledProgress, and the identical
        // comment in AdditionInstaller/Extractor/DirectModInstaller/ModInstaller/Rebuilder.
        progress = new ThrottledProgress<AdditionProgress>(progress);

        var wanted = new HashSet<string>(modNames, StringComparer.OrdinalIgnoreCase);
        var mods = manifest.Mods.Where(m => wanted.Contains(m.Name)).ToList();
        onStep?.Invoke($"removal: starting for {mods.Count} mod(s)");

        var removedMods = new List<string>();
        var skipped = new List<string>();

        // Mods that came out only partly, and the assets of theirs still sitting in the game. These
        // stay on the record rather than being struck off it — see where the record is rewritten.
        var retained = new Dictionary<AddedMod, List<AddedArchiveEntry>>();
        var freedIds = new List<int>();
        var linesRemoved = 0;

        // Gathered across every mod so the archive is rebuilt once rather than once per mod.
        var entriesToRemove = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // Collision records to prune, keyed by the bundle holding them. These live INSIDE an archive
        // entry rather than being entries themselves, so they ride along with the same rebuild.
        var collisionToRemove = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods)
        {
            // Grouped by archive so each one is OPENED ONCE for all of this mod's entries. It used to
            // be opened once per entry - seven separate opens of a 940 MB file to check seven small
            // ones - and every open means a stat and a fresh handle through Wine's filesystem
            // translation onto an SD card. A run died in this exact window, in a step the log had
            // nothing to say about. Whether that is why, nobody knows; six opens that did not need to
            // happen are worth removing either way.
            var stillInTheGame = new List<AddedArchiveEntry>();

            foreach (var group in mod.ArchiveEntries.GroupBy(
                         e => e.ArchiveRelativePath, StringComparer.OrdinalIgnoreCase))
            {
                var entries = group.ToList();
                onStep?.Invoke($"removal: checking {entries.Count} entry/entries in {group.Key} are still SAFT's");

                foreach (var name in VerifyArchiveEntries(gameRoot, group.Key, entries, skipped, stillInTheGame))
                {
                    if (!entriesToRemove.TryGetValue(group.Key, out var set))
                        entriesToRemove[group.Key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(name);
                }
            }

            onStep?.Invoke($"removal: '{mod.Name}' - taking out its map data lines");

            foreach (var added in mod.Collisions)
            {
                if (!collisionToRemove.TryGetValue(added.BundleName, out var set))
                    collisionToRemove[added.BundleName] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(added.ModelName);
            }

            linesRemoved += RemoveDataLines(gameRoot, mod, skipped);
            freedIds.AddRange(mod.ObjectIds);

            if (stillInTheGame.Count == 0) removedMods.Add(mod.Name);
            else retained[mod] = stillInTheGame;
        }

        // A mod that added only collision still needs its archive rebuilt, so the set of archives to
        // touch is the union of both kinds of removal rather than just the entry removals.
        var archives = new HashSet<string>(entriesToRemove.Keys, StringComparer.OrdinalIgnoreCase);
        if (collisionToRemove.Count > 0) archives.Add(AdditionInstaller.DefaultArchiveRelativePath);

        var entriesRemoved = 0;
        var deferredEntries = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        var deferredCollision = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var archiveRelativePath in archives)
        {
            entriesToRemove.TryGetValue(archiveRelativePath, out var names);
            names ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (deferRebuildsFor is not null && deferRebuildsFor.Contains(archiveRelativePath))
            {
                onStep?.Invoke(
                    $"removal: {names.Count} entry/entries in {archiveRelativePath} handed on rather than rewritten here");

                // Handed over rather than done here. Both halves have to go: the entries AND the
                // collision records, since the records live inside an entry that is being carried
                // across by whoever does the rewrite. Only what was verified above is handed over —
                // an entry the user has changed since installing was already skipped and stays put.
                if (names.Count > 0) deferredEntries[archiveRelativePath] = names;
                foreach (var bundle in collisionToRemove)
                    deferredCollision[bundle.Key] = new HashSet<string>(bundle.Value, StringComparer.OrdinalIgnoreCase);
                continue;
            }

            onStep?.Invoke($"removal: taking {names.Count} entry/entries out of {archiveRelativePath}");
            entriesRemoved += RebuildArchiveWithout(
                gameRoot, archiveRelativePath, names, collisionToRemove, skipped, progress, speed, onStep);
        }

        // A mod only leaves the record once nothing of it is left in the game.
        //
        // This used to be an unconditional Remove, and that was a quiet, permanent bug. An entry
        // whose bytes had changed since install is deliberately LEFT IN PLACE - deleting it would
        // destroy work SAFT never did - and the mod was struck from the record anyway. The asset then
        // sat in the archive with nothing describing it: invisible to every future uninstall, because
        // every future uninstall reads this file. The summary said the mod had been removed, and the
        // only way to find out otherwise was to open the archive and look.
        //
        // Now what could not be removed stays written down. The record keeps exactly the entries
        // still in the game and drops everything that did come out - the map lines, the collision,
        // the object ids - so running uninstall again retries only what is left rather than trying to
        // remove the same lines twice.
        foreach (var mod in mods)
        {
            if (!retained.TryGetValue(mod, out var survivors))
            {
                manifest.Mods.Remove(mod);
                continue;
            }

            mod.ArchiveEntries.Clear();
            mod.ArchiveEntries.AddRange(survivors);
            mod.DataLines.Clear();
            mod.Collisions.Clear();
            mod.ObjectIds.Clear();

            onStep?.Invoke(
                $"removal: '{mod.Name}' is still recorded - {survivors.Count} asset(s) could not be " +
                "removed and are still in your game");
        }

        // The gta.dat registration is shared by every addition, so it only comes out once nothing is
        // left that depends on it — removing it early would stop other mods' objects loading.
        if (manifest.Mods.Count == 0) RemoveGtaDatRegistration(gameRoot);

        onStep?.Invoke(
            $"removal: complete - {removedMods.Count} mod(s), {entriesRemoved} entry/entries removed here, " +
            $"{linesRemoved} map line(s), {skipped.Count} left alone");

        return new AdditionRemovalResult(
            removedMods, skipped, entriesRemoved, linesRemoved, freedIds, deferredEntries, deferredCollision);
    }

    /// <summary>
    /// Confirms each entry is still byte-for-byte what SAFT put there, and returns the names of the
    /// ones that are. If the user has since replaced one — installed another mod over the top, or
    /// edited it — deleting it would destroy work SAFT never did, so it's left in place and reported.
    ///
    /// Takes the whole list for one archive rather than a single entry, because the archive is opened
    /// to answer the question and a 940 MB file should be opened once for seven answers, not seven
    /// times for one each.
    /// </summary>
    /// <param name="stillInTheGame">
    /// Entries this refuses to remove which are, as far as it can tell, STILL THERE. The caller keeps
    /// these in the record so a later uninstall can try again — see the note where the record is
    /// rewritten. An entry that has simply gone is not added here: there is nothing left to describe.
    ///
    /// When the archive cannot be read at all, every entry in it lands here. That errs towards
    /// keeping a record for something already gone, which costs a confusing line in the uninstall
    /// summary; the other direction costs the only description of assets sitting in someone's game,
    /// which cannot be recovered.
    /// </param>
    private static List<string> VerifyArchiveEntries(
        string gameRoot, string archiveRelativePath, IReadOnlyList<AddedArchiveEntry> entries,
        List<string> skipped, List<AddedArchiveEntry> stillInTheGame)
    {
        var verified = new List<string>();
        var archivePath = Path.Combine(gameRoot, archiveRelativePath);

        if (!File.Exists(archivePath))
        {
            foreach (var entry in entries)
            {
                skipped.Add($"{entry.EntryName}: the archive it was added to no longer exists");
                stillInTheGame.Add(entry);
            }

            return verified;
        }

        try
        {
            using var archive = ImgArchive.Open(archivePath);

            // Looked up by name rather than scanned per entry: the directory table holds 16,316 of
            // them in a stock gta3.img.
            var byName = new Dictionary<string, ImgEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in archive.Entries) byName.TryAdd(e.Name, e);

            foreach (var entry in entries)
            {
                if (!byName.TryGetValue(entry.EntryName, out var found))
                {
                    skipped.Add($"{entry.EntryName}: it is no longer in the archive");
                    continue;
                }

                // ComputeSha256 ignores trailing zeros on both sides, so the sector padding an archive
                // adds doesn't make an untouched asset look modified.
                if (AdditionsManifest.ComputeSha256(ReadEntry(archive, found)) != entry.Sha256)
                {
                    skipped.Add($"{entry.EntryName}: it has been changed since SAFT added it, so it was left in place");
                    stillInTheGame.Add(entry);
                    continue;
                }

                verified.Add(entry.EntryName);
            }
        }
        catch (Exception ex)
        {
            // One failure to read the archive answers for every entry in it - the same message each
            // entry would have reported on its own. They stay on the record, because an archive that
            // could not be opened has told us nothing about what is inside it.
            foreach (var entry in entries)
            {
                skipped.Add($"{entry.EntryName}: {ex.Message}");
                stillInTheGame.Add(entry);
            }

            return new List<string>();
        }

        return verified;
    }

    /// <summary>
    /// Deletes a mod's own lines from the map files, matching on the exact text rather than on line
    /// numbers — those shift every time another mod is installed or removed above them.
    /// </summary>
    private static int RemoveDataLines(string gameRoot, AddedMod mod, List<string> skipped)
    {
        var removed = 0;

        foreach (var group in mod.DataLines.GroupBy(l => l.FileRelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(gameRoot, group.Key);
            if (!File.Exists(path))
            {
                skipped.Add($"{group.Key}: the file no longer exists");
                continue;
            }

            // gta.dat's registration is shared between mods and is handled separately, once.
            if (Path.GetFileName(path).Equals("gta.dat", StringComparison.OrdinalIgnoreCase)) continue;

            var lines = File.ReadAllLines(path).ToList();
            foreach (var wanted in group)
            {
                var index = lines.FindIndex(l => l.Trim().Equals(wanted.Line.Trim(), StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    skipped.Add($"a line in {group.Key} was already gone or had been edited");
                    continue;
                }
                lines.RemoveAt(index);
                removed++;
            }

            File.WriteAllLines(path, lines);
        }

        return removed;
    }

    /// <summary>
    /// Rewrites an archive without the named entries. As with adding, there's no way to excise an
    /// entry in place, so this is a full rebuild — the reason removing an addition costs minutes
    /// where removing a replacement costs seconds.
    /// </summary>
    private static int RebuildArchiveWithout(
        string gameRoot,
        string archiveRelativePath,
        ISet<string> names,
        IReadOnlyDictionary<string, HashSet<string>> collisionToRemove,
        List<string> skipped,
        IProgress<AdditionProgress>? progress,
        StorageSpeed? speed = null,
        Action<string>? onStep = null)
    {
        var archivePath = Path.Combine(gameRoot, archiveRelativePath);
        if (!File.Exists(archivePath)) return 0;

        // EXPERIMENTAL fast path. Taking entries out needs no rebuild at all: the records come out of
        // the directory table and the data is simply left behind as dead space. Everything below this
        // is the rebuild that has always been here, and it still runs whenever the fast path declines.
        if (AdditionInstaller.UseInPlaceEditing &&
            TryRemoveInPlace(archivePath, archiveRelativePath, names, collisionToRemove, skipped, onStep, out var takenOut))
        {
            return takenOut;
        }

        var keep = new List<(string Name, Func<Stream> OpenContent)>();
        var removed = 0;
        var prunedRecords = 0;
        var rebuilt = archivePath + ".saft-tmp";

        // Entries are streamed straight from the old archive into the new one. They used to be
        // copied out to the system temp folder first — which under Winlator means the Wine
        // container's own storage, not the card the game sits on. Removing a mod moved 900 MB onto
        // internal storage and back, and on a device with little of it free that stalled outright.
        using (var archive = ImgArchive.Open(archivePath))
        {
            foreach (var entry in archive.Entries)
            {
                if (names.Contains(entry.Name)) { removed++; continue; }

                if (collisionToRemove.TryGetValue(entry.Name, out var records))
                {
                    var pruned = PruneCollision(ReadEntry(archive, entry), entry.Name, records, skipped, out var count);
                    prunedRecords += count;
                    keep.Add((entry.Name, () => new MemoryStream(pruned, writable: false)));
                }
                else
                {
                    var carried = entry;
                    keep.Add((entry.Name, () => archive.OpenEntry(carried)));
                }
            }

            // A mod may have added collision and nothing else, in which case no ENTRY is going away
            // but the archive still has to be rewritten to drop the records.
            if (removed == 0 && prunedRecords == 0) return 0;

            ImgArchive.Write(rebuilt, keep,
                (done, total) => progress?.Report(new AdditionProgress("Removing added assets from the archive", done, total)),
                (done, total) => progress?.Report(new AdditionProgress("Reading the existing archive", done, total)),
                speed is null ? null : speed.Sample);
        }

        FileReplace.MoveOver(rebuilt, archivePath);
        return removed;
    }

    /// <summary>Reads a whole entry into memory. Only used for collision bundles, which are small.</summary>
    internal static byte[] ReadEntry(ImgArchive archive, ImgEntry entry)
    {
        using var stream = archive.OpenEntry(entry);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Takes the entries out without rebuilding: their records leave the directory table, their data
    /// stays behind as dead space, and any shared collision bundle is repointed at a pruned copy.
    ///
    /// Uninstalling was the half of this that never got done. Installing was made to write a few
    /// megabytes instead of 940 and removing was left rebuilding the whole archive, which is why
    /// taking a mod off was still slow - and slow in the way that fills an SD card's write buffer and
    /// makes everything else on the device crawl.
    /// </summary>
    private static bool TryRemoveInPlace(
        string archivePath, string archiveRelativePath, ISet<string> names,
        IReadOnlyDictionary<string, HashSet<string>> collisionToRemove, List<string> skipped,
        Action<string>? onStep, out int removed)
    {
        removed = 0;

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

        var present = new HashSet<string>(entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        var toRemove = names.Where(present.Contains).ToList();

        // Only bundles that are actually here, and only if pruning them changes anything.
        var prunedBundles = new List<(string Name, Func<Stream> OpenContent)>();
        if (collisionToRemove.Count > 0)
        {
            using var archive = ImgArchive.Open(archivePath);
            foreach (var (bundleName, records) in collisionToRemove)
            {
                var entry = archive.Entries.FirstOrDefault(
                    e => e.Name.Equals(bundleName, StringComparison.OrdinalIgnoreCase));
                if (entry is null) continue;
                if (toRemove.Contains(bundleName, StringComparer.OrdinalIgnoreCase)) continue;

                var before = ReadEntry(archive, entry);
                var pruned = PruneCollision(before, bundleName, records, skipped, out var count);
                if (count == 0) continue;

                // Same measure of "changed" as the installer uses: trailing zeros ignored, because
                // what comes out of an archive carries its sector padding and what was just rebuilt
                // in memory does not.
                if (AdditionsManifest.ComputeSha256(pruned) != AdditionsManifest.ComputeSha256(before))
                    prunedBundles.Add((bundleName, () => new MemoryStream(pruned, writable: false)));
            }
        }

        if (toRemove.Count == 0 && prunedBundles.Count == 0)
        {
            onStep?.Invoke($"removal: nothing left to take out of {archiveRelativePath}");
            return true;
        }

        onStep?.Invoke(
            $"removal: editing {archiveRelativePath} in place - {toRemove.Count} entry/entries out, " +
            $"{prunedBundles.Count} collision bundle(s) pruned (no rebuild)");

        if (toRemove.Count > 0)
        {
            if (ImgArchiveEditor.TryRemove(archivePath, toRemove, out removed) == ImgArchiveEditor.Outcome.NotPossible)
                return false;
        }

        if (prunedBundles.Count > 0 &&
            ImgArchiveEditor.TryReplace(archivePath, prunedBundles) == ImgArchiveEditor.Outcome.NotPossible)
        {
            return false;
        }

        var dead = ImgArchiveEditor.DeadBytes(archivePath) / (1024.0 * 1024.0);
        onStep?.Invoke($"removal: done in place; {dead:0.0} MB of {archiveRelativePath} is now unused space");
        return true;
    }

    /// <summary>
    /// Returns a collision bundle with this mod's records taken out, leaving every other record —
    /// Rockstar's and other mods' alike — byte-for-byte where it was.
    ///
    /// Same rule as everywhere else in uninstall: a record that isn't there any more is reported
    /// rather than treated as an error, because another tool having already removed it is not a
    /// failure worth stopping for.
    ///
    /// Shared with <see cref="AdditionInstaller"/>: when a reinstall folds removal and addition into
    /// one rewrite, the same pruning has to happen there instead, on the way past the bundle.
    /// </summary>
    internal static byte[] PruneCollision(
        byte[] original, string bundleName, IReadOnlyCollection<string> records,
        ICollection<string> skipped, out int pruned)
    {
        pruned = 0;
        var kept = new List<ColRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Copied into a case-insensitive set rather than searched as it arrives: model names are
        // matched case-insensitively everywhere else, and a plain Contains on the caller's
        // collection would quietly become a case-SENSITIVE comparison.
        var wanted = new HashSet<string>(records, StringComparer.OrdinalIgnoreCase);

        foreach (var record in ColBundle.Read(original))
        {
            if (wanted.Contains(record.Name)) { seen.Add(record.Name); pruned++; continue; }
            kept.Add(record);
        }

        foreach (var missing in records.Where(r => !seen.Contains(r)))
            skipped.Add($"collision for '{missing}' was already gone from {bundleName}");

        return ColBundle.Write(kept);
    }

    private static void RemoveGtaDatRegistration(string gameRoot)
    {
        var path = Path.Combine(gameRoot, "data", "gta.dat");
        if (!File.Exists(path)) return;

        var marker = Path.Combine("maps", AdditionInstaller.SaftMapFolder).Replace(Path.DirectorySeparatorChar, '\\');
        var kept = File.ReadAllLines(path)
            .Where(l => !l.Contains(marker, StringComparison.OrdinalIgnoreCase))
            .ToList();

        File.WriteAllLines(path, kept);
    }
}
