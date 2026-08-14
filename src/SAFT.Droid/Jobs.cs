using SAFT.Core;

namespace SAFT.Droid;

/// <summary>
/// Uninstalling, which is the other half of what the shipping build does.
///
/// The install lives next door in <see cref="InstallRunner"/>. Between them they are SAFT.exe's two
/// tabs — not SAFT-Dev.exe's five. Extract, install-into-extracted and rebuild are developer tabs
/// and stay on the desktop; the Android port ships what the released Windows build ships, plus room
/// for Player Skin Mods and Limit Adjustment when 2.2 adds them.
///
/// Everything here runs on a background thread and reports through <c>say</c>. None of it knows it
/// is on Android.
/// </summary>
internal static class Jobs
{
    /// <summary>
    /// Puts the game back as it was: originals restored over the top of whatever replaced them, and
    /// added objects taken out.
    ///
    /// The order is not free. Added objects come out FIRST, because their removal rewrites the
    /// archive — doing it after the ordinary restores would rebuild the same archive twice for one
    /// job. This is the same fold the install uses, from the other end.
    ///
    /// Additions have no vanilla counterpart, so nothing about them sits in the backup folder as a
    /// file. The record SAFT wrote at install time is their only trace, which is exactly why it
    /// lives in the backup folder alongside the originals.
    /// </summary>
    /// <param name="modBackupFolder">
    /// Where to keep a copy of the mod files being removed, or null to not keep them. The caller
    /// picks the folder rather than SAFT deriving one, and that is the point: two mods' removed
    /// files landing in the same folder would overwrite each other by name, leaving a folder that
    /// claims to be one mod and is really two. What comes out of a game has to stay separable, or
    /// putting it back is guesswork.
    /// </param>
    /// <param name="warn">
    /// Puts a warning in front of the user rather than in the log. Used for the one thing they need
    /// to act on: an SD card that slowed down while being written, which can stall the game's first
    /// load if they go straight back to playing.
    /// </param>
    /// <param name="ask">
    /// Puts a question in front of the user and waits for the answer. Used for the one thing worth
    /// stopping for: a backup folder whose record was written for a different game folder.
    /// </param>
    public static void Uninstall(
        string gameFolder, string backupFolder, string? modBackupFolder, Action<string> say,
        Action<string, string>? warn = null,
        Func<string, string, StreamingSeverity?, bool>? ask = null)
    {
        // Before anything is read or written: does this backup folder's record belong to this game?
        //
        // The two folders are picked separately, so one game's backup folder can be pointed at
        // another game. The uninstall then finds nothing of the record to remove while the game those
        // assets are really in keeps them. A warning rather than a refusal - a second copy of the
        // same game is an ordinary thing to have, and SAFT cannot tell that apart from a slip.
        var recordedRoot = AdditionsManifest.Load(backupFolder)?.GameRootPath;
        if (GameFolderCheck.LooksLikeADifferentGame(recordedRoot, gameFolder))
        {
            say($"note: this backup folder's record was written for {recordedRoot}");
            if (ask is not null
                && !ask("Different game folder", GameFolderCheck.Warning(recordedRoot), StreamingSeverity.Caution))
            {
                say("\nUNINSTALL CANCELLED - nothing was written.");
                return;
            }
        }

        say($"restoring {gameFolder}\n     from {backupFolder}");

        // Same safety net as the install path: an archive left half-edited by an interrupted run is
        // put back before this one writes to it.
        var recovered = ImgArchiveEditor.RecoverAll(gameFolder, null, say);
        if (recovered.Count > 0)
            say($"recovered {recovered.Count} archive(s) from an interrupted edit before starting");
        say(modBackupFolder is null
            ? "the mod files being removed are not being kept"
            : $"the mod files being removed will be kept in {modBackupFolder}");

        // Before anything is written. The additions removal below touches the game, so a refusal
        // that waited for the restore would land on a half-uninstalled game.
        DirectModInstaller.EnsureBackupFolderIsSeparate(backupFolder, modBackupFolder);

        var plan = DirectModInstaller.Plan(gameFolder, backupFolder, say);
        say($"{plan.Matches.Count} archived + {plan.UnarchivedMatches.Count} loose + " +
            $"{plan.AudioMatches.Count} audio original(s) to put back");

        var additionsManifest = AdditionsManifest.Load(backupFolder);
        var modsToRemove = additionsManifest?.Mods.Select(m => m.Name).ToList() ?? new List<string>();

        if (modsToRemove.Count > 0)
        {
            say($"\nthis backup folder also records {modsToRemove.Count} mod(s) whose objects SAFT ADDED:");
            foreach (var name in modsToRemove) say($"   {name}");
            say("removing those rebuilds the archive, which takes longer than a plain restore");
        }

        var speed = new StorageSpeed();

        // Additions first - see the note above.
        if (modsToRemove.Count > 0 && additionsManifest is not null)
        {
            say("\nremoving added objects");
            var removed = AdditionUninstaller.Remove(
                gameFolder, additionsManifest, modsToRemove,
                new Reporter<AdditionProgress>(p => say($"   {p.Stage} {p.FilesDone}/{p.FilesTotal}")),
                deferRebuildsFor: null, onStep: say, speed: speed);

            say($"removed {removed.ArchiveEntriesRemoved} asset(s), {removed.DataLinesRemoved} map line(s), " +
                $"{removed.FreedObjectIds.Count} object slot(s) freed");
            foreach (var note in removed.Skipped) say($"   left alone - {note}");

            // Rewritten so a later uninstall doesn't try to remove all this a second time.
            additionsManifest.Save(backupFolder);
        }

        say("\nputting the originals back");
        var result = DirectModInstaller.Apply(
            plan, modBackupFolder,
            new Reporter<DirectInstallProgress>(p => say($"   {p.Stage} {p.FilesDone}/{p.FilesTotal}")),
            say, deferRebuildsFor: null, speed: speed);

        say($"\nrestored {result.Archives.Sum(s => s.FilesReplaced)} entry/entries across " +
            $"{result.Archives.Count} archive(s) and {result.Unarchived.Count} loose file(s)");

        Report(say, "could not be restored", result.UnarchivedFailed.Select(f => f.ToString()).ToList());

        // Last, once nothing else will touch these archives: give the space back.
        //
        // Removing an entry and shrinking a restored one both leave holes, and neither is reclaimed
        // on its own. Uninstalling is exactly when someone expects the folder to get smaller, so the
        // cost of a full pass is paid here rather than on every install.
        say("\npacking out unused space");
        long reclaimed = 0;
        foreach (var found in GameScanner.FindArchives(gameFolder))
        {
            try
            {
                // Counted out loud. This rewrites a whole archive and has taken three to four
                // minutes on tired storage; silence for that long reads as a hang, and a user who
                // closes SAFT during it is interrupting a rewrite of their game. Every tenth is
                // enough to show movement without filling the log.
                var lastTenth = -1;

                reclaimed += ImgArchiveEditor.Compact(
                    found.AbsolutePath, say, speed,
                    (done, count) =>
                    {
                        if (count <= 0) return;
                        var tenth = done * 10 / count;
                        if (tenth == lastTenth) return;
                        lastTenth = tenth;
                        say($"   {found.RelativePath} {done:N0}/{count:N0} ({tenth * 10}%)");
                    });
            }
            catch (Exception ex)
            {
                // The uninstall itself has already succeeded. Failing to reclaim space must not
                // turn that into a failure.
                say($"   could not pack {found.RelativePath}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        say(reclaimed > 0
            ? $"gave back {reclaimed / 1048576.0:N1} MB"
            : "nothing to reclaim - the archives were already packed");

        say($"\n{speed.Describe()}");
        say("\nUNINSTALL COMPLETE.");

        if (speed.Warning() is { } tired) warn?.Invoke("Give your SD card a rest", tired);
    }

    /// <summary>
    /// Lists a handful and then says how many more, rather than printing four hundred file names into
    /// a text view on a handheld.
    /// </summary>
    private static void Report(Action<string> say, string what, IReadOnlyList<string> items)
    {
        if (items.Count == 0) return;

        say($"\n{items.Count} file(s) {what}:");
        foreach (var item in items.Take(8)) say($"   {item}");
        if (items.Count > 8) say($"   ...and {items.Count - 8} more");
    }

    private sealed class Reporter<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }
}
