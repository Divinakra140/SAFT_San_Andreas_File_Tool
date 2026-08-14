using SAFT.Core;

namespace SAFT.Droid;

/// <summary>
/// Proof, rather than inference.
///
/// "It said INSTALL COMPLETE" is not evidence that a file in a 900 MB archive now holds the mod's
/// bytes. This opens the game and looks — for every file the mod would replace, it reads what is
/// actually in the archive right now and compares it against the mod's copy AND against the vanilla
/// original in the backup folder. Each file then comes back as one of three answers, and there is no
/// fourth:
///
///     MOD      — the bytes in your game are the mod's bytes. It is installed.
///     VANILLA  — the bytes match the backed-up original. It is not installed.
///     NEITHER  — something else is in there, which is worth knowing about on its own.
///
/// It writes nothing. It is safe to run at any time, before or after anything, and the counts it
/// prints are the thing to photograph.
/// </summary>
internal static class Verify
{
    public static void Report(string gameFolder, string modFolder, string backupFolder, Action<string> say)
    {
        say($"game:   {gameFolder}");
        say($"mod:    {modFolder}");
        say($"backup: {backupFolder}\n");

        var plan = DirectModInstaller.Plan(gameFolder, modFolder, say);

        var installed = new List<string>();
        var vanilla = new List<string>();
        var neither = new List<string>();
        var unreadable = new List<string>();

        // Grouped by archive so each one is opened once rather than once per entry.
        foreach (var group in plan.Matches.GroupBy(m => m.ArchiveAbsolutePath, StringComparer.OrdinalIgnoreCase))
        {
            ImgArchive? archive = null;
            try
            {
                archive = ImgArchive.Open(group.Key);
                foreach (var match in group)
                {
                    var entry = archive.Entries.FirstOrDefault(e =>
                        e.Name.Equals(match.EntryName, StringComparison.OrdinalIgnoreCase));

                    if (entry is null)
                    {
                        unreadable.Add($"{match.EntryName} (not in {match.ArchiveRelativePath})");
                        continue;
                    }

                    Classify(match.FileName, ReadEntry(archive, entry, match.ModFilePath),
                        match.ModFilePath, backupFolder, installed, vanilla, neither, unreadable);
                }
            }
            catch (Exception ex)
            {
                unreadable.Add($"{Path.GetFileName(group.Key)} ({ex.GetType().Name})");
            }
            finally
            {
                archive?.Dispose();
            }
        }

        // Loose files are simpler: they are just files, so they are compared as files.
        foreach (var match in plan.UnarchivedMatches)
        {
            try
            {
                Classify(match.FileName, File.ReadAllBytes(match.AbsolutePath),
                    match.ModFilePath, backupFolder, installed, vanilla, neither, unreadable);
            }
            catch (Exception ex)
            {
                unreadable.Add($"{match.FileName} ({ex.GetType().Name})");
            }
        }

        say("\n================ REPLACEMENTS ================");
        say($"in your game as the MOD's bytes:     {installed.Count}");
        say($"still the VANILLA original:          {vanilla.Count}");
        say($"neither (changed by something else): {neither.Count}");
        if (unreadable.Count > 0) say($"could not be read:                   {unreadable.Count}");

        List(say, "installed", installed);
        List(say, "still vanilla", vanilla);
        List(say, "neither", neither);
        List(say, "unreadable", unreadable);

        ReportAdditions(gameFolder, backupFolder, say);

        say("\nVERIFY COMPLETE. Nothing was written.");
    }

    /// <summary>
    /// Reads an entry, trimmed to the length of the file it is being compared against.
    ///
    /// Archive entries are stored in whole 2048-byte sectors, so a 3,000-byte model occupies 4,096
    /// bytes and the last 1,096 are padding. Hashing the padded bytes would make every comparison
    /// fail — this has caught SAFT out before. The mod file's own length is the right amount to read.
    /// </summary>
    private static byte[] ReadEntry(ImgArchive archive, ImgEntry entry, string comparedWith)
    {
        var wanted = (int)Math.Min(new FileInfo(comparedWith).Length, entry.SizeSectors * 2048L);
        var buffer = new byte[wanted];

        using var stream = archive.OpenEntry(entry);
        var read = 0;
        while (read < wanted)
        {
            var got = stream.Read(buffer, read, wanted - read);
            if (got <= 0) break;
            read += got;
        }

        return read == wanted ? buffer : buffer[..read];
    }

    private static void Classify(
        string fileName, byte[] inGame, string modFilePath, string backupFolder,
        List<string> installed, List<string> vanilla, List<string> neither, List<string> unreadable)
    {
        try
        {
            var actual = AdditionsManifest.ComputeSha256(inGame);

            if (actual == AdditionsManifest.ComputeSha256(modFilePath))
            {
                installed.Add(fileName);
                return;
            }

            // The backup folder mirrors the game's own layout, so the original could be at any depth
            // under it. Matching on name is enough here - these names came from the game.
            var original = FindInBackup(backupFolder, fileName);
            if (original is not null && actual == AdditionsManifest.ComputeSha256(original))
            {
                vanilla.Add(fileName);
                return;
            }

            neither.Add(original is null ? $"{fileName} (no original backed up to compare)" : fileName);
        }
        catch (Exception ex)
        {
            unreadable.Add($"{fileName} ({ex.GetType().Name})");
        }
    }

    private static string? FindInBackup(string backupFolder, string fileName)
    {
        try
        {
            return Directory.EnumerateFiles(backupFolder, fileName, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The other half: objects SAFT ADDED have no vanilla counterpart, so the only record of them is
    /// the manifest. Each recorded entry is looked up in the archive it claims to be in, and its
    /// bytes checked against the hash written down at install time.
    /// </summary>
    private static void ReportAdditions(string gameFolder, string backupFolder, Action<string> say)
    {
        var manifest = AdditionsManifest.Load(backupFolder);

        say("\n================= ADDITIONS =================");
        if (manifest is null || manifest.Mods.Count == 0)
        {
            say("no record of anything added through this backup folder");
            return;
        }

        foreach (var mod in manifest.Mods)
        {
            say($"\n'{mod.Name}' — {mod.ArchiveEntries.Count} asset(s), {mod.ObjectIds.Count} object id(s)");

            var present = 0;
            var changed = 0;
            var missing = 0;

            foreach (var group in mod.ArchiveEntries.GroupBy(e => e.ArchiveRelativePath, StringComparer.OrdinalIgnoreCase))
            {
                var archivePath = Path.Combine(gameFolder, group.Key);
                ImgArchive? archive = null;
                try
                {
                    archive = ImgArchive.Open(archivePath);
                    foreach (var recorded in group)
                    {
                        var entry = archive.Entries.FirstOrDefault(e =>
                            e.Name.Equals(recorded.EntryName, StringComparison.OrdinalIgnoreCase));

                        if (entry is null) { missing++; continue; }

                        var bytes = new byte[entry.SizeSectors * 2048L];
                        using var stream = archive.OpenEntry(entry);
                        var read = stream.Read(bytes, 0, bytes.Length);

                        // Compared at every plausible length rather than a guessed one: the recorded
                        // hash was taken of the unpadded bytes, and the entry does not say how many
                        // of its bytes are real.
                        if (MatchesAtSomeLength(bytes, read, recorded.Sha256)) present++;
                        else changed++;
                    }
                }
                catch (Exception ex)
                {
                    say($"   could not read {group.Key}: {ex.GetType().Name}");
                }
                finally
                {
                    archive?.Dispose();
                }
            }

            say($"   in your game and unchanged: {present}");
            if (changed > 0) say($"   present but CHANGED since:  {changed}");
            if (missing > 0) say($"   MISSING from the archive:   {missing}");
        }
    }

    /// <summary>
    /// True if the recorded hash matches the entry's bytes at any length that is not padding.
    ///
    /// The trailing bytes of a sector-aligned entry are zeros, so the real content ends at the last
    /// non-zero byte — give or take a file that genuinely ends in zeros, which is why this walks back
    /// from there rather than assuming.
    /// </summary>
    private static bool MatchesAtSomeLength(byte[] bytes, int read, string expected)
    {
        var end = read;
        while (end > 0 && bytes[end - 1] == 0) end--;

        // The exact end, then each sector boundary up to the full padded length: four comparisons at
        // most, against a hash that either matches or does not.
        for (var length = end; length <= read; length = length == end ? NextSector(end) : length + 2048)
        {
            if (AdditionsManifest.ComputeSha256(bytes[..length]) == expected) return true;
            if (length == read) break;
        }

        return false;
    }

    private static int NextSector(int length) => (length + 2047) / 2048 * 2048;

    private static void List(Action<string> say, string what, IReadOnlyList<string> items)
    {
        if (items.Count == 0) return;

        say($"\n{what}:");
        foreach (var item in items.Take(12)) say($"   {item}");
        if (items.Count > 12) say($"   ...and {items.Count - 12} more");
    }
}
