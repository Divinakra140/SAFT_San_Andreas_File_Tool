namespace SAFT.Core;

/// <summary>One unarchived game file: a real file sitting in the game folder, not an entry inside an IMG archive.</summary>
public sealed record UnarchivedFile(string AbsolutePath, string RelativePath);

/// <summary>
/// Indexes the files that live loose in a game folder rather than inside an IMG archive — the
/// map placement data (.ipl/.ide), path nodes, handling/data tables, loose textures and so on.
///
/// These are just as replaceable as archive entries and just as much a part of a mod, but they
/// need matching against the filesystem instead of an archive directory table. The audio
/// packages/streams and the archives themselves are excluded, since those have their own
/// dedicated (and far more careful) replacement paths.
/// </summary>
public static class UnarchivedIndex
{
    /// <summary>
    /// Maps file name -> every unarchived game file with that name. A list rather than a single
    /// entry because San Andreas genuinely ships duplicate names in different folders (the
    /// data/Decision/*.ped files also appear under data/Decision/Allowed/).
    /// </summary>
    public static Dictionary<string, List<UnarchivedFile>> Build(string gameRoot)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in GameScanner.FindArchives(gameRoot)) excluded.Add(archive.AbsolutePath);
        foreach (var pkg in SfxIndex.Load(gameRoot)) excluded.Add(pkg.AbsolutePath);
        foreach (var station in StreamIndex.Load(gameRoot)) excluded.Add(station.AbsolutePath);

        var index = new Dictionary<string, List<UnarchivedFile>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(gameRoot, "*", SearchOption.AllDirectories))
        {
            if (excluded.Contains(path)) continue;

            var fileName = Path.GetFileName(path);
            if (!FileFilters.IsReplaceableUnarchivedFile(fileName)) continue;

            if (!index.TryGetValue(fileName, out var list))
            {
                list = new List<UnarchivedFile>();
                index[fileName] = list;
            }
            list.Add(new UnarchivedFile(path, Path.GetRelativePath(gameRoot, path)));
        }
        return index;
    }

    /// <summary>
    /// The same index over an <em>extracted</em> install rather than a live game folder. Here the
    /// unarchived files are the ones the extractor mirrored at their original relative paths;
    /// everything unpacked out of an archive lives under that archive's own relative path instead
    /// (models/gta3.img/dff/…), so excluding those directories is what separates the two.
    /// </summary>
    public static Dictionary<string, List<UnarchivedFile>> BuildForExtraction(
        string extractionRoot, IEnumerable<string> archiveRelativePaths)
    {
        var archiveDirs = archiveRelativePaths
            .Select(rel => Path.GetFullPath(Path.Combine(extractionRoot, rel)) + Path.DirectorySeparatorChar)
            .ToList();

        var index = new Dictionary<string, List<UnarchivedFile>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(extractionRoot, "*", SearchOption.AllDirectories))
        {
            var full = Path.GetFullPath(path);
            if (archiveDirs.Any(dir => full.StartsWith(dir, StringComparison.OrdinalIgnoreCase))) continue;

            var fileName = Path.GetFileName(path);
            if (!FileFilters.IsReplaceableUnarchivedFile(fileName)) continue;

            if (!index.TryGetValue(fileName, out var list))
            {
                list = new List<UnarchivedFile>();
                index[fileName] = list;
            }
            list.Add(new UnarchivedFile(path, Path.GetRelativePath(extractionRoot, path)));
        }
        return index;
    }

    /// <summary>Whether two plain files hold the same content, ignoring trailing zero padding — the extracted-install form of <see cref="ContentMatches(Stream, long, string)"/>.</summary>
    public static bool ContentMatches(string firstPath, string secondPath)
    {
        var first = new FileInfo(firstPath);
        var second = new FileInfo(secondPath);
        if (!first.Exists || !second.Exists) return false;
        if (first.Length > MaxComparableLength || second.Length > MaxComparableLength) return false;

        return TrimTrailingZeros(File.ReadAllBytes(firstPath))
            .SequenceEqual(TrimTrailingZeros(File.ReadAllBytes(secondPath)));
    }

    /// <summary>
    /// Anything past this size isn't byte-compared when deciding whether an archived and an
    /// unarchived copy of the same name hold the same content — it just reports "different", which
    /// makes SAFT ask rather than assume. Real dual-location files in San Andreas are small
    /// (the largest, a path-node table, is ~140KB), so this only ever trips on something unusual,
    /// and erring toward "ask" is the safe direction.
    /// </summary>
    private const long MaxComparableLength = 8 * 1024 * 1024;

    /// <summary>
    /// Whether an archive entry and an unarchived file that share a name actually hold the same
    /// content. This is the crux of the dual-location problem: San Andreas ships 64 path-node
    /// tables (nodes0.dat … nodes63.dat) byte-for-byte identically in both data/Paths/ and inside
    /// gta3.img, where replacing both is not just safe but necessary — but it also ships
    /// arrow.dff and hoop.dff as two genuinely different live models that merely share a name
    /// (models/generic/ loads by hardcoded path, gta3.img loads by object ID 1318/1316). Comparing
    /// the bytes is what tells those two cases apart, so a mod file is never applied to an asset
    /// the user didn't mean.
    ///
    /// Trailing zero padding is ignored on both sides: archive entries are stored sector-aligned,
    /// so the archived copy is always zero-padded up to a 2048-byte boundary.
    /// </summary>
    public static bool ContentMatches(Stream archiveEntry, long entryLength, string unarchivedPath)
    {
        var unarchivedLength = new FileInfo(unarchivedPath).Length;
        if (entryLength > MaxComparableLength || unarchivedLength > MaxComparableLength) return false;

        var archived = new byte[entryLength];
        archiveEntry.ReadExactly(archived);
        var unarchived = File.ReadAllBytes(unarchivedPath);

        return TrimTrailingZeros(archived).SequenceEqual(TrimTrailingZeros(unarchived));
    }

    private static ReadOnlySpan<byte> TrimTrailingZeros(byte[] data)
    {
        var end = data.Length;
        while (end > 0 && data[end - 1] == 0) end--;
        return data.AsSpan(0, end);
    }
}
