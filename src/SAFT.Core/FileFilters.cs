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
    /// Extensions never eligible for unarchived (loose-in-the-game-folder) replacement, no matter
    /// what a mod folder contains. Executables and native libraries are deliberately out of scope —
    /// SAFT replaces game assets, not game binaries, and silently swapping a .exe/.dll is both a far
    /// bigger blast radius than any asset and squarely the modloader/CLEO territory SAFT exists to
    /// avoid. ".img" is excluded because wholesale archive replacement would bypass the entire
    /// patch/rebuild path that is the point of this tool.
    ///
    /// ".scm" (data/script/main.scm) is excluded for a different and more important reason: every
    /// other file SAFT touches is a leaf asset, so restoring the original fully undoes the change.
    /// The game script isn't — San Andreas save files embed the script's global variable block, and
    /// main.scm defines that layout. Any save written while a modded script was active keeps that
    /// state, and those saves live outside the game folder (Documents\GTA San Andreas User Files),
    /// where SAFT's backups never reach. Restoring main.scm would not un-break them. SAFT only
    /// installs what it can genuinely uninstall, so it doesn't install this at all.
    /// </summary>
    private static readonly string[] UnarchivedReplacementBlocklist = { ".exe", ".dll", ".asi", ".img", ".scm" };

    /// <summary>
    /// Whether a file sitting loose in the game folder may be replaced by a same-named mod file.
    /// Deliberately a blocklist, not an allowlist: San Andreas scatters a long tail of data formats
    /// (.ipl, .ide, .dat, .ped, .grp, .zon, .cfg, .gxt, extensionless files…) through its folders,
    /// and enumerating them all would guarantee missing some legitimate one.
    /// </summary>
    public static bool IsReplaceableUnarchivedFile(string fileName)
    {
        if (IsIgnoredFile(fileName)) return false;
        foreach (var blocked in UnarchivedReplacementBlocklist)
            if (fileName.EndsWith(blocked, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>
    /// A compiled game script — data/script/main.scm, or one of the streamed scripts inside
    /// script.img. SAFT never installs these, anywhere, by any route.
    ///
    /// This isn't caution about the file itself, it's about what outlives it. San Andreas save
    /// files hold a table of streamed-script records referencing scripts BY INDEX (verified
    /// directly in a real save: 25 records at 20 bytes each, named DANCER, PCHAIR, OTBWTCH,
    /// PEDROUL … matching script.img's own entries), and main.scm defines the global variable
    /// layout saves are written against. Replace either and an existing save can hold live
    /// references into bytecode that no longer matches it. Saves live in the user's Documents
    /// folder, outside the game directory, so no backup SAFT makes can undo that — which breaks
    /// the one rule the whole tool rests on: everything SAFT installs, SAFT can uninstall.
    ///
    /// They're also the easiest possible thing to replace by hand (main.scm is a single file at a
    /// fixed path), so refusing costs the user almost nothing.
    /// </summary>
    public static bool IsGameScriptFile(string fileName) =>
        fileName.EndsWith(".scm", StringComparison.OrdinalIgnoreCase);

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
