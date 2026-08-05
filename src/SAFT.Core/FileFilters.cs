namespace SAFT.Core;

/// <summary>
/// Files that should never be treated as real archive content: filesystem/OS clutter that
/// can end up inside an extracted or mod-source folder without the user ever putting it
/// there (AppleDouble sidecars from browsing an exFAT/network share on macOS, Explorer
/// artifacts on Windows), plus mod-package cruft like readmes that people never bother
/// stripping out by hand.
/// </summary>
public static class FileFilters
{
    public static bool IsIgnoredFile(string fileName)
    {
        if (fileName.Equals(SaftManifest.FileName, StringComparison.OrdinalIgnoreCase)) return true;
        if (fileName.StartsWith("._", StringComparison.Ordinal)) return true;
        if (fileName.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)) return true;
        if (fileName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)) return true;
        if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return true;
        if (fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// The last N path segments of a file (e.g. grandparent/parent/filename) — matches how
    /// community audio-editing tools already lay out extracted files
    /// ("GENRL/Bank_137/sound_001.wav", "AA/Track_001.ogg"), regardless of what folder structure a
    /// mod pack wraps around that. Shared by both mod-install paths (direct and extraction-based),
    /// since audio matching works identically either way.
    /// </summary>
    public static string? GetLastPathSegments(string sourcePath, int count)
    {
        var parts = new List<string> { Path.GetFileName(sourcePath) };
        var dir = Path.GetDirectoryName(sourcePath);
        for (var i = 1; i < count; i++)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name)) return null;
            parts.Insert(0, name);
            dir = Path.GetDirectoryName(dir);
        }
        return string.Join("/", parts);
    }
}
