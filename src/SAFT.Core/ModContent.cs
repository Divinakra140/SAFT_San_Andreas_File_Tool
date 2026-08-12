namespace SAFT.Core;

/// <summary>
/// What kind of thing a mod folder actually contains — asked before any of the expensive analysis,
/// so SAFT only does the work this particular mod requires.
///
/// This exists because SAFT read the entire San Andreas map on every single install regardless of
/// what was being installed. A pack of 45 replacement sound effects and 12 radio tracks — which
/// cannot move a single object on the map, change a single model's weight, or add a single object
/// ID — still caused all 60 .ide files, all 54 text .ipl files and every binary .ipl inside the
/// archives to be parsed, 14,823 models to be weighed, and 50,982 placements to be sorted into
/// cells. Every one of the audio installs that died on a real device died inside that work, before
/// a single byte had been written.
///
/// The answer that work produces for a sound-only mod is not merely expensive, it is known in
/// advance: no placements, no definitions, no changed model or texture sizes, therefore no density
/// figure and no streaming impact. Computing it was never anything but waste.
/// </summary>
public static class ModContent
{
    /// <summary>
    /// The extensions that can affect what the game has to stream, or how many object slots are
    /// used. Models and textures carry the weight; collision and animation ride along with them as
    /// additions; .ide and .ipl define and place things.
    ///
    /// Deliberately a superset of <see cref="AdditionScanner"/>'s own asset list — a file type that
    /// counts as a possible addition must count here too, or the addition scan would be skipped for
    /// a mod that genuinely has additions.
    /// </summary>
    private static readonly string[] StreamingRelevantExtensions =
        { ".dff", ".txd", ".col", ".ifp", ".ide", ".ipl" };

    /// <summary>
    /// Whether anything in this mod folder could change the map, the streaming weight, or the object
    /// ID allocation. False for a pure audio pack, and for anything else made only of file types the
    /// map analysis has no opinion about.
    ///
    /// Errs towards true: an unreadable folder gets the full analysis rather than silently skipping
    /// checks the user is relying on.
    /// </summary>
    public static bool AffectsStreaming(string modSourceFolder, GameFiles? modFiles = null)
    {
        try
        {
            // Checked explicitly, because the shared listing reports a folder it cannot read as
            // EMPTY rather than throwing - every other consumer of it wants that, and this one must
            // not, since "no streaming-relevant files" and "could not look" are opposite answers
            // here. Reading an unreadable folder as empty would skip the map analysis, the object-slot
            // check and the streaming warning on a mod nobody has been able to look inside.
            if (!Directory.Exists(modSourceFolder)) return true;

            foreach (var path in GameFiles.For(modSourceFolder, modFiles).Paths)
            {
                var fileName = Path.GetFileName(path);
                if (FileFilters.IsIgnoredFile(fileName)) continue;
                if (HasStreamingRelevantExtension(fileName)) return true;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    public static bool HasStreamingRelevantExtension(string fileName) =>
        StreamingRelevantExtensions.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));
}
