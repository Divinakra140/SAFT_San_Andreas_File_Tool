namespace SAFT.Core;

/// <summary>
/// Whether a backup folder's record was written for the game folder it is about to be used against.
///
/// The game folder and the backup folder are chosen separately, and the record that says what SAFT
/// ADDED lives in the backup folder. Pair one game's backup folder with a different game and the
/// uninstall reads a record listing assets that game never had: it finds nothing to remove, while the
/// game those assets are actually in keeps them and is never told. That is the most likely
/// explanation for seven assets going missing from a record on 2026-08-13 - two copies of San Andreas
/// were in play that night, one internal and one on an SD card.
///
/// It is a WARNING and never a refusal. Pointing SAFT at a second copy of the same game is a
/// perfectly ordinary thing to do, and the check cannot tell that apart from a mistake with any
/// certainty - so it says what it noticed and lets the user decide.
/// </summary>
public static class GameFolderCheck
{
    /// <summary>
    /// True if the record names a game folder that looks like a DIFFERENT game from this one.
    ///
    /// Only the last path segment is compared, deliberately. The same install is reached by genuinely
    /// different paths depending on where SAFT is running - "E:\San Andreas\...\Grand Theft Auto San
    /// Andreas" on Windows and "/storage/emulated/0/Download/GTA SA/..." on Android are routinely the
    /// same folder over the same cable - so comparing full paths would warn constantly for people who
    /// mod from both, which trains them to click through the one warning that matters.
    ///
    /// The cost of comparing only the leaf is that two copies in different parents with the same
    /// folder name read as the same game. That direction is the right one to fail in: a missed
    /// warning leaves the user exactly where they are today, while a false one every single run makes
    /// the feature worse than not having it.
    ///
    /// Answers false whenever it cannot tell — no record, no recorded path, an unusable path.
    /// </summary>
    public static bool LooksLikeADifferentGame(string? recordedGameRoot, string? gameFolder)
    {
        var recorded = LastSegment(recordedGameRoot);
        var current = LastSegment(gameFolder);

        if (recorded is null || current is null) return false;

        return !recorded.Equals(current, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The final folder name, from a path written on either platform. Both separators are handled
    /// whatever SAFT is running on, because the record travels between them.
    /// </summary>
    private static string? LastSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var trimmed = path.TrimEnd('/', '\\', ' ');
        if (trimmed.Length == 0) return null;

        var cut = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        var segment = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;

        return segment.Length == 0 ? null : segment;
    }

    /// <summary>What to put in front of the user. Short enough for a 960x544 handheld screen.</summary>
    public static string Warning(string? recordedGameRoot) =>
        $"This backup folder's record was written for a different game folder:\n\n{recordedGameRoot}\n\n" +
        "If that is another copy of the game, uninstalling here will not remove anything from it. " +
        "Carry on only if you meant to use this folder.";
}
