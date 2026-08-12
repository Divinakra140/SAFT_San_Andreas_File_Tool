namespace SAFT.Core;

/// <summary>
/// Every file in a folder, listed once — a game folder, or a mod folder.
///
/// It began as the game folder's listing and now serves the mod folder too, because the mod folder
/// had exactly the same disease: <see cref="DirectModInstaller.Plan"/> walked it,
/// <see cref="ModContent.AffectsStreaming"/> walked it, <see cref="AdditionScanner.Scan"/> walked it,
/// the mod's own files were weighed by walking it, and a reinstall walked it a fifth time. SAFT HUNG
/// in that fifth walk on a real device, with the app alive and the window refusing to close - the
/// third hang now to happen inside a recursive directory enumeration.
///
/// SAFT walked the game folder recursively at least eight times per install: the archive search did
/// it (three times on its own, once per caller), the loose-file index did it, the asset weighing did
/// it, the .ipl search did it, the .ide search did it, and the known-names list did it. Each walk
/// asked the filesystem the same question about the same unchanged folder and threw the answer away.
///
/// On a desktop that is invisible - a 430 file tree enumerates in milliseconds. Under Winlator it is
/// not: every enumeration goes through Wine's filesystem translation onto Android's storage stack and
/// out to an SD card. Two separate installs have now stopped dead inside one of these walks -
/// <see cref="PlacementDensity.WeighGameAssets"/> once and <see cref="UnarchivedIndex.Build"/> once -
/// while the app itself stayed alive and responsive. Nothing was wrong with either function; they
/// were waiting on the filesystem.
///
/// The walk cannot be made reliable from inside SAFT. What it can do is stop asking eight times for
/// an answer that cannot have changed, which cuts the exposure by the same factor.
///
/// Deliberately a plain snapshot passed down by callers rather than a hidden static cache: the point
/// at which the game folder is read should be visible in the code, not a side effect of who happened
/// to call what first.
/// </summary>
public sealed class GameFiles
{
    private GameFiles(string root, IReadOnlyList<string> paths)
    {
        Root = root;
        Paths = paths;
    }

    /// <summary>The game folder this listing came from.</summary>
    public string Root { get; }

    /// <summary>Absolute paths of every file under <see cref="Root"/>, at any depth.</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>
    /// How long one folder may take before the log names it.
    ///
    /// A whole second, not the tenth of a second a folder normally takes. 250 ms was the first guess
    /// and it was too eager: a single folder on a plain SD card crosses it regularly, which would put
    /// lines in front of users about something entirely normal. This is meant to catch the pathological
    /// case — the walks SAFT has HUNG inside ran for minutes — not ordinary slow storage.
    /// </summary>
    private const long SlowFolderMs = 1000;

    /// <summary>
    /// Walks the folder once. An unreadable folder yields an empty listing rather than throwing -
    /// every caller here previously tolerated a folder it could not read, and this must not turn
    /// that into a crash.
    ///
    /// Walked one folder at a time rather than with a single recursive enumeration, so that a stall
    /// inside it can be attributed. This walk is where SAFT has twice stopped dead; a recursive
    /// EnumerateFiles(AllDirectories) is one opaque call, so all either of those left in the log was
    /// "listing the game folder" and no way to tell which folder it was in.
    ///
    /// It reports only when there is something to report: the totals at the end, and any single
    /// folder that took longer than <see cref="SlowFolderMs"/>. A line per folder was what found this
    /// during development, and it is two lines away if it is ever needed again — but a release build
    /// should not fill a user's log with 35 lines of nothing every time it checks a mod.
    /// </summary>
    public static GameFiles Walk(string root, Action<string>? onStep = null)
    {
        // Named, because this now lists the mod folder as well as the game folder and two lines
        // reading "listing the game folder" in a row is exactly the kind of thing that wastes an hour
        // when the log is all anyone has to go on.
        onStep?.Invoke($"files: listing {Path.GetFileName(Path.TrimEndingDirectorySeparator(root))}");
        var paths = new List<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var folders = 0;
        var slowest = 0L;
        var slowestName = string.Empty;

        // Breadth-first, sorted, so the order files come back in is the same on every run and on
        // every platform - several callers take the first match of a name and must not start
        // choosing differently.
        var pending = new Queue<string>();
        pending.Enqueue(root);

        while (pending.Count > 0)
        {
            var folder = pending.Dequeue();
            var name = Relative(root, folder);
            var startedAt = clock.ElapsedMilliseconds;

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(folder).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    pending.Enqueue(sub);

                // Filesystem junk is dropped here, once, rather than left for each consumer to remember.
                // Copying a game folder to an SD card from a Mac leaves an AppleDouble "._name" beside
                // every file AND every directory, and those carry the real file's extension: "._card.dff"
                // ends with .dff, so the asset weighing counted them as loose models and sized the game
                // from them. Windows leaves Thumbs.db and desktop.ini the same way.
                //
                // Only ever junk — see FileFilters.IsFilesystemJunk, which is deliberately narrower than
                // the mod-folder filter precisely so it is safe to apply to the game's own files.
                foreach (var path in Directory.EnumerateFiles(folder).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    if (!FileFilters.IsFilesystemJunk(Path.GetFileName(path)))
                        paths.Add(path);
            }
            catch
            {
                // Missing or unreadable folder. Skipping just this one is a change for the better:
                // the single recursive call used to abandon the WHOLE listing when any folder in it
                // could not be read, so one bad subfolder made the game look empty.
                onStep?.Invoke($"files: could not read {name}, skipped");
                continue;
            }

            folders++;
            var took = clock.ElapsedMilliseconds - startedAt;
            if (took > slowest) { slowest = took; slowestName = name; }
            if (took >= SlowFolderMs) onStep?.Invoke($"files: {name} took {took:N0} ms");
        }

        onStep?.Invoke(
            $"files: {paths.Count:N0} file(s) across {folders:N0} folder(s) in {clock.ElapsedMilliseconds:N0} ms" +
            (slowest >= SlowFolderMs ? $" (slowest {slowestName} at {slowest:N0} ms)" : string.Empty));
        return new GameFiles(root, paths);
    }

    /// <summary>The folder's path relative to the game root, for the log. The root itself reads as ".".</summary>
    private static string Relative(string root, string folder)
    {
        try
        {
            var relative = Path.GetRelativePath(root, folder);
            return string.IsNullOrEmpty(relative) ? "." : relative;
        }
        catch { return folder; }
    }

    /// <summary>
    /// The listing for <paramref name="root"/>, walking it only if one was not already supplied.
    /// Lets every consumer take an optional listing and stay correct when called standalone.
    /// </summary>
    public static GameFiles For(string root, GameFiles? existing, Action<string>? onStep = null) =>
        existing is not null && PathsMatch(existing.Root, root) ? existing : Walk(root, onStep);

    /// <summary>Paths whose file name ends with <paramref name="extension"/> (".ipl", ".img", ...).</summary>
    public IEnumerable<string> WithExtension(string extension) =>
        Paths.Where(p => p.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static bool PathsMatch(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
