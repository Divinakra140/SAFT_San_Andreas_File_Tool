namespace SAFT.Core;

/// <summary>Describes one IMG archive found under a scanned game install.</summary>
public sealed record FoundArchive(string AbsolutePath, string RelativePath);

/// <summary>Locates a San Andreas PC install and its IMG archives without assuming a fixed layout.</summary>
public static class GameScanner
{
    private static readonly string[] KnownExeNames = { "gta_sa.exe", "gta-sa.exe" };

    /// <summary>
    /// True if <paramref name="root"/> looks like a San Andreas PC install: it has one of the
    /// known executable names and a models/gta3.img alongside it.
    /// </summary>
    public static bool LooksLikeSanAndreasInstall(string root)
    {
        if (!Directory.Exists(root)) return false;

        var hasExe = Directory.EnumerateFiles(root)
            .Any(f => KnownExeNames.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase));
        var hasMainArchive = File.Exists(Path.Combine(root, "models", "gta3.img"));

        return hasExe && hasMainArchive;
    }

    /// <summary>
    /// Recursively scans <paramref name="root"/> for *.img files and returns the ones that are
    /// genuine VER2 archives (verified by magic bytes, not just extension).
    /// </summary>
    public static IReadOnlyList<FoundArchive> FindArchives(string root, GameFiles? files = null)
    {
        var results = new List<FoundArchive>();

        foreach (var path in GameFiles.For(root, files).WithExtension(".img"))
        {
            if (!ImgArchive.IsImgArchive(path)) continue;

            var relative = Path.GetRelativePath(root, path);
            results.Add(new FoundArchive(path, relative));
        }

        return results
            .OrderBy(a => a.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
