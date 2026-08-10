namespace SAFT.Core;

/// <summary>What removing a mod's additions actually did, including anything deliberately left alone.</summary>
public sealed record AdditionRemovalResult(
    IReadOnlyList<string> RemovedMods,
    IReadOnlyList<string> Skipped,
    int ArchiveEntriesRemoved,
    int DataLinesRemoved,
    IReadOnlyList<int> FreedObjectIds);

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
    public static AdditionRemovalResult Remove(
        string gameRoot,
        AdditionsManifest manifest,
        IEnumerable<string> modNames,
        IProgress<AdditionProgress>? progress = null)
    {
        var wanted = new HashSet<string>(modNames, StringComparer.OrdinalIgnoreCase);
        var mods = manifest.Mods.Where(m => wanted.Contains(m.Name)).ToList();

        var removedMods = new List<string>();
        var skipped = new List<string>();
        var freedIds = new List<int>();
        var linesRemoved = 0;

        // Gathered across every mod so the archive is rebuilt once rather than once per mod.
        var entriesToRemove = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // Collision records to prune, keyed by the bundle holding them. These live INSIDE an archive
        // entry rather than being entries themselves, so they ride along with the same rebuild.
        var collisionToRemove = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods)
        {
            foreach (var entry in mod.ArchiveEntries)
            {
                if (!VerifyArchiveEntry(gameRoot, entry, out var reason))
                {
                    skipped.Add($"{entry.EntryName}: {reason}");
                    continue;
                }

                if (!entriesToRemove.TryGetValue(entry.ArchiveRelativePath, out var set))
                    entriesToRemove[entry.ArchiveRelativePath] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(entry.EntryName);
            }

            foreach (var added in mod.Collisions)
            {
                if (!collisionToRemove.TryGetValue(added.BundleName, out var set))
                    collisionToRemove[added.BundleName] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(added.ModelName);
            }

            linesRemoved += RemoveDataLines(gameRoot, mod, skipped);
            freedIds.AddRange(mod.ObjectIds);
            removedMods.Add(mod.Name);
        }

        // A mod that added only collision still needs its archive rebuilt, so the set of archives to
        // touch is the union of both kinds of removal rather than just the entry removals.
        var archives = new HashSet<string>(entriesToRemove.Keys, StringComparer.OrdinalIgnoreCase);
        if (collisionToRemove.Count > 0) archives.Add(AdditionInstaller.DefaultArchiveRelativePath);

        var entriesRemoved = 0;
        foreach (var archiveRelativePath in archives)
        {
            entriesToRemove.TryGetValue(archiveRelativePath, out var names);
            entriesRemoved += RebuildArchiveWithout(
                gameRoot, archiveRelativePath,
                names ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                collisionToRemove, skipped, progress);
        }

        foreach (var mod in mods) manifest.Mods.Remove(mod);

        // The gta.dat registration is shared by every addition, so it only comes out once nothing is
        // left that depends on it — removing it early would stop other mods' objects loading.
        if (manifest.Mods.Count == 0) RemoveGtaDatRegistration(gameRoot);

        return new AdditionRemovalResult(removedMods, skipped, entriesRemoved, linesRemoved, freedIds);
    }

    /// <summary>
    /// Confirms an archive entry is still byte-for-byte what SAFT put there. If the user has since
    /// replaced it — installed another mod over the top, or edited it — deleting it would destroy
    /// work SAFT never did, so it's left in place and reported instead.
    /// </summary>
    private static bool VerifyArchiveEntry(string gameRoot, AddedArchiveEntry entry, out string reason)
    {
        var archivePath = Path.Combine(gameRoot, entry.ArchiveRelativePath);
        if (!File.Exists(archivePath))
        {
            reason = "the archive it was added to no longer exists";
            return false;
        }

        try
        {
            using var archive = ImgArchive.Open(archivePath);
            var found = archive.Entries.FirstOrDefault(
                e => e.Name.Equals(entry.EntryName, StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                reason = "it is no longer in the archive";
                return false;
            }

            using var stream = archive.OpenEntry(found);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);

            // ComputeSha256 ignores trailing zeros on both sides, so the sector padding an archive
            // adds doesn't make an untouched asset look modified.
            if (AdditionsManifest.ComputeSha256(buffer.ToArray()) != entry.Sha256)
            {
                reason = "it has been changed since SAFT added it, so it was left in place";
                return false;
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
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
        IProgress<AdditionProgress>? progress)
    {
        var archivePath = Path.Combine(gameRoot, archiveRelativePath);
        if (!File.Exists(archivePath)) return 0;

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
                    var pruned = PruneCollision(archive, entry, records, skipped, ref prunedRecords);
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
                (done, total) => progress?.Report(new AdditionProgress("Reading the existing archive", done, total)));
        }

        FileReplace.MoveOver(rebuilt, archivePath);
        return removed;
    }

    /// <summary>
    /// Returns a collision bundle with this mod's records taken out, leaving every other record —
    /// Rockstar's and other mods' alike — byte-for-byte where it was.
    ///
    /// Same rule as everywhere else in uninstall: a record that isn't there any more is reported
    /// rather than treated as an error, because another tool having already removed it is not a
    /// failure worth stopping for.
    /// </summary>
    private static byte[] PruneCollision(
        ImgArchive archive, ImgEntry entry, ISet<string> records, List<string> skipped, ref int pruned)
    {
        byte[] original;
        using (var stream = archive.OpenEntry(entry))
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            original = buffer.ToArray();
        }

        var kept = new List<ColRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in ColBundle.Read(original))
        {
            if (records.Contains(record.Name)) { seen.Add(record.Name); pruned++; continue; }
            kept.Add(record);
        }

        foreach (var missing in records.Where(r => !seen.Contains(r)))
            skipped.Add($"collision for '{missing}' was already gone from {entry.Name}");

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
