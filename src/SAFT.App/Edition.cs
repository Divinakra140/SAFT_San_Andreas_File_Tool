namespace SAFT.App;

/// <summary>
/// Which of the two SAFT builds this is.
///
/// There is one codebase and one source tree; the two exes differ by a single compile-time constant
/// set at publish. That is deliberate rather than lazy: nearly every bug found during testing showed
/// up on exactly one platform, and two diverging projects would have meant fixing each of them twice
/// and eventually only remembering once.
///
/// SAFT (the default): Install Mods and Uninstall Mods. Everything in it finishes in seconds, which
/// is what makes it honest to recommend for Winlator.
///
/// SAFT Dev (built with MODDEV): adds Extract Game Files, Install Mod(s) into Extracted and
/// Rebuild from Extracted. Those three are the only route to some things - streamed scripts, or
/// rebuilding a game from scratch - but they write over 20,000 individual files, which takes half an
/// hour on a desktop and is pathological on Android: exFAT has no directory index, so every file
/// created in a folder that already holds 12,000 of them is a linear scan, and Winlator does not
/// cache its way out of that the way Windows and macOS do. Measured, on the same SD card: steady on
/// NTFS, steady on macOS, quadratic under Winlator.
/// </summary>
internal static class Edition
{
#if MODDEV
    public const bool IncludesModDeveloperTabs = true;
    public const string Name = "SAFT Dev";
    public const string Tagline = "San Andreas File Tool - Developer Edition";
#else
    public const bool IncludesModDeveloperTabs = false;
    public const string Name = "SAFT";
    public const string Tagline = "San Andreas File Tool";
#endif
}
