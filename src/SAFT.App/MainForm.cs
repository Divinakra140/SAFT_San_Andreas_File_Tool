using System.IO;
using SAFT.Core;

namespace SAFT.App;

public partial class MainForm : Form
{
    // ---- Tab 1: Extract ----
    private TextBox GameFolderBox = null!;
    private TextBox ExtractDestBox = null!;
    private CheckBox ExtractAudioCheckBox = null!;
    private Button ExtractButton = null!;
    private Label ScanSummaryText = null!;
    private Label ExtractWarningText = null!;
    private Label ExtractSlowWarningText = null!;

    /// <summary>Destination the size popup has already been shown for, so it appears once per choice rather than on every recalculation.</summary>
    private string? _warnedAboutExtractionSize;
    private Label ExtractSubProgressText = null!;
    private ProgressBar ExtractSubProgressBar = null!;
    private ProgressBar ExtractProgressBar = null!;

    // ---- Tab 2: Install into Extracted ----
    private TextBox InstallExtractionFolderBox = null!;
    private Label InstallManifestSummaryText = null!;
    private TextBox ModSourceFolderBox = null!;
    private Button InstallButton = null!;
    private Label InstallSubProgressText = null!;
    private ProgressBar InstallSubProgressBar = null!;

    // ---- Tab 3: Rebuild ----
    private TextBox ExtractionFolderBox = null!;
    private Label ManifestSummaryText = null!;
    private RadioButton NewFolderOption = null!;
    private RadioButton InPlaceWithBackupOption = null!;
    private Panel RebuildDestRow = null!;
    private TextBox RebuildDestBox = null!;
    private Label InPlaceWarningText = null!;
    private Button RebuildButton = null!;
    private Label RebuildSubProgressText = null!;
    private ProgressBar RebuildSubProgressBar = null!;
    private ProgressBar RebuildProgressBar = null!;

    // ---- Tab 4: Install without extraction ----
    private TextBox DirectGameFolderBox = null!;
    private TextBox DirectModFolderBox = null!;
    private Panel DirectBackupDestRow = null!;
    private TextBox DirectBackupDestBox = null!;
    private Label DirectBackupNoticeText = null!;
    private Button DirectInstallButton = null!;
    private Label DirectSubProgressText = null!;
    private ProgressBar DirectSubProgressBar = null!;
    private ProgressBar DirectProgressBar = null!;

    // ---- Tab 5: Uninstall Mod(s) ----
    private TextBox UninstallGameFolderBox = null!;
    private TextBox UninstallBackupFolderBox = null!;
    private CheckBox UninstallBackupModsCheckBox = null!;
    private Panel UninstallBackupDestRow = null!;
    private TextBox UninstallBackupDestBox = null!;
    private Button UninstallButton = null!;
    private Label UninstallSubProgressText = null!;
    private ProgressBar UninstallSubProgressBar = null!;
    private ProgressBar UninstallProgressBar = null!;

    private IReadOnlyList<FoundArchive>? _scanResults;
    private SaftManifest? _loadedManifest;
    private bool _uiReady;

    public MainForm()
    {
        // If UI construction throws for any reason (a GDI+/codec quirk under an unusual runtime
        // being the known-risky case), the exception would otherwise escape the constructor and
        // crash the whole process before Application.Run ever shows a window — "the app won't even
        // open," with no way to see why. Falling back to a minimal window with the real error
        // message is strictly better than that, and gives something to actually report back.
        try
        {
            BuildUi();
            _uiReady = true;
        }
        catch (Exception ex)
        {
            Controls.Clear();
            Text = "SAFT — startup error";
            Width = 640;
            Height = 320;
            Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
                Text = "SAFT failed to build its window and is showing this fallback instead of crashing outright. " +
                       "Please report this exact message:\n\n" + ex,
            });
        }
    }

    private static string FormatSize(long bytes) => $"{bytes / 1073741824.0:0.0}GB";

    /// <summary>
    /// Reports a caught failure with enough detail to act on.
    ///
    /// "Install failed: Object reference not set to an instance of an object" names a category of
    /// bug and nothing else - not the file, not the line, not the call that got there. The type and
    /// the stack are what identify it, so both go on screen AND into the log file beside the exe,
    /// which is the copy that can be sent on.
    /// </summary>
    private static void ReportFailure(Exception ex, string what)
    {
        var logPath = Path.Combine(Program.ExeFolder, "saft-crash-log.txt");
        var written = false;
        try
        {
            File.AppendAllText(logPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {what} failed{Environment.NewLine}{ex}" +
                $"{Environment.NewLine}{new string('-', 78)}{Environment.NewLine}");
            written = true;
        }
        catch
        {
            // Read-only media or no space; the dialog below still carries the detail.
        }

        // The first few frames are the useful part; the whole trace would not fit on a 544px screen.
        var frames = (ex.StackTrace ?? "").Split('\n').Take(4).Select(l => l.Trim());

        MessageBox.Show(
            $"{what} failed.{Environment.NewLine}{Environment.NewLine}" +
            $"{ex.GetType().Name}: {ex.Message}{Environment.NewLine}{Environment.NewLine}" +
            string.Join(Environment.NewLine, frames) +
            (written ? $"{Environment.NewLine}{Environment.NewLine}Full details written to {logPath}" : ""),
            "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>
    /// The reason part of a skip message, with the specific file dropped, so that twelve lines
    /// differing only in a filename collapse into one line with a count.
    /// </summary>
    private static string SkipReason(string skipped)
    {
        var colon = skipped.IndexOf(": ", StringComparison.Ordinal);
        return colon > 0 ? skipped[(colon + 2)..] : skipped;
    }

    /// <summary>
    /// Puts a chosen game folder into every tab that asks for one.
    ///
    /// It's the same folder in all of them — the user has one San Andreas install, and picking it
    /// again on each tab is pure friction. Each box stays editable, so switching to a different
    /// install is still just a Browse away; that choice then propagates in turn.
    /// </summary>
    private void ShareGameFolder(string folder)
    {
        foreach (var box in new[] { GameFolderBox, DirectGameFolderBox, UninstallGameFolderBox })
        {
            if (box is not null) box.Text = folder;
        }

        // The other tabs' buttons enable on their own criteria, and one of those has just been met.
        UpdateDirectInstallButtonEnabled();
        UpdateUninstallButtonEnabled();
    }

    /// <summary>
    /// Where the last folder was picked from, so the next browse starts there instead of at the root
    /// of the device.
    ///
    /// The folders SAFT asks for are almost always neighbours — a game folder, a mod folder and a
    /// backup folder usually sit within a directory or two of each other — so following the user
    /// around beats anchoring to any one of them. Remembering the GAME folder specifically would
    /// help the second pick and then be wrong for the rest.
    ///
    /// Deliberately not written to disk: SAFT is a portable exe that may be running from read-only
    /// media, and a settings file is a bigger promise than this feature is worth.
    /// </summary>
    private static string? _lastBrowsedFolder;

    private static string? BrowseForFolder(string description)
    {
        using var dialog = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true };

        // The parent, not the folder itself: opening inside the folder you last chose means climbing
        // out again to reach its neighbour, which is the common case.
        if (_lastBrowsedFolder is { } previous && Directory.Exists(previous))
        {
            var parent = Path.GetDirectoryName(previous.TrimEnd(Path.DirectorySeparatorChar));
            dialog.InitialDirectory = Directory.Exists(parent) ? parent : previous;
            dialog.SelectedPath = previous;
        }

        if (dialog.ShowDialog() != DialogResult.OK) return null;

        _lastBrowsedFolder = dialog.SelectedPath;
        return dialog.SelectedPath;
    }

    /// <summary>
    /// Explains why a game script in the mod folder was left alone. Shown before anything is
    /// installed, so the user knows the mod isn't fully applied and can decide for themselves
    /// whether to place it by hand. The two kinds get separate messages because the manual route
    /// differs completely: main.scm is one loose file, while a streamed script only comes out with
    /// an extract-and-rebuild.
    /// </summary>
    private void WarnAboutRefusedScripts(IReadOnlyList<RefusedScript> refusedScripts)
    {
        if (refusedScripts.Count == 0) return;

        if (refusedScripts.Any(s => s.Kind == RefusedScriptKind.MainScript))
        {
            MessageBox.Show(
                "Your mod folder contained a modified script (story line) file. SAFT doesn't replace these, " +
                "as the result may be irreversible and can corrupt save files.\n\n" +
                "If you wish to replace it yourself and run the risk, it's just one file, it isn't archived, and " +
                "it's easily accessible in your game directory — navigate to:\n\n" +
                "    data > script > main.scm\n\n" +
                "and replace the game's main.scm with the modified main.scm from your mod folder.\n\n" +
                "Everything else in your mod will still be installed normally.",
                "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        var streamed = refusedScripts.Where(s => s.Kind == RefusedScriptKind.StreamedScript).ToList();
        if (streamed.Count > 0)
        {
            MessageBox.Show(
                $"Your mod folder contained {streamed.Count} modified mission/minigame script file(s) that live inside " +
                "the game's script.img archive. SAFT doesn't replace these either, for the same reason — the result " +
                "may be irreversible and can corrupt save files.\n\n" +
                "If you wish to install them yourself and run the risk, use SAFT's own tabs:\n\n" +
                "    1. \"Extract Game Files\" to extract your game\n" +
                "    2. copy your .scm files into the extracted data\\script\\script.img folder\n" +
                "    3. \"Rebuild from Extracted\" to build the game back up\n\n" +
                "Everything else in your mod will still be installed normally.",
                "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Every file name the game already has, archived or loose. Answering "does this already exist"
    /// is what separates a replacement from an addition.
    /// </summary>
    private static HashSet<string> GameFileNames(string gameRoot, GameFiles? listing = null)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Logged, because when this walks rather than reusing a listing it is one of the two places
        // SAFT has hung outright - and the walk now names each folder as it reads it.
        var files = GameFiles.For(gameRoot, listing, ActivityLog.Note);

        foreach (var found in GameScanner.FindArchives(gameRoot, files))
        {
            try
            {
                // Names only, so the archive is never held open for it. See ImgArchive.ReadDirectory.
                foreach (var entry in ImgArchive.ReadDirectory(found.AbsolutePath)) names.Add(entry.Name);
            }
            catch
            {
                // An unreadable archive just means those names look "new"; the addition popups then
                // ask rather than acting, which is the safe direction.
            }
        }

        // Handed the listing this method already has. Without it the index walked the game folder a
        // second time inside a method that had just walked it - and that walk is one of the two the
        // app has hung inside. Same fix as the rest of the single-walk work: stop asking twice.
        foreach (var group in UnarchivedIndex.Build(gameRoot, files)) names.Add(group.Key);
        return names;
    }

    /// <summary>The size of each file this mod would replace, keyed by name, for the streaming comparison.</summary>
    private static Dictionary<string, long> ReplacementSizes(DirectInstallPlan plan)
    {
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var match in plan.Matches)
        {
            try { sizes[match.FileName] = new FileInfo(match.ModFilePath).Length; } catch { }
        }
        foreach (var match in plan.UnarchivedMatches)
        {
            try { sizes[match.FileName] = new FileInfo(match.ModFilePath).Length; } catch { }
        }

        return sizes;
    }

    /// <summary>
    /// Shows what a mod does to the game's streaming load, and asks permission when it goes beyond
    /// what the player's own game demonstrably handles. Returns false only if the user chooses to
    /// stop. Within-range mods just get an acknowledgement, never a decision.
    /// </summary>
    /// <summary>
    /// Holds the user for a minute after an install, before they can go and launch the game.
    ///
    /// The wait is real and it was measured, not reasoned about. Writing the archive finishes well
    /// before an SD card has committed it, and a game launched inside that window opens a
    /// half-written archive and hangs on a black screen — the "Canton freeze". Flushing to disk at
    /// the end of the write did NOT close the gap: packs G, J and E all froze on an immediate first
    /// launch after that change, K did not, N did not after deliberately waiting a minute, and the
    /// PreRelease pack froze again on an immediate launch. Waiting has never once failed.
    ///
    /// The first button is genuinely disabled rather than merely counting, because the informative
    /// version of this advice already existed in the readme and was read by the person who wrote it,
    /// who then launched immediately anyway and got the freeze.
    ///
    /// The second button is live from the start, and that is the point of having two: the wait only
    /// matters if the next thing you do is launch the game on Winlator. Anyone carrying on inside
    /// SAFT touches nothing the card is still writing, and anyone on real Windows never needed the
    /// wait at all, so neither should be made to sit through a minute that does nothing for them.
    /// </summary>
    private void HoldBeforeLaunching()
    {
        // Shown in BOTH editions, which took a moment's thought to land on.
        //
        // The freeze this prevents is an SD-card write-back still in flight when the game opens the
        // archive, so what matters is the wall-clock gap between finishing an install and starting
        // the game. The usual Dev workflow serves that gap for free: install on a Windows machine,
        // eject the card, carry it to the handheld, boot Winlator - a minute on its own. On that
        // reading Dev should skip the dialog, and for a while it did.
        //
        // But Dev runs under Winlator perfectly well; its extraction tabs are merely slow there, and
        // nothing stops someone using its Install Mods tab on the handheld and launching seconds
        // later. That user needs the warning exactly as much as an ordinary user does.
        //
        // What settles it is that the countdown only disables the FIRST button. "Dismiss" is live
        // from the first frame, so anyone who does not need the wait pays one click for it, not a
        // minute. One click is a cheap price for covering the case that would otherwise look, to the
        // person it happens to, like SAFT broke their game.
        const int seconds = 60;

        // What this wait is FOR was a mystery for months - it was arrived at empirically, by noticing
        // that a game launched straight after an install would sometimes freeze and one launched a
        // minute later never did. Measuring an SD card's write speed collapse explained it: an erase
        // is the slowest thing flash does, a card keeps erasing in the background long after SAFT has
        // finished, and the game's asset reads queue up behind that work. The timer was right; only
        // the reason was missing. Saying the reason is what makes people actually wait.
        using var wait = ConfirmDialog.Wait(
            "Give your SD card a minute before you launch the game.\n\n" +
            "SAFT just wrote a lot of data, and your card is still tidying up after it. If GTA SA " +
            "starts while that is happening, it can sit on the loading screen or fail to load an " +
            "area. That is the card being busy - your game is not damaged and nothing needs fixing.\n\n" +
            "A minute is usually enough. Slower or nearly-full cards can want two or three.",
            "OK",
            "Dismiss",
            seconds);

        wait.ShowDialog(this);
    }

    private bool ConfirmStreamingImpact(StreamingVerdict verdict)
    {
        if (!verdict.NeedsConfirmation)
        {
            // The single-button path, taken only by a Fine verdict — which is to say, only when the
            // news is good. Until a mod finally came back Fine, no test had ever run this branch.
            //
            // Logged either side of the constructor call because ConfirmDialog's own first line runs
            // in the constructor BODY, after the Form base constructor has already created window
            // resources. Without this, "died before the dialog" and "died building the window" look
            // identical in the log: both are simply a missing line.
            ActivityLog.Note("dialog: about to construct the Fine acknowledgement");
            using var ok = ConfirmDialog.Acknowledgement(verdict.Message, "OK", verdict.Severity);
            ActivityLog.Note("dialog: Fine acknowledgement constructed, showing it");
            ok.ShowDialog(this);
            ActivityLog.Note("dialog: Fine acknowledgement closed");
            return true;
        }

        using var confirm = new ConfirmDialog(
            verdict.Message,
            "Continue",
            "Don't install",
            verdict.Severity);
        confirm.ShowDialog(this);
        return confirm.Result;
    }

    /// <summary>
    /// Asks whether to install a mod's new objects. There is deliberately no "add without logging"
    /// option: nobody should end up with additions they can't cleanly uninstall.
    /// </summary>
    private bool ConfirmAdditions(AdditionPlan additions)
    {
        var slotsAfter = additions.SlotsAvailable - additions.SlotsRequired;
        using var confirm = new ConfirmDialog(
            $"Your mod folder contains {additions.SlotsRequired} new object(s) that are not in your game " +
            $"directory. These would take up {additions.SlotsRequired} of your game's object slots. SAFT can " +
            $"add them because there are currently {additions.SlotsAvailable} available and compatible slots.\n\n" +
            "Would you like SAFT to add them, and write backup-logs of how they were added, so you can " +
            $"cleanly uninstall them later? This would leave you with {slotsAfter} empty slots.",
            "Yes, add new assets",
            "No, replacements only");
        confirm.ShowDialog(this);

        if (!confirm.Result) return false;

        // SAFT's own dialog rather than a MessageBox. This one sits at the exact point three separate
        // crashes have happened around, and as a native dialog it left no trace in the log at all -
        // the run before this change showed a 1.8 second silence here every single time, with the
        // crash landing on one side of it or the other and no way to tell which. See ConfirmDialog.Note.
        using var note = ConfirmDialog.Note(
            "Adding new assets…\n\n" +
            "Please note that any assets added through other tools or methods will not be uninstallable " +
            "through SAFT. But if SAFT added it in, SAFT can remove it later.",
            "OK");
        note.ShowDialog(this);
        return true;
    }

    /// <summary>
    /// The mod brought new assets but no .ide/.ipl, so the game would never show them — they would
    /// take up object slots and appear nowhere. A guide listing the player's own free slots is
    /// written next to the files they need to fix.
    /// </summary>
    /// <summary>
    /// Asks whether files SAFT doesn't recognise are meant to be new, BEFORE any of the prompts that
    /// help add them.
    ///
    /// A replacement only happens when the mod file has exactly the same name as the game file it
    /// replaces. Someone who builds a new taxi and calls it mytaxi.dff has made a mod that replaces
    /// nothing — and every prompt after this one would then cheerfully walk them through adding a
    /// second, separate vehicle to the game, which reads as instructions rather than as a warning.
    /// By the end they could have followed every step correctly and still not have the thing they set
    /// out to make.
    ///
    /// So the question is asked once, plainly, at the top: did you mean to ADD these, or did you mean
    /// to REPLACE something and get the names wrong? Everything downstream assumes the answer.
    /// </summary>
    private bool ConfirmAdditionsAreIntentional(DirectInstallPlan plan, AdditionPlan additions)
    {
        const int shown = 6;
        var names = additions.NewAssets.Select(a => a.FileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var listed = string.Join(Environment.NewLine, names.Take(shown).Select(n => "    " + n));
        if (names.Count > shown) listed += $"{Environment.NewLine}    ...and {names.Count - shown} more";

        var replaced = plan.Matches.Count + plan.UnarchivedMatches.Count + plan.AudioMatches.Count + plan.StreamMatches.Count;

        // A mod shipping .ide/.ipl has gone to the trouble of describing objects and where they go,
        // which is a deliberate act rather than a slip. Worth saying so, instead of implying the
        // author probably made a mistake.
        var reading = additions.Definitions.Count > 0
            ? "This mod also supplies .ide/.ipl map data for them, so it does look deliberate."
            : "This mod supplies no .ide/.ipl map data for them, which is what a mod meant purely as a " +
              "REPLACEMENT looks like.";

        // Stop means stop. Nothing is installed, including the files that DID match — a half-applied
        // mod is worse than none, because it looks finished. Saying so here matters more than it
        // looks: the line that used to sit in this spot claimed the matched files would go in
        // "whichever way you answer", which was not even true of the code, and would have left
        // someone believing their mod was installed when the install had been cancelled.
        var alsoMatched = replaced > 0
            ? $"{Environment.NewLine}{Environment.NewLine}{replaced} other file(s) in this folder DO match " +
              "your game. Continuing replaces those as normal and adds the ones above. Stopping installs " +
              "nothing at all - not those either - so you never end up with half a mod in your game."
            : "";

        using var confirm = new ConfirmDialog(
            $"{names.Count} file(s) in this mod folder are not named the same as anything in your game:" +
            $"{Environment.NewLine}{Environment.NewLine}{listed}{Environment.NewLine}{Environment.NewLine}" +

            $"SAFT is going to assume these are assets you want to ADD to the game, that were never there " +
            $"before, and walk you through doing that properly. {reading}{alsoMatched}" +
            $"{Environment.NewLine}{Environment.NewLine}" +

            // "a new taxi has to be called taxi.dff" was the first attempt, and "new" is the one word
            // this sentence cannot afford - it is the word for the other thing, in a message whose
            // entire job is telling the two apart.
            "If this mod was only ever meant to REPLACE things, stop here. A file replaces a game file " +
            "when it has exactly the same name as it: a taxi REPLACEMENT mod has to be called taxi.dff, " +
            "not mytaxi.dff. Rename your files to match the ones they are meant to replace, then run " +
            "this again. Nothing has been changed yet.",

            "Yes, these are meant to be new additions",
            "Stop, I need to rename these files",
            StreamingSeverity.Caution);
        confirm.ShowDialog(this);
        return confirm.Result;
    }

    private bool ConfirmAdditionsWithoutPlacementData(string gameFolder, string modFolder, AdditionPlan additions)
    {
        var guide = AddingAssetsGuide.TryWrite(gameFolder, modFolder, additions.SlotsAvailable);

        var whereToLook = guide.Written
            ? $"{AddingAssetsGuide.FileName} has been put in your mod folder. It explains how to add objects " +
              $"properly, and lists the {additions.SlotsAvailable} empty object slots in your game."
            : $"SAFT could not write {AddingAssetsGuide.FileName} into your mod folder ({guide.Reason}). Your " +
              $"game has {additions.SlotsAvailable} free object slots. See the Adding Assets guide on the " +
              "SAFT releases page.";

        using var confirm = new ConfirmDialog(
            "Your mod folder has new assets that aren't in your game, but no .ide and .ipl files to go with " +
            "them (.ide says what an object is, .ipl says where it goes). Without those, SAFT can copy the " +
            "files in but the game will never show them — they'd use up object slots and appear nowhere.\n\n" +
            whereToLook,
            "Install the rest",
            "Stop, let me fix it");
        confirm.ShowDialog(this);
        return confirm.Result;
    }

    /// <summary>
    /// The record of a previous install of this same mod folder, if the backup folder has one.
    ///
    /// Matching on the mod folder's name is enough here, because these are records SAFT itself wrote
    /// for that same folder — it isn't being used to identify a mod in the wild.
    /// </summary>
    /// <param name="manifest">
    /// The record that was read, handed back so the reinstall below can use it instead of reading the
    /// same file again a few seconds later. Two reads of one small file is not much work, but this is
    /// the stretch of the install three crashes have now happened in, and the least it can do is not
    /// ask twice.
    /// </param>
    /// <summary>
    /// Returns the installed copy of this mod if there is one, and the record it came from.
    ///
    /// A tuple rather than an out parameter so this can be called on a background thread - it reads a
    /// file, and reading files on the UI thread is what leaves Winlator unable to answer Android for
    /// long enough to be declared unresponsive.
    /// </summary>
    private static (AddedMod? Installed, AdditionsManifest? Record) FindInstalledMod(string backupFolder, string modName)
    {
        if (string.IsNullOrWhiteSpace(backupFolder)) return (null, null);

        try
        {
            var manifest = AdditionsManifest.Load(backupFolder);
            return (manifest?.Mods.FirstOrDefault(m => m.Name.Equals(modName, StringComparison.OrdinalIgnoreCase)), manifest);
        }
        catch
        {
            // An unreadable manifest just means we can't tell, and installing normally is no worse
            // than today's behaviour.
            return (null, null);
        }
    }

    /// <summary>
    /// Says, in its own window, when the drive underneath was crawling.
    ///
    /// Its own window rather than tacked onto the install summary, because that summary is a plain
    /// MessageBox: it is sized by Windows, not by SAFT, and Winlator's 960x544 screen is short enough
    /// that adding paragraphs to it pushed its own OK button off the bottom of the display where it
    /// could not be pressed. Everything SAFT writes itself goes through ConfirmDialog, which measures
    /// against the real screen and scrolls rather than overflowing.
    /// </summary>
    private void ReportSlowStorage(StorageSpeed speed)
    {
        if (speed.Warning() is not { } warning) return;

        using var note = ConfirmDialog.Note(warning, "OK");
        note.ShowDialog(this);
    }

    /// <summary>
    /// Asks before reinstalling a mod that's already installed. The cost is worth naming: removing
    /// the old copy means an extra rebuild of the archive, which is minutes rather than seconds.
    /// </summary>
    private bool ConfirmReinstall(AddedMod existing)
    {
        var slots = existing.ObjectIds.Count == 0
            ? ""
            : $" using object slot(s) {string.Join(", ", existing.ObjectIds.Take(8))}";

        using var confirm = new ConfirmDialog(
            $"'{existing.Name}' is already installed - {existing.ObjectIds.Count} object(s){slots}, added on " +
            $"{existing.AddedAtUtc.ToLocalTime():d MMMM yyyy}.\n\n" +
            "SAFT can remove that copy and install this one fresh. That is how you update a mod to a " +
            "newer version.\n\n" +
            "Be warned this is slow. SAFT has to rebuild the whole game archive twice, once to take the " +
            "old copy out and once to put the new one in, so expect it to take several minutes and to " +
            "look frozen at times. The progress bar will keep moving.\n\n" +
            "If this is a mistake and you don't want to update your currently installed mod, just press " +
            "Leave it as it is.",
            "Reinstall it",
            "Leave it as it is",
            StreamingSeverity.Caution);
        confirm.ShowDialog(this);
        return confirm.Result;
    }

    /// <summary>The mod needs more object slots than the game has left, so its new objects can't be installed.</summary>
    private bool ConfirmAdditionsThatDoNotFit(AdditionPlan additions)
    {
        using var confirm = new ConfirmDialog(
            $"Your mod folder contains {additions.SlotsRequired} new object(s) that aren't in your game. SAFT " +
            "can only add them if there are enough free object slots, and your game has " +
            $"{additions.SlotsAvailable} left — so these additional assets will not be installed.\n\n" +
            "Would you like to still install the other files in the mod folder?",
            "Skip them, replacements only",
            "Don't install");
        confirm.ShowDialog(this);
        return confirm.Result;
    }

    /// <summary>
    /// Refuses to place objects the mod hasn't supplied collision for.
    ///
    /// This is a refusal rather than a warning because installing anyway produces a game that
    /// crashes the moment a save is loaded — not a game with objects you can walk through. The
    /// wording says what's missing, who can fix it and how, since the user usually can't: the .col
    /// has to come from whoever built the mod.
    /// </summary>
    private bool ConfirmSkippingAdditionsWithoutCollision(string gameFolder, string modFolder, AdditionPlan additions)
    {
        // Same treatment as a mod arriving without .ide/.ipl: the user is missing a piece they can
        // actually go and fix, so the guide goes into the mod folder next to the files it talks about.
        var guide = AddingAssetsGuide.TryWrite(gameFolder, modFolder, additions.SlotsAvailable);

        var names = additions.ModelsWithoutCollision;
        var listed = string.Join(", ", names.Take(6)) + (names.Count > 6 ? $", and {names.Count - 6} more" : "");

        var whereToLook = guide.Written
            ? $"{AddingAssetsGuide.FileName} has been put in your mod folder. It explains how to make a " +
              "collision file and how to check the name inside it matches your model."
            : $"SAFT could not write {AddingAssetsGuide.FileName} into your mod folder ({guide.Reason}). " +
              "See the Adding Assets guide on the SAFT releases page.";

        using var confirm = new ConfirmDialog(
            $"This mod places {names.Count} new object(s) but includes no collision for them: {listed}.\n\n" +
            "Anything placed on the map needs a collision (.col) file. Without one the game crashes as " +
            "soon as you load a save — anywhere on the map, not just near the object. So SAFT will not " +
            "add these.\n\n" +
            whereToLook + "\n\n" +
            "Install the rest of the mod anyway?",
            "Skip them, replacements only",
            "Don't install",
            StreamingSeverity.Serious);
        confirm.ShowDialog(this);
        return confirm.Result;
    }

    /// <summary>Sets a progress bar's fractional (archive-count + within-archive-fraction) position using scaled integer steps, since WinForms' ProgressBar.Value is an int, not the double WPF's was.</summary>
    private static void SetScaledProgress(ProgressBar bar, int groupIndex, int groupCount, int filesDone, int filesTotal)
    {
        const int scale = 1000;
        bar.Maximum = Math.Max(1, groupCount) * scale;
        var fraction = filesTotal == 0 ? 0 : (double)filesDone / filesTotal;
        bar.Value = Math.Clamp((int)((groupIndex - 1) * scale + fraction * scale), 0, bar.Maximum);
    }

    // ================= TAB 1: Extract =================

    private void OnBrowseGameFolder(object? sender, EventArgs e)
    {
        var folder = BrowseForFolder("Select your GTA San Andreas game folder");
        if (folder is null) return;

        if (!GameScanner.LooksLikeSanAndreasInstall(folder))
        {
            var proceed = MessageBox.Show(
                "This folder doesn't look like a San Andreas PC install (no gta_sa.exe / models\\gta3.img found). " +
                "Use it anyway?",
                "SAFT", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (proceed != DialogResult.Yes) return;
        }

        ShareGameFolder(folder);
        _scanResults = null;
        ExtractButton.Enabled = false;
        ScanSummaryText.Text = "";
        ExtractWarningText.Text = "";
    }

    private void OnBrowseExtractDest(object? sender, EventArgs e)
    {
        var folder = BrowseForFolder("Select where to extract the game's archives");
        if (folder is not null) ExtractDestBox.Text = folder;

        _ = UpdateExtractionWarningAsync();
    }

    private async void OnScan(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(GameFolderBox.Text))
        {
            MessageBox.Show("Pick a game folder first.", "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var gameFolder = GameFolderBox.Text;
        ScanSummaryText.Text = "Scanning…";
        ExtractWarningText.Text = "";
        ExtractButton.Enabled = false;

        try
        {
            _scanResults = await Task.Run(() => GameScanner.FindArchives(gameFolder));
        }
        catch (Exception ex)
        {
            ScanSummaryText.Text = "";
            MessageBox.Show($"Scan failed: {ex.Message}", "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (_scanResults.Count == 0)
        {
            ScanSummaryText.Text = "No VER2 IMG archives were found under that folder.";
            return;
        }

        ScanSummaryText.Text = $"Found {_scanResults.Count} archive(s): " +
                                string.Join(", ", _scanResults.Select(a => a.RelativePath));
        ExtractButton.Enabled = true;

        await UpdateExtractionWarningAsync();
    }

    /// <summary>
    /// The reason "extracted size" and real "size on disk" can differ by many GB isn't a bug in
    /// the byte math — it's that ~20k small game files each get rounded up to a full filesystem
    /// cluster, and clusters on large removable/exFAT drives are often far bigger than the IMG
    /// format's own 2048-byte sectors. Needs both a game folder (to know the archives) and a
    /// destination (to know which drive's cluster size applies), so this is a no-op until both
    /// are set, and re-runs whenever either changes.
    /// </summary>
    private async Task UpdateExtractionWarningAsync()
    {
        if (_scanResults is not { Count: > 0 } || string.IsNullOrWhiteSpace(ExtractDestBox.Text))
            return;

        var scanResults = _scanResults;
        var gameFolder = GameFolderBox.Text;
        var destination = ExtractDestBox.Text;
        var includeAudio = ExtractAudioCheckBox.Checked;

        ExtractWarningText.Text = "Calculating storage required…";
        try
        {
            var totalBytes = await Task.Run(() =>
            {
                var clusterSize = Win32Disk.GetClusterSizeBytes(destination);
                return Extractor.EstimateExtractedSizeOnDiskBytes(gameFolder, scanResults, includeAudio, clusterSize);
            });

            if (_scanResults != scanResults || ExtractDestBox.Text != destination) return; // stale by the time this finished

            ExtractWarningText.Text = $"Warning, extracted game files will take up approximately {FormatSize(totalBytes)} of storage.";

            // Also said out loud, once, the first time a size is worked out for this destination.
            // The label alone was not doing the job: it is red text sitting directly above other red
            // text, which the eye reads as one block and skips. Extraction is the first and most
            // inviting button in the app, and it is the single most expensive thing SAFT can do -
            // whoever is about to press it should have had to dismiss something.
            if (_warnedAboutExtractionSize != destination)
            {
                _warnedAboutExtractionSize = destination;
                using var warning = ConfirmDialog.Acknowledgement(
                    $"Extracting this game will write about {FormatSize(totalBytes)} into:\n{destination}\n\n" +
                    $"That is over 20,000 separate files. On Windows this takes a few minutes. On Winlator " +
                    "it can take a long time - creating that many files one at a time is slow on Android's " +
                    "storage, and it is the slowest thing SAFT does by a wide margin.\n\n" +
                    "You only need this tab to rebuild the game from scratch. To install a mod, use " +
                    "\"Install Mod(s) without extraction\" instead - it changes only the files your mod " +
                    "touches and takes seconds.",
                    "OK, I understand",
                    StreamingSeverity.Caution);
                warning.ShowDialog(this);
            }
        }
        catch (Exception ex)
        {
            ExtractWarningText.Text = $"Couldn't calculate storage size: {ex.Message}";
        }
    }

    private async void OnExtractAudioOptionChanged(object? sender, EventArgs e) => await UpdateExtractionWarningAsync();

    private async void OnExtract(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ExtractDestBox.Text))
        {
            MessageBox.Show("Pick a destination folder first.", "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var gameFolder = GameFolderBox.Text;
        var destFolder = ExtractDestBox.Text;
        var includeAudio = ExtractAudioCheckBox.Checked;

        ExtractProgressBar.Value = 0;
        ExtractSubProgressBar.Value = 0;
        ExtractSubProgressText.Text = "Starting…";
        SetExtractControlsEnabled(false);

        // Logged per STAGE, never per file - the whole reason extraction was slow is that per-file
        // work reaches the UI thread. This fires a handful of times for the entire run, and turns
        // "it closed itself somewhere in models" into a line naming the archive and the file count.
        var loggedStage = "";
        var progress = new Progress<ExtractionProgress>(p =>
        {
            SetScaledProgress(ExtractProgressBar, p.ArchiveIndex, p.ArchiveCount, p.FilesDone, p.FilesTotal);

            ExtractSubProgressBar.Maximum = Math.Max(1, p.FilesTotal);
            ExtractSubProgressBar.Value = Math.Clamp(p.FilesDone, 0, ExtractSubProgressBar.Maximum);
            ExtractSubProgressText.Text = $"[{p.ArchiveIndex}/{p.ArchiveCount}] {p.CurrentArchive} — file {p.FilesDone:N0} of {p.FilesTotal:N0}";

            if (p.CurrentArchive == loggedStage) return;
            loggedStage = p.CurrentArchive;
            ActivityLog.Note($"extract: starting {p.CurrentArchive} ({p.FilesTotal:N0} file(s)), stage {p.ArchiveIndex} of {p.ArchiveCount}");
        });

        try
        {
            ActivityLog.Note($"extract: starting, audio {(includeAudio ? "included" : "excluded")}, into {destFolder}");
            var manifest = await Task.Run(() => Extractor.Extract(gameFolder, destFolder, includeAudio, progress));
            ActivityLog.Note($"extract: finished, {manifest.Archives.Count} archive(s)");
            ExtractSubProgressText.Text = "Done.";
            MessageBox.Show($"Extraction complete. {manifest.Archives.Count} archive(s) extracted to {destFolder}.", "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Extraction failed: {ex.Message}", "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetExtractControlsEnabled(true);
        }
    }

    private void SetExtractControlsEnabled(bool enabled)
    {
        ExtractButton.Enabled = enabled && _scanResults is { Count: > 0 };
    }

    // ================= TAB 2: Install Mod into Extracted =================

    /// <summary>
    /// Loads a manifest and reflects it into both the Install and Rebuild tabs, which share the
    /// same "which extracted install am I working with" state — picking the folder in either tab
    /// keeps both in sync instead of asking the user to select it twice. Also kicks off the
    /// (potentially slow) rebuild size estimate used by the Rebuild tab's radio button labels.
    /// </summary>
    private bool TryLoadManifest(string folderPath)
    {
        try
        {
            _loadedManifest = SaftManifest.Load(folderPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        var totalEntries = _loadedManifest.Archives.Sum(a => a.OriginalEntryOrder.Count);
        var summary =
            $"Extracted from: {_loadedManifest.GameRootPath}\n" +
            $"{_loadedManifest.Archives.Count} archive(s), {totalEntries} original entries. " +
            $"Extracted {_loadedManifest.ExtractedAtUtc.ToLocalTime():g}.";

        ExtractionFolderBox.Text = folderPath;
        ManifestSummaryText.Text = summary;
        RebuildButton.Enabled = true;

        InstallExtractionFolderBox.Text = folderPath;
        InstallManifestSummaryText.Text = summary;
        InstallButton.Enabled = !string.IsNullOrWhiteSpace(ModSourceFolderBox.Text);

        _ = UpdateRebuildSizeEstimateAsync(folderPath);

        return true;
    }

    private async Task UpdateRebuildSizeEstimateAsync(string extractionFolder)
    {
        const string newFolderBase = "Rebuild into a new folder (safe, non-destructive)";
        const string inPlaceBase = "Install over the original game files (backs up each archive as .img.bak first, inside the corresponding folder within the rebuilt game directory)";

        NewFolderOption.Text = $"{newFolderBase} — calculating size…";
        InPlaceWithBackupOption.Text = $"{inPlaceBase} — calculating size…";

        try
        {
            var estimate = await Task.Run(() => RebuildEstimator.Estimate(extractionFolder));
            if (ExtractionFolderBox.Text != extractionFolder) return; // a different folder was picked meanwhile

            NewFolderOption.Text =
                $"{newFolderBase} adds a second playable game folder totaling {FormatSize(estimate.NewFolderTotalBytes)} in the output folder";
            InPlaceWithBackupOption.Text =
                $"{inPlaceBase} replacing original {FormatSize(estimate.GameRootTotalBytes)} game with a " +
                $"{FormatSize(estimate.InPlaceWithBackupTotalBytes)} total output game (including .img.bak clean backups)";
        }
        catch (Exception)
        {
            NewFolderOption.Text = newFolderBase;
            InPlaceWithBackupOption.Text = inPlaceBase;
        }
    }

    private void OnBrowseExtractionFolderForInstall(object? sender, EventArgs e)
    {
        var folder = BrowseForFolder("Select the folder you extracted the game to");
        if (folder is not null) TryLoadManifest(folder);
    }

    private void OnBrowseModSourceFolder(object? sender, EventArgs e)
    {
        var folder = BrowseForFolder("Select the folder containing the mod's files");
        if (folder is null) return;

        ModSourceFolderBox.Text = folder;
        InstallButton.Enabled = _loadedManifest is not null;
    }

    private async void OnInstallMod(object? sender, EventArgs e)
    {
        if (_loadedManifest is null)
        {
            MessageBox.Show("Pick an extracted folder first.", "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(ModSourceFolderBox.Text))
        {
            MessageBox.Show("Pick a mod folder first.", "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var extractionFolder = InstallExtractionFolderBox.Text;
        var modFolder = ModSourceFolderBox.Text;

        InstallSubProgressBar.Value = 0;
        InstallSubProgressText.Text = "Starting…";
        InstallButton.Enabled = false;

        var progress = new Progress<ModInstallProgress>(p =>
        {
            InstallSubProgressBar.Maximum = Math.Max(1, p.FilesTotal);
            InstallSubProgressBar.Value = Math.Clamp(p.FilesDone, 0, InstallSubProgressBar.Maximum);
            InstallSubProgressText.Text = $"Checking file {p.FilesDone:N0} of {p.FilesTotal:N0}: {p.CurrentFile}";
        });

        try
        {
            var result = await Task.Run(() => ModInstaller.Install(extractionFolder, modFolder, progress));

            WarnAboutRefusedScripts(result.RefusedScripts);

            InstallSubProgressText.Text = "Done.";
            var unmatchedCount = result.Unmatched.Count + result.AudioUnmatched.Count;
            MessageBox.Show(
                $"Done. Routed {result.Routed.Count} file(s), {result.AudioRouted.Count} audio file(s)." +
                (result.UnarchivedRouted.Count > 0 ? $" {result.UnarchivedRouted.Count} game file(s) outside the archives (map data, etc)." : "") +
                (unmatchedCount > 0 ? $" {unmatchedCount} file(s) didn't match anything and were left unplaced." : "") +
                (result.Ambiguous.Count > 0
                    ? $"\n\n{result.Ambiguous.Count} file(s) exist both inside an archive and loose in the game folder with different contents — " +
                      "only the archived copy was replaced, since SAFT can't tell which one your mod meant."
                    : "") +
                "\n\nGo to the Rebuild tab when you're ready to build the archives.",
                "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            ReportFailure(ex, "Install");
        }
        finally
        {
            InstallButton.Enabled = true;
        }
    }

    // ================= TAB 3: Rebuild from Extracted =================

    private void OnBrowseExtractionFolder(object? sender, EventArgs e)
    {
        var folder = BrowseForFolder("Select the folder you extracted the game to");
        if (folder is not null) TryLoadManifest(folder);
    }

    private void OnOutputModeChanged(object? sender, EventArgs e)
    {
        if (!_uiReady) return;

        RebuildDestRow.Visible = NewFolderOption.Checked;
        InPlaceWarningText.Visible = InPlaceWithBackupOption.Checked;
    }

    private void OnBrowseRebuildDest(object? sender, EventArgs e)
    {
        var folder = BrowseForFolder("Select where to write the rebuilt archives");
        if (folder is not null) RebuildDestBox.Text = folder;
    }

    private async void OnRebuild(object? sender, EventArgs e)
    {
        if (_loadedManifest is null)
        {
            MessageBox.Show("Pick an extracted folder first.", "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var extractionFolder = ExtractionFolderBox.Text;
        var gameRoot = _loadedManifest.GameRootPath;
        var newFolder = NewFolderOption.Checked;
        var withBackup = InPlaceWithBackupOption.Checked;

        if (newFolder && string.IsNullOrWhiteSpace(RebuildDestBox.Text))
        {
            MessageBox.Show("Pick an output folder first.", "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (withBackup)
        {
            var confirm = MessageBox.Show(
                $"This will overwrite the archives inside:\n{gameRoot}\n\n" +
                "A .img.bak backup of each original is created automatically, inside the corresponding folder within the rebuilt game directory. Continue?",
                "SAFT", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;
        }

        RebuildProgressBar.Value = 0;
        RebuildSubProgressBar.Value = 0;
        RebuildButton.Enabled = false;

        var rebuildProgress = new Progress<RebuildProgress>(p =>
        {
            SetScaledProgress(RebuildProgressBar, p.ArchiveIndex, p.ArchiveCount, p.FilesDone, p.FilesTotal);

            RebuildSubProgressBar.Maximum = Math.Max(1, p.FilesTotal);
            RebuildSubProgressBar.Value = Math.Clamp(p.FilesDone, 0, RebuildSubProgressBar.Maximum);
            RebuildSubProgressText.Text = $"[{p.ArchiveIndex}/{p.ArchiveCount}] {p.CurrentArchive} — file {p.FilesDone:N0} of {p.FilesTotal:N0}";
        });

        try
        {
            IReadOnlyList<RebuildSummary> summaries;

            if (newFolder)
            {
                var outputFolder = RebuildDestBox.Text;
                summaries = await Task.Run(() =>
                    Rebuilder.RebuildNewPlayableCopy(extractionFolder, outputFolder, rebuildProgress));
            }
            else
            {
                summaries = await Task.Run(() =>
                    Rebuilder.RebuildInPlace(extractionFolder, gameRoot, makeBackups: withBackup, rebuildProgress));
            }

            RebuildSubProgressText.Text = "Done.";

            var totalAdded = summaries.Sum(s => s.Added);
            var totalRemoved = summaries.Sum(s => s.Removed);
            var changeSummary = totalAdded > 0 || totalRemoved > 0
                ? $"\n\n{totalAdded} file(s) added, {totalRemoved} removed across {summaries.Count} archive(s)."
                : "";

            if (withBackup)
            {
                var backupNames = summaries.Select(s => Path.GetFileName(s.RelativePath) + ".bak").ToList();

                MessageBox.Show(
                    "Rebuild complete." + changeSummary + "\n\n" +
                    "Backups of your original archives were saved inside the corresponding folders within the game directory " +
                    "(each original .img renamed to .img.bak):\n\n" + string.Join("\n", backupNames),
                    "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show("Rebuild complete." + changeSummary, "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Rebuild failed: {ex.Message}", "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            RebuildButton.Enabled = true;
        }
    }

    // ================= TAB 4: Install Mod(s) without extraction =================

    private void OnBrowseDirectGameFolder(object? sender, EventArgs e)
    {
        var folder = BrowseForFolder("Select your GTA San Andreas game folder");
        if (folder is null) return;

        if (!GameScanner.LooksLikeSanAndreasInstall(folder))
        {
            var proceed = MessageBox.Show(
                "This folder doesn't look like a San Andreas PC install (no gta_sa.exe / models\\gta3.img found). " +
                "Use it anyway?",
                "SAFT", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (proceed != DialogResult.Yes) return;
        }

        ShareGameFolder(folder);
        UpdateDirectInstallButtonEnabled();
    }

    private void OnBrowseDirectModFolder(object? sender, EventArgs e)
    {
        var folder = BrowseForFolder("Select the folder containing the mod's files");
        if (folder is not null) DirectModFolderBox.Text = folder;

        UpdateDirectInstallButtonEnabled();
    }

    private void UpdateDirectInstallButtonEnabled()
    {
        DirectInstallButton.Enabled =
            !string.IsNullOrWhiteSpace(DirectGameFolderBox.Text) && !string.IsNullOrWhiteSpace(DirectModFolderBox.Text);
    }

    private void OnBrowseDirectBackupDest(object? sender, EventArgs e)
    {
        var folder = BrowseForFolder("Select where to save backups of replaced files");
        if (folder is not null) DirectBackupDestBox.Text = folder;
    }

    private async void OnDirectInstall(object? sender, EventArgs e)
    {
        var gameFolder = DirectGameFolderBox.Text;
        var modFolder = DirectModFolderBox.Text;

        if (string.IsNullOrWhiteSpace(gameFolder) || string.IsNullOrWhiteSpace(modFolder))
        {
            MessageBox.Show("Pick both a game folder and a mod folder first.", "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(DirectBackupDestBox.Text))
        {
            MessageBox.Show(
                "Pick a backup folder first. Every original file is backed up before it is replaced, " +
                "and that backup is what the Uninstall tab restores from.",
                "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Refused up front rather than discovered later. Backing up into the mod folder quietly
        // doubles every file in it and there is no sign anything is wrong until an install reports
        // twice as many replacements as the mod contains.
        if (FolderAccess.WhyBackupFolderIsUnusable(DirectBackupDestBox.Text, gameFolder, modFolder) is { } why)
        {
            ActivityLog.Note($"install: refused backup folder - {why}");
            MessageBox.Show(why, "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        DirectProgressBar.Value = 0;
        DirectSubProgressBar.Value = 0;
        DirectInstallButton.Enabled = false;

        // The checks before the first popup used to move nothing but a line of grey text, which on a
        // slow device reads as a window that has locked up. They are a known, fixed number of steps,
        // so they can be shown as steps — a filling bar rather than a still one. Deliberately the
        // ordinary determinate bar already used everywhere else in the app, not a marquee: a marquee
        // is a different comctl32 style and this is not the place to find out how Wine draws it.
        const int analysisSteps = 4;
        var analysisStep = 0;
        void Analysing(string what)
        {
            DirectSubProgressBar.Maximum = analysisSteps;
            DirectSubProgressBar.Value = Math.Clamp(analysisStep, 0, analysisSteps);
            DirectSubProgressText.Text = what;
            analysisStep++;
        }

        Analysing("Checking mod files against the live game…");

        try
        {
            ActivityLog.Note($"install: planning against {modFolder}");

            // The game folder is listed ONCE for this whole install and shared by every step below.
            // It was being walked recursively eight times per install - the archive search alone did
            // it three times - and two installs have now stopped dead inside one of those walks under
            // Winlator, where each one goes through Wine's filesystem layer onto an SD card. See
            // GameFiles.
            var gameListing = await Task.Run(() => GameFiles.Walk(gameFolder, ActivityLog.Note));

            // The MOD folder, walked once, for the same reason the game folder is. Five separate
            // recursive walks of it went into one install: the plan, the streaming-content check, the
            // addition scan, the weighing of the mod's own files, and a reinstall's second scan. SAFT
            // HUNG inside that last one on a real device - app alive, window unclosable - and it is the
            // third hang to happen inside a recursive directory enumeration. A mod folder is small, but
            // "small" is not the point: every one of these goes through Wine's filesystem translation
            // onto an SD card, and that is the thing that stops responding.
            var modListing = await Task.Run(() => GameFiles.Walk(modFolder, ActivityLog.Note));

            var plan = await Task.Run(() => DirectModInstaller.Plan(gameFolder, modFolder, ActivityLog.Note, gameListing, modListing));
            ActivityLog.Note($"install: plan has {plan.Matches.Count} archived + {plan.UnarchivedMatches.Count} loose match(es)");

            WarnAboutRefusedScripts(plan.RefusedScripts);

            // Whether any of the analysis below is worth doing at all. A mod made only of sounds,
            // music or loose data files cannot change a placement, a model weight or an object ID,
            // so reading the map to find that out is pure cost — and it is the cost that was
            // killing audio installs on a real device. See ModContent.AffectsStreaming.
            var affectsStreaming = await Task.Run(() => ModContent.AffectsStreaming(modFolder, modListing));

            AdditionPlan? additions = null;
            StreamingVerdict verdict;

            // Hoisted out of the block below so the reinstall path can reuse them instead of asking
            // the game the same questions twice. See where they are used again.
            HashSet<string>? existingNames = null;
            GameDensityBaseline? gameBaseline = null;
            IReadOnlySet<int>? gameUsedIds = null;
            // Kept alongside the ids and the baseline for the same reason they are: the reinstall
            // scan below needs to know which objects the game already defines, and re-reading every
            // .ide to find out is the duplicated work that made SAFT hang on a busy SD card.
            IReadOnlyList<IdeDefinition>? gameDefinitions = null;

            if (!affectsStreaming)
            {
                ActivityLog.Note("install: mod has no models, textures or map data; skipping the map read and weight checks");
                verdict = StreamingAdvice.ComposeWithoutStreamingContent();
            }
            else
            {
                // Everything the game already has, so the scanner can tell an addition from a replacement.
                Analysing("Checking what your game already has…");
                var existing = await Task.Run(() => GameFileNames(gameFolder, gameListing));
                existingNames = existing;

                // The game's own map, read ONCE and handed to both the addition scanner and the streaming
                // measurement. They each used to read it independently — all 60 .ide files, all 54 text
                // .ipl files and every binary .ipl inside the archives — for the same answer, at about
                // 85 MB of allocation each. In a 32-bit process whose Large Object Heap is never
                // compacted, that second pass was allocating into the holes left by the first, and the
                // crash that followed was the allocator failing to find contiguous room rather than the
                // machine running out of memory. See GameSnapshot.
                var gameMap = await Task.Run(() => GameSnapshot.Read(gameFolder, ActivityLog.Note, gameListing));
                ActivityLog.Note($"install: game holds {existing.Count} known file name(s); scanning for additions");

                Analysing("Checking what this mod adds…");
                // Pulled out of the argument list and logged either side. As an inline argument it ran
                // inside the same lambda as the scan, so a failure in it was indistinguishable from a
                // failure in the scan - and this exact gap is where two runs have now died.
                ActivityLog.Note("install: collecting the object ids already in use");
                var usedIds = await Task.Run(() => ObjectIdAllocator.UsedIdsFrom(gameMap.Definitions));
                gameUsedIds = usedIds;
                gameBaseline = gameMap.Baseline;
                gameDefinitions = gameMap.Definitions;

                ActivityLog.Note($"install: {usedIds.Count:N0} object id(s) in use; scanning the mod folder for additions");
                additions = await Task.Run(() => AdditionScanner.Scan(
                    gameFolder, modFolder, existing.Contains, gameMap.Baseline, usedIds, ActivityLog.Note, modListing,
                    gameDefinitions: gameMap.Definitions));

                // What the mod does to the streaming budget — this applies to replacement-only mods too,
                // which is where an over-heavy pack quietly stops the world rendering.
                Analysing("Checking how much this mod adds to what your game has to load…");
                var replacementSizes = await Task.Run(() => ReplacementSizes(plan));
                ActivityLog.Note("install: measuring streaming impact");
                var impact = await Task.Run(() => StreamingImpact.Measure(gameFolder, replacementSizes, ActivityLog.Note, gameMap));

                // The baseline goes in whether or not this mod adds anything: it describes the game being
                // installed into, which matters just as much for a pure replacement.
                verdict = StreamingAdvice.Compose(
                    additions.HasAdditions ? additions.Density : null, impact, additions.Density.Baseline);
            }

            // Full, so the last step reads as finished rather than as stopped three quarters of the
            // way along while the popup is being built.
            DirectSubProgressBar.Value = analysisSteps;
            DirectSubProgressText.Text = "Checks complete.";
            ActivityLog.Note($"install: verdict {verdict.Severity}, within range {verdict.WithinRange}");

            if (!ConfirmStreamingImpact(verdict))
            {
                DirectSubProgressText.Text = "Cancelled.";
                return;
            }

            var installAdditions = false;
            if (additions is not null && additions.HasAdditions)
            {
                // Asked first, because every prompt below it assumes the answer. They are all about
                // adding these files properly, and none of them are any use to someone whose real
                // problem is that they meant to replace something and misnamed it.
                if (additions.NewAssets.Count > 0 && !ConfirmAdditionsAreIntentional(plan, additions))
                {
                    DirectSubProgressText.Text = "Cancelled.";
                    return;
                }

                // Checked before anything else after that, because it's the only one that isn't a
                // judgement call: placing an object with no collision crashes the game on load,
                // every time.
                if (additions.PlacesModelsWithoutCollision)
                {
                    if (!ConfirmSkippingAdditionsWithoutCollision(gameFolder, modFolder, additions))
                    {
                        DirectSubProgressText.Text = "Cancelled.";
                        return;
                    }
                }
                else if (additions.LacksPlacementData)
                {
                    if (!ConfirmAdditionsWithoutPlacementData(gameFolder, modFolder, additions))
                    {
                        DirectSubProgressText.Text = "Cancelled.";
                        return;
                    }
                }
                else if (!additions.FitsInAvailableSlots)
                {
                    if (!ConfirmAdditionsThatDoNotFit(additions))
                    {
                        DirectSubProgressText.Text = "Cancelled.";
                        return;
                    }
                }
                else
                {
                    installAdditions = ConfirmAdditions(additions);
                }
            }

            // Installing a mod that is already installed removes the old copy first rather than
            // layering a second one on top. Without that, the same models would be added again under
            // fresh object ids, leaving duplicate assets, duplicate map lines and two identically
            // named records that the Uninstall tab can't tell apart.
            //
            // Named either side because this was a silent gap in the log, and a run died inside it:
            // the last line written was the previous popup closing, and the next line would have been
            // the reinstall popup being built. Between them is only a manifest read, but "only" is
            // what the previous five unlogged gaps looked like too.
            var modName = Path.GetFileName(modFolder.TrimEnd(Path.DirectorySeparatorChar));
            ActivityLog.Note($"install: checking whether '{modName}' is already installed");
            // Read on a background thread: this is a file read, and it sat on the UI thread until two
            // separate crashes landed on this exact line.
            var backupFolderForLookup = DirectBackupDestBox.Text;
            var found = installAdditions
                ? await Task.Run(() => FindInstalledMod(backupFolderForLookup, modName))
                : (Installed: null, Record: (AdditionsManifest?)null);

            var alreadyInstalled = found.Installed;
            var installedRecord = found.Record;
            ActivityLog.Note(alreadyInstalled is null
                ? "install: not already installed"
                : $"install: already installed - {alreadyInstalled.ObjectIds.Count} object(s); asking about reinstalling");

            if (alreadyInstalled is not null && !ConfirmReinstall(alreadyInstalled))
            {
                installAdditions = false;
                alreadyInstalled = null;
            }

            if (plan.Matches.Count == 0 && plan.AudioMatches.Count == 0 && plan.StreamMatches.Count == 0
                && plan.UnarchivedMatches.Count == 0 && !installAdditions)
            {
                DirectSubProgressText.Text = "Done.";
                var unmatchedCount = plan.Unmatched.Count + plan.AudioUnmatched.Count + plan.StreamUnmatched.Count;
                MessageBox.Show(
                    "No mod files matched anything in your current install. Nothing was changed." +
                    (unmatchedCount > 0 ? $" ({unmatchedCount} file(s) didn't match anything.)" : ""),
                    "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (plan.AnyArchiveNeedsRebuild)
            {
                // Name the specific archive(s) that actually need rebuilding — only fall back to
                // talking about "your game" in the rare case literally every archive is affected.
                var rebuildTargets = plan.ArchivesNeedingRebuild.Select(Path.GetFileName).ToList();
                var targetDescription = rebuildTargets.Count >= plan.TotalArchivesInGame
                    ? "your game"
                    : string.Join(", ", rebuildTargets);

                using var confirm = new ConfirmDialog(
                    $"your mod files are larger in size than the matching original files in {targetDescription}, " +
                    $"the mod installer will have to rebuild {targetDescription} in order for them to load properly in game, " +
                    $"allow to rebuild {targetDescription}?",
                    $"yes, rebuild {targetDescription} to accommodate larger mod files",
                    "no, cancel this mod installation");
                confirm.ShowDialog(this);
                if (!confirm.Result)
                {
                    DirectSubProgressText.Text = "Cancelled.";
                    return;
                }
            }

            var audioToApply = plan.AudioMatchesThatFit;
            var streamsToApply = plan.StreamMatchesThatFit;
            var groupCountForProgress = plan.Matches.Select(m => m.ArchiveRelativePath).Distinct().Count()
                + (audioToApply.Count > 0 ? 1 : 0) + (streamsToApply.Count > 0 ? 1 : 0)
                + (plan.UnarchivedMatches.Count > 0 ? 1 : 0);
            var progress = new Progress<DirectInstallProgress>(p =>
            {
                SetScaledProgress(DirectProgressBar, p.ArchiveIndex, Math.Max(groupCountForProgress, p.ArchiveCount), p.FilesDone, p.FilesTotal);

                DirectSubProgressBar.Maximum = Math.Max(1, p.FilesTotal);
                DirectSubProgressBar.Value = Math.Clamp(p.FilesDone, 0, DirectSubProgressBar.Maximum);
                DirectSubProgressText.Text =
                    $"[{p.ArchiveIndex}/{p.ArchiveCount}] {p.CurrentArchive}: {p.Stage} — file {p.FilesDone:N0} of {p.FilesTotal:N0}";
            });

            var backupFolder = DirectBackupDestBox.Text;

            // Before anything is written. Backing the originals up into the mod folder itself would
            // file them alongside the mod's own files, where the next scan reads them back as things
            // to install.
            DirectModInstaller.EnsureBackupFolderIsSeparate(modFolder, backupFolder);

            // Adding assets rewrites the target archive in full, and so does replacing a file that no
            // longer fits. Doing both meant writing models\gta3.img - 940 MB - out twice in a single
            // install; the first pass took 36.8 seconds and the process was killed during the second.
            // When both are happening to the same archive, the replacements are handed to the
            // additions rewrite so the archive is written exactly once. Originals are still backed up
            // by the pass below before anything is deferred.
            //
            // A reinstall counts as "both happening" even when the mod adds nothing new: the additions
            // pass has to rewrite that archive anyway to take the previously installed copy back out,
            // so a replacement can ride along with it for free.
            var foldIntoAdditions = installAdditions && additions is not null
                                    && (alreadyInstalled is not null
                                        || additions.NewAssets.Any(a =>
                                            a.FileName.EndsWith(".dff", StringComparison.OrdinalIgnoreCase)
                                            || a.FileName.EndsWith(".txd", StringComparison.OrdinalIgnoreCase)));

            // Worked out BEFORE the fold, not just before the write.
            //
            // The mod's own additions are not replacements, and the fold below reads the replacement
            // plan to decide what to carry into the additions rewrite. Filtering only at the Apply
            // call left the fold reading the UNFILTERED plan, so an asset the mod defines was handed
            // over as a replacement and installed as an addition both - and in the rewrite loop a
            // replacement is checked before a removal, so the old copy was kept and the new one
            // appended beside it. Seven leftovers came out as fourteen entries under seven names.
            var toApply = installAdditions && additions is not null ? plan.Without(additions.AssetFileNames) : plan;
            if (toApply.Matches.Count != plan.Matches.Count)
                ActivityLog.Note($"install: {plan.Matches.Count - toApply.Matches.Count} file(s) the mod defines itself left to the addition installer");

            var deferredArchive = AdditionInstaller.DefaultArchiveRelativePath;
            var deferredReplacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            IReadOnlySet<string>? deferRebuildsFor = null;

            // The other half of the reinstall fold: what the removal below would have rewritten the
            // archive to take out, carried to the additions rewrite instead.
            var deferredDrops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deferredPrunes = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);

            if (foldIntoAdditions)
            {
                foreach (var m in toApply.Matches.Where(m =>
                             m.ArchiveRelativePath.Equals(deferredArchive, StringComparison.OrdinalIgnoreCase)))
                    deferredReplacements[m.EntryName] = m.ModFilePath;

                if (deferredReplacements.Count > 0)
                {
                    deferRebuildsFor = new HashSet<string>(new[] { deferredArchive }, StringComparer.OrdinalIgnoreCase);
                    ActivityLog.Note(
                        $"install: folding {deferredReplacements.Count} replacement(s) for {deferredArchive} " +
                        "into the additions rewrite (one rebuild instead of two)");
                }
            }

            // One watcher for the whole install, handed to everything that writes an archive. It reads
            // the byte count the write loop already keeps and times it; it never changes what is
            // written or in what order. The point is to be able to tell the user that a crawl was
            // their SD card catching its breath rather than SAFT having stopped - measured on a real
            // device going from 30 seconds an archive to 183 for the same 940 MB.
            var speed = new StorageSpeed();

            // The mod's own additions are not replacements, so the replacement pass gives them up.
            //
            // The two passes ask different questions of the same folder: "does the game already have
            // a file by this name" (which a modded game answers yes to, for the mod's own leftovers)
            // versus "does the mod define this in its .ide" (which only the mod can answer). The
            // second is the truthful one. Without this, an asset left behind by an earlier round gets
            // backed up as though the copy in the game were stock - which is how a backup folder came
            // to hold the modpack's own files under the name of the originals.
            var result = await Task.Run(() => DirectModInstaller.Apply(toApply, backupFolder, progress, ActivityLog.Note, deferRebuildsFor, speed));

            AdditionInstallResult? added = null;
            if (installAdditions && additions is not null)
            {
                // Everything from here to the summary was unlogged, and it is not cheap: adding
                // assets rewrites the WHOLE archive they go into, so a mod that both replaces
                // oversized files and adds new ones rebuilds models\gta3.img twice in one install -
                // 940 MB out to the card, twice. An install died in this gap with the log's last
                // word being "apply: finished".
                ActivityLog.Note($"additions: installing {additions.NewAssets.Count} new asset(s), " +
                                 $"{additions.Definitions.Count} definition(s), {additions.Placements.Count} placement(s)");
                DirectSubProgressText.Text = "Adding new objects…";
                var additionProgress = new Progress<AdditionProgress>(p =>
                {
                    DirectSubProgressText.Text = $"{p.Stage} — {p.FilesDone:N0} of {p.FilesTotal:N0}";
                    DirectSubProgressBar.Maximum = Math.Max(1, p.FilesTotal);
                    DirectSubProgressBar.Value = Math.Clamp(p.FilesDone, 0, DirectSubProgressBar.Maximum);
                });

                // Removing the previous copy first is what keeps installing idempotent: whatever
                // happens, this mod ends up present exactly once, with one record.
                if (alreadyInstalled is not null && backupFolder is not null)
                {
                    // Reinstalling used to rewrite the archive TWICE more: once here to drop the old
                    // copy's entries, once below to add the new ones - 940 MB out to the card each
                    // time, for a job that changes a handful of entries. The removal now does
                    // everything EXCEPT that rewrite and hands back what it would have taken out, so
                    // the rewrite below is the only one. Named per step so a run that crawls can be
                    // told apart from one that has stopped - on a card whose sustained write speed
                    // has collapsed those look identical from the outside.
                    ActivityLog.Note("reinstall: removing the previously installed copy (archive rewrite deferred)");
                    DirectSubProgressText.Text = "Removing the previously installed copy…";
                    // Reusing the record read a moment ago when the reinstall was confirmed, rather
                    // than reading the same file off the card again. Nothing between the two touches
                    // it: the direct pass writes archives, audio and loose game files, never this.
                    var priorManifest = installedRecord ?? await Task.Run(() => AdditionsManifest.Load(backupFolder));
                    ActivityLog.Note(priorManifest is null
                        ? "reinstall: no record found, nothing to remove"
                        : $"reinstall: record has {priorManifest.Mods.Count} mod(s) in it" +
                          (installedRecord is not null ? " (reusing the one already read)" : ""));
                    if (priorManifest is not null)
                    {
                        var removal = await Task.Run(() => AdditionUninstaller.Remove(
                            gameFolder, priorManifest, new[] { modName }, additionProgress,
                            new HashSet<string>(new[] { deferredArchive }, StringComparer.OrdinalIgnoreCase),
                            ActivityLog.Note, speed));

                        // The record is deliberately NOT saved here. The old copy's entries are still
                        // in the archive at this point - their removal is riding along with the
                        // rewrite below - and writing "this mod is gone" before it is gone would, if
                        // the process died in between, leave those entries in the archive with
                        // nothing recording them: assets SAFT could never remove again. SAFT only
                        // installs what it can uninstall. The save below rewrites this record anyway
                        // once the rewrite has actually happened, and until then the worst case is a
                        // record that still claims map lines already deleted, which uninstall
                        // already reports and works around rather than choking on.
                        if (removal.DeferredEntryRemovals.TryGetValue(deferredArchive, out var dropped))
                            deferredDrops.UnionWith(dropped);
                        foreach (var bundle in removal.DeferredCollisionPrunes)
                            deferredPrunes[bundle.Key] = bundle.Value;

                        foreach (var note in removal.Skipped)
                            ActivityLog.Note($"reinstall: left alone - {note}");
                        ActivityLog.Note(
                            $"reinstall: previous copy's map data removed; {deferredDrops.Count} archive entry/entries " +
                            $"and {deferredPrunes.Values.Sum(r => r.Count)} collision record(s) handed to the rewrite below");
                    }

                    // The old copy's assets and ids are gone, so what counts as "new" has changed.
                    // Its ENTRIES are still physically in the archive - their removal is riding along
                    // with the rewrite below - so they are struck off the list of what the game has,
                    // or the rescan would call this mod's own assets replacements of themselves.
                    //
                    // The list itself is REUSED rather than rebuilt. Rebuilding it meant walking the
                    // game folder again and reopening all eight archives to read out their directory
                    // tables - a second full pass over models\gta3.img among them - for an answer that
                    // cannot have changed: everything between then and now either replaced a file's
                    // contents or is still waiting to happen, and neither adds or removes a NAME.
                    // A run died a few milliseconds into this stretch, and this is the heaviest thing
                    // in it. If the list was never built (a mod with no streaming content), there is
                    // nothing to reuse and it is built here as before.
                    DirectSubProgressText.Text = "Rechecking what this mod adds…";
                    var names = existingNames is not null
                        ? new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase)
                        : await Task.Run(() => GameFileNames(gameFolder));
                    ActivityLog.Note(existingNames is not null
                        ? $"reinstall: reusing the {names.Count:N0} known file name(s) from the checks"
                        : "reinstall: rereading what the game has");
                    names.ExceptWith(deferredDrops);
                    // The listing from the top of the install, not a fifth walk of the mod folder.
                    // This is the exact call SAFT hung inside: same files, same folder, a different
                    // answer to "is this already in the game" — and the files were never the part
                    // that had to be re-read.
                    // The baseline and the used object ids come from the checks too, for the same
                    // reason the name list does: they were worked out minutes ago and nothing since
                    // has changed them. Recomputing them read every .ide in the game for the ids and
                    // then the WHOLE MAP again for the baseline - measured at 6 and 27 seconds on a
                    // real device, 33 seconds of a reinstall spent answering questions it had already
                    // answered. That is what "Rechecking what this mod adds" was doing all that time.
                    //
                    // The ids are a moment stale - the copy being removed frees four of them - which
                    // makes the free-slot figure four lower than the truth. Erring low is the safe
                    // direction for a number whose only job is to warn when slots are running out.
                    additions = await Task.Run(() => AdditionScanner.Scan(
                        gameFolder, modFolder, names.Contains,
                        baseline: gameBaseline, usedObjectIds: gameUsedIds,
                        onStep: ActivityLog.Note, modFiles: modListing,
                        gameDefinitions: gameDefinitions));

                    // A name can be on both lists: coming out with the old copy, and going back in
                    // from the mod folder. Exactly one route must put it back, or the archive ends up
                    // with the entry twice - or, worse, not at all. The rescan above decides. A name
                    // it calls a new asset is appended fresh and recorded in the manifest, which is
                    // the route that keeps the mod uninstallable afterwards, so the handed-over
                    // replacement for that name would only duplicate it and is dropped. The other
                    // branch is a guard: a name the rescan does NOT claim has no route back in except
                    // its replacement, so that entry stays rather than being deleted.
                    var readdedByAdditions = new HashSet<string>(
                        additions.NewAssets.Select(a => a.FileName), StringComparer.OrdinalIgnoreCase);
                    foreach (var name in deferredDrops.ToList())
                    {
                        if (readdedByAdditions.Contains(name)) deferredReplacements.Remove(name);
                        else if (deferredReplacements.ContainsKey(name)) deferredDrops.Remove(name);
                    }
                }

                ActivityLog.Note(
                    $"additions: rewriting the archive once - new assets plus {deferredReplacements.Count} folded " +
                    $"replacement(s) and {deferredDrops.Count} folded removal(s)");
                added = await Task.Run(() => AdditionInstaller.Apply(
                    gameFolder, additions, modName, additionProgress, deferredReplacements,
                    deferredDrops, deferredPrunes, speed));
                ActivityLog.Note($"additions: installed - {added.Recorded.ArchiveEntries.Count} archive entry/entries, " +
                                 $"{added.Problems.Count} problem(s)");

                // The record of what was added lives in the backup folder, alongside the originals of
                // anything replaced — an added asset has no vanilla counterpart, so without this the
                // uninstall tab would have no way to know the addition ever happened.
                //
                // On a reinstall this is also where the REMOVAL is finally written down, which is why
                // it waits until the rewrite above has actually happened: it loads the record fresh
                // off disk, drops the old copy's entry by name, and puts the new one in its place.
                if (backupFolder is not null)
                {
                    // OFF THE UI THREAD, like every other file operation in this app. This one was
                    // not, and two runs in a row died in the gap it left in the log - along with
                    // three earlier crashes, every one of them at a manifest read or write with
                    // nothing else in between. Android decides an app is unresponsive when its main
                    // thread stops answering, and under Winlator SAFT's UI thread is what keeps that
                    // loop turning: blocking it on a file read is how "Winlator is not responsive"
                    // happens. The work is identical; only the thread it runs on has changed.
                    ActivityLog.Note("install: writing down what was added");
                    await Task.Run(() =>
                    {
                        var manifest = AdditionsManifest.Load(backupFolder) ?? new AdditionsManifest { GameRootPath = gameFolder };

                        // Belt and braces: if a record under this name somehow survived, replace it
                        // rather than sitting alongside it.
                        manifest.Mods.RemoveAll(m => m.Name.Equals(modName, StringComparison.OrdinalIgnoreCase));
                        manifest.Mods.Add(added.Recorded);
                        manifest.Save(backupFolder);
                    });
                    ActivityLog.Note("install: record written");
                }
            }

            DirectSubProgressText.Text = "Done.";
            ActivityLog.Note($"install: {speed.Describe()}");
            ActivityLog.Census("after installing");

            var filesReplaced = result.Archives.Sum(s => s.FilesReplaced);
            var tooLargeCount = plan.AudioMatchesTooLarge.Count + plan.StreamMatchesTooLarge.Count;
            var failedCount = result.AudioFailed.Count + result.StreamFailed.Count;
            // Files the addition path handled are not "unmatched" — nor are the .ide/.ipl snippets,
            // which are instructions rather than assets. Counting them made a fully successful
            // install report every file in the mod folder as having failed.
            var consumedByAdditions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (additions is not null)
            {
                foreach (var asset in additions.NewAssets) consumedByAdditions.Add(asset.FileName);
            }

            var unmatchedFileCount =
                plan.Unmatched.Count(n =>
                    !consumedByAdditions.Contains(n)
                    && !n.EndsWith(".ide", StringComparison.OrdinalIgnoreCase)
                    && !n.EndsWith(".ipl", StringComparison.OrdinalIgnoreCase))
                + plan.AudioUnmatched.Count + plan.StreamUnmatched.Count;

            var summaryLines = new List<string>
            {
                $"Mod installation complete. {filesReplaced} file(s) replaced across {result.Archives.Count} archive(s)."
            };
            if (result.Unarchived.Count > 0) summaryLines.Add($"{result.Unarchived.Count} game file(s) replaced outside the archives (map data, etc).");
            if (added is not null)
            {
                summaryLines.Add(
                    $"{added.Recorded.ObjectIds.Count} new object(s) added, using object slot(s) " +
                    $"{string.Join(", ", added.Recorded.ObjectIds)}.");

                if (added.Recorded.Collisions.Count > 0)
                    summaryLines.Add($"Collision installed for {added.Recorded.Collisions.Count} model(s).");

                // Not a problem — an empty collision record is how you deliberately make something
                // you can walk through — but worth saying, since it's also what an accidentally
                // emptied record looks like.
                if (additions is { WalkThroughModels.Count: > 0 } a)
                    summaryLines.Add(
                        $"{a.WalkThroughModels.Count} object(s) will be walk-through, because their collision " +
                        $"has no shape in it: {string.Join(", ", a.WalkThroughModels.Take(6))}.");

                foreach (var problem in added.Problems) summaryLines.Add(problem);
            }
            if (result.Audio.Count > 0) summaryLines.Add($"{result.Audio.Count} audio file(s) patched.");
            if (result.Streams.Count > 0) summaryLines.Add($"{result.Streams.Count} streamed track(s) patched.");
            if (result.UnarchivedFailed.Count > 0) summaryLines.Add($"{result.UnarchivedFailed.Count} game file(s) could not be written and were skipped.");
            if (plan.Ambiguous.Count > 0)
                summaryLines.Add(
                    $"{plan.Ambiguous.Count} file(s) exist both inside an archive and loose in your game folder, with different contents — " +
                    "the archived copy was replaced and the loose copy was left alone, since SAFT can't tell which one your mod meant.");
            if (tooLargeCount > 0) summaryLines.Add($"{tooLargeCount} audio file(s) were too large to replace and were skipped.");
            if (failedCount > 0) summaryLines.Add($"{failedCount} audio file(s) failed to read and were skipped.");
            if (unmatchedFileCount > 0) summaryLines.Add($"{unmatchedFileCount} file(s) didn't match anything in your game and were left unplaced.");

            // Only claim backups were made if something was actually replaced. An addition has no
            // original to back up, so saying "originals were backed up" after a pure addition sends
            // the user looking for files that were never supposed to exist.
            var replacedAnything = filesReplaced > 0 || result.Unarchived.Count > 0
                || result.Audio.Count > 0 || result.Streams.Count > 0;

            if (replacedAnything)
            {
                summaryLines.Add($"Originals of every replaced file were backed up to: {backupFolder}");

                // Naming the file matters: it's the one thing the Uninstall tab cannot work without,
                // and it's easy to tidy away without realising what it was for.
                if (added is not null)
                    summaryLines.Add(
                        $"The record of what was added is {AdditionsManifest.FileName}, in that same folder. " +
                        "The Uninstall tab needs that file to remove them again, so keep it with the backups.");
            }
            else if (added is not null)
                summaryLines.Add(
                    $"Nothing needed backing up — added objects have no original to replace. The record of " +
                    $"what was added is {AdditionsManifest.FileName}, in {backupFolder}. The Uninstall tab " +
                    "needs that file to remove them again, so keep it there.");

            // SAFT's own dialog, not a MessageBox. Two reasons, one already proven on a real screen:
            // a MessageBox is sized by Windows, and this summary grows a line per problem, so at
            // 960x544 it has already pushed its own OK button off the bottom of the display where it
            // could not be pressed. The second is that a MessageBox is a native window built by Wine,
            // and this app's history is a long list of native window work behaving differently there.
            // ConfirmDialog measures against the real screen, scrolls rather than overflowing, logs
            // itself, and is disposed.
            using (var summary = ConfirmDialog.Note(string.Join("\n", summaryLines), "OK"))
                summary.ShowDialog(this);
            ReportSlowStorage(speed);

            HoldBeforeLaunching();
        }
        catch (Exception ex)
        {
            ReportFailure(ex, "Install");
        }
        finally
        {
            DirectInstallButton.Enabled = true;
        }
    }

    // ================= TAB 5: Uninstall Mod(s) =================

    private void OnBrowseUninstallGameFolder(object? sender, EventArgs e)
    {
        var folder = BrowseForFolder("Select your GTA San Andreas game folder");
        if (folder is null) return;

        if (!GameScanner.LooksLikeSanAndreasInstall(folder))
        {
            var proceed = MessageBox.Show(
                "This folder doesn't look like a San Andreas PC install (no gta_sa.exe / models\\gta3.img found). " +
                "Use it anyway?",
                "SAFT", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (proceed != DialogResult.Yes) return;
        }

        ShareGameFolder(folder);
        UpdateUninstallButtonEnabled();
    }

    private void OnBrowseUninstallBackupFolder(object? sender, EventArgs e)
    {
        var folder = BrowseForFolder("Select the folder containing your backed-up vanilla game files");
        if (folder is not null) UninstallBackupFolderBox.Text = folder;

        UpdateUninstallButtonEnabled();
    }

    private void UpdateUninstallButtonEnabled()
    {
        UninstallButton.Enabled =
            !string.IsNullOrWhiteSpace(UninstallGameFolderBox.Text) && !string.IsNullOrWhiteSpace(UninstallBackupFolderBox.Text);
    }

    private void OnUninstallBackupModsOptionChanged(object? sender, EventArgs e)
    {
        if (!_uiReady) return;

        UninstallBackupDestRow.Visible = UninstallBackupModsCheckBox.Checked;
    }

    private void OnBrowseUninstallBackupDest(object? sender, EventArgs e)
    {
        var folder = BrowseForFolder("Select where to save backups of your current mod files");
        if (folder is not null) UninstallBackupDestBox.Text = folder;
    }

    private async void OnUninstall(object? sender, EventArgs e)
    {
        var gameFolder = UninstallGameFolderBox.Text;
        var backupFolder = UninstallBackupFolderBox.Text;

        if (string.IsNullOrWhiteSpace(gameFolder) || string.IsNullOrWhiteSpace(backupFolder))
        {
            MessageBox.Show("Pick both a game folder and a backup folder first.", "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var makeModBackup = UninstallBackupModsCheckBox.Checked;
        if (makeModBackup && string.IsNullOrWhiteSpace(UninstallBackupDestBox.Text))
        {
            MessageBox.Show("Pick a folder to back up your current mods to, or uncheck that option.", "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Before anything is read or written: does this backup folder's record belong to this game?
        //
        // The two folders are picked separately, so one game's backup folder can be pointed at
        // another game. The uninstall then finds nothing of the record to remove while the game those
        // assets are really in keeps them - which is the most likely account of how seven assets came
        // to be orphaned. A warning rather than a refusal: a second copy of the same game is an
        // ordinary thing to have, and SAFT cannot tell that apart from a slip.
        var recordedRoot = AdditionsManifest.Load(backupFolder)?.GameRootPath;
        if (GameFolderCheck.LooksLikeADifferentGame(recordedRoot, gameFolder))
        {
            ActivityLog.Note($"uninstall: record was written for {recordedRoot}, this game is {gameFolder}");
            using var mismatch = new ConfirmDialog(
                GameFolderCheck.Warning(recordedRoot),
                "Uninstall anyway",
                "Cancel",
                StreamingSeverity.Caution);
            mismatch.ShowDialog(this);
            if (!mismatch.Result)
            {
                ActivityLog.Note("uninstall: cancelled at the folder mismatch warning");
                return;
            }

            ActivityLog.Note("uninstall: user chose to continue past the folder mismatch warning");
        }

        UninstallProgressBar.Value = 0;
        UninstallSubProgressBar.Value = 0;
        UninstallSubProgressText.Text = "Checking backup files against the live game…";
        UninstallButton.Enabled = false;

        try
        {
            // Breadcrumbs, at the same granularity as the install path. Uninstall had none, so when
            // it took the process down there was nothing in the log between "opening main window"
            // and the next session start - no way to tell which step died. The install path's
            // breadcrumbs are the only reason the peds.ide crash was found in one attempt.
            ActivityLog.Note($"uninstall: planning against backup folder {backupFolder}");
            var plan = await Task.Run(() => DirectModInstaller.Plan(gameFolder, backupFolder, ActivityLog.Note));
            ActivityLog.Note(
                $"uninstall: plan has {plan.Matches.Count} archived + {plan.UnarchivedMatches.Count} loose + " +
                $"{plan.AudioMatches.Count} audio + {plan.StreamMatches.Count} stream match(es), " +
                $"{plan.Ambiguous.Count} ambiguous, rebuild={plan.AnyArchiveNeedsRebuild}");

            WarnAboutRefusedScripts(plan.RefusedScripts);

            // Additions have no vanilla counterpart, so nothing about them sits in the backup folder
            // as a file — the record SAFT wrote at install time is the only trace, which is exactly
            // why it lives here alongside the backed-up originals.
            var additionsManifest = await Task.Run(() => AdditionsManifest.Load(backupFolder));
            var modsToRemove = additionsManifest?.Mods.Select(m => m.Name).ToList() ?? new List<string>();
            ActivityLog.Note($"uninstall: additions manifest {(additionsManifest is null ? "absent" : "loaded")}, {modsToRemove.Count} mod(s) to remove");

            if (modsToRemove.Count > 0)
            {
                using var confirm = new ConfirmDialog(
                    $"This backup folder also records {modsToRemove.Count} mod(s) whose objects SAFT ADDED to " +
                    "your game:\n\n    " + string.Join("\n    ", modsToRemove) + "\n\n" +
                    "Uninstalling these will also require rebuilding the archives, because your mods added " +
                    "assets on top of the originals. This adds time to the uninstall. Continue?",
                    "Uninstall and rebuild",
                    "Not yet");
                confirm.ShowDialog(this);
                if (!confirm.Result)
                {
                    UninstallSubProgressText.Text = "Cancelled.";
                    return;
                }
            }

            if (plan.Matches.Count == 0 && plan.AudioMatches.Count == 0 && plan.StreamMatches.Count == 0
                && plan.UnarchivedMatches.Count == 0 && modsToRemove.Count == 0)
            {
                UninstallSubProgressText.Text = "Done.";
                MessageBox.Show("No backup files matched anything in your current install. Nothing was changed.", "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (plan.AnyArchiveNeedsRebuild)
            {
                // Name the specific archive(s) that actually need rebuilding — only fall back to
                // talking about "your game" in the rare case literally every archive is affected.
                var rebuildTargets = plan.ArchivesNeedingRebuild.Select(Path.GetFileName).ToList();
                var targetDescription = rebuildTargets.Count >= plan.TotalArchivesInGame
                    ? "your game"
                    : string.Join(", ", rebuildTargets);

                using var confirm = new ConfirmDialog(
                    $"your backup files are larger in size than the matching current files in {targetDescription}, " +
                    $"the uninstaller will have to rebuild {targetDescription} in order for them to load properly in game, " +
                    $"allow to rebuild {targetDescription}?",
                    $"yes, rebuild {targetDescription} to restore your vanilla files",
                    "no, cancel this uninstall");
                confirm.ShowDialog(this);
                if (!confirm.Result)
                {
                    UninstallSubProgressText.Text = "Cancelled.";
                    return;
                }
            }

            var audioToApply = plan.AudioMatchesThatFit;
            var streamsToApply = plan.StreamMatchesThatFit;
            var groupCountForProgress = plan.Matches.Select(m => m.ArchiveRelativePath).Distinct().Count()
                + (audioToApply.Count > 0 ? 1 : 0) + (streamsToApply.Count > 0 ? 1 : 0)
                + (plan.UnarchivedMatches.Count > 0 ? 1 : 0);
            var progress = new Progress<DirectInstallProgress>(p =>
            {
                SetScaledProgress(UninstallProgressBar, p.ArchiveIndex, Math.Max(groupCountForProgress, p.ArchiveCount), p.FilesDone, p.FilesTotal);

                UninstallSubProgressBar.Maximum = Math.Max(1, p.FilesTotal);
                UninstallSubProgressBar.Value = Math.Clamp(p.FilesDone, 0, UninstallSubProgressBar.Maximum);
                UninstallSubProgressText.Text =
                    $"[{p.ArchiveIndex}/{p.ArchiveCount}] {p.CurrentArchive}: {p.Stage} — file {p.FilesDone:N0} of {p.FilesTotal:N0}";
            });

            // Checked here rather than left to Apply, because the additions removal below writes to
            // the game. A refusal that waited for the restore would land on a half-uninstalled game.
            var modBackupFolderChoice = makeModBackup ? UninstallBackupDestBox.Text : null;
            DirectModInstaller.EnsureBackupFolderIsSeparate(backupFolder, modBackupFolderChoice);

            var speed = new StorageSpeed();
            // Additions come out FIRST, and this order is load-bearing rather than merely tidy.
            //
            // Restoring first meant the restore pass found SAFT's own added entries still sitting in
            // the archive, backed them up as though they were vanilla originals, and left the removal
            // to run afterwards - which emptied the record while the seven assets stayed in gta3.img.
            // The Android build has always done it this way round; this one had drifted away from the
            // order its own comment described.
            //
            // It is also the cheaper order: the removal rebuilds the archive, so doing it before the
            // ordinary restores keeps the two from rebuilding the same archive twice.
            AdditionRemovalResult? removed = null;
            if (modsToRemove.Count > 0 && additionsManifest is not null)
            {
                UninstallSubProgressText.Text = "Removing added objects…";
                var removalProgress = new Progress<AdditionProgress>(p =>
                {
                    UninstallSubProgressText.Text = $"{p.Stage} — {p.FilesDone:N0} of {p.FilesTotal:N0}";
                    UninstallSubProgressBar.Maximum = Math.Max(1, p.FilesTotal);
                    UninstallSubProgressBar.Value = Math.Clamp(p.FilesDone, 0, UninstallSubProgressBar.Maximum);
                });

                ActivityLog.Note($"uninstall: removing added objects for {modsToRemove.Count} mod(s)");
                removed = await Task.Run(() =>
                    AdditionUninstaller.Remove(
                        gameFolder, additionsManifest, modsToRemove, removalProgress,
                        deferRebuildsFor: null, onStep: ActivityLog.Note, speed: speed));
                ActivityLog.Note($"uninstall: removal finished - {removed.ArchiveEntriesRemoved} asset(s), {removed.DataLinesRemoved} map line(s), {removed.FreedObjectIds.Count} slot(s) freed");

                // The record is rewritten so a later uninstall doesn't try to remove all this again.
                ActivityLog.Note("uninstall: writing down what is left");
                await Task.Run(() => additionsManifest.Save(backupFolder));
                ActivityLog.Note("uninstall: record written");
            }

            var modBackupFolder = modBackupFolderChoice;
            ActivityLog.Note($"uninstall: restoring, mod backup {(modBackupFolder is null ? "off" : "to " + modBackupFolder)}");
            var result = await Task.Run(() => DirectModInstaller.Apply(
                plan, modBackupFolder, progress, ActivityLog.Note, deferRebuildsFor: null, speed: speed));
            ActivityLog.Note($"uninstall: restore finished - {result.Archives.Sum(s => s.FilesReplaced)} entry/entries across {result.Archives.Count} archive(s), {result.Unarchived.Count} loose file(s)");

            // Last, once nothing else will touch these archives: give the space back.
            //
            // Editing in place is what makes SAFT quick, and dead space is the price. Removing an
            // entry drops it from the directory table but leaves its bytes in the file, and restoring
            // a small original over a large modded entry shrinks the entry and strands the remainder.
            // Neither is reclaimed on its own, so a game that has had a heavy mod installed and taken
            // out again keeps that space forever - measured at 149,504 bytes across six holes after a
            // single install-and-uninstall of one small mod, every hole in the middle of the file
            // where truncation cannot reach it.
            //
            // Uninstalling is exactly when someone expects the folder to get smaller, and they have
            // already accepted a wait, so the full pass is paid here rather than on every install.
            UninstallSubProgressText.Text = "Packing out unused space…";
            ActivityLog.Note("uninstall: packing out unused space");

            // Reported like every other long step, and for the reason SAFT reports any of them: this
            // rewrites a whole archive, and on tired storage that has run for three to four minutes.
            // A progress bar that never moves is how a user decides the app has hung and closes it
            // in the middle of a rewrite of their game.
            var packProgress = new Progress<DirectInstallProgress>(p =>
            {
                UninstallSubProgressText.Text = $"{p.Stage} — {p.FilesDone:N0} of {p.FilesTotal:N0}";
                UninstallSubProgressBar.Maximum = Math.Max(1, p.FilesTotal);
                UninstallSubProgressBar.Value = Math.Clamp(p.FilesDone, 0, UninstallSubProgressBar.Maximum);
            });

            var reclaimed = await Task.Run(() =>
            {
                long total = 0;
                var archives = GameScanner.FindArchives(gameFolder);
                var archiveNumber = 0;

                foreach (var found in archives)
                {
                    archiveNumber++;
                    var stage = $"Packing out unused space — {found.RelativePath} ({archiveNumber} of {archives.Count})";

                    // Throttled at the door, so 16,316 entries cost ten UI updates a second rather
                    // than 16,316 of them. Same reason every other writer in SAFT throttles.
                    var throttled = new ThrottledProgress<DirectInstallProgress>(packProgress);

                    try
                    {
                        total += ImgArchiveEditor.Compact(
                            found.AbsolutePath, ActivityLog.Note, speed,
                            (done, count) => throttled.Report(new DirectInstallProgress(
                                found.RelativePath, archiveNumber, archives.Count, stage, done, count)));
                    }
                    catch (Exception ex)
                    {
                        // The uninstall itself has already succeeded. Failing to reclaim space must
                        // not turn that into a failure.
                        ActivityLog.Note($"uninstall: could not pack {found.RelativePath} - {ex.GetType().Name}: {ex.Message}");
                    }
                }

                return total;
            });

            ActivityLog.Note(reclaimed > 0
                ? $"uninstall: gave back {reclaimed / 1048576.0:N1} MB"
                : "uninstall: nothing to reclaim, the archives were already packed");

            UninstallSubProgressText.Text = "Done.";

            var filesRestored = result.Archives.Sum(s => s.FilesReplaced);
            var tooLargeCount = plan.AudioMatchesTooLarge.Count + plan.StreamMatchesTooLarge.Count;
            var failedCount = result.AudioFailed.Count + result.StreamFailed.Count;
            var unmatchedCount = plan.Unmatched.Count + plan.AudioUnmatched.Count + plan.StreamUnmatched.Count;

            var summaryLines = new List<string>
            {
                $"Uninstall complete. {filesRestored} file(s) restored across {result.Archives.Count} archive(s)."
            };
            if (result.Unarchived.Count > 0) summaryLines.Add($"{result.Unarchived.Count} game file(s) restored outside the archives (map data, etc).");
            if (removed is not null)
            {
                summaryLines.Add(
                    $"{removed.RemovedMods.Count} added mod(s) removed: {removed.ArchiveEntriesRemoved} asset(s) " +
                    $"taken back out of the archives and {removed.DataLinesRemoved} map entry/entries deleted, " +
                    $"freeing {removed.FreedObjectIds.Count} object slot(s).");

                // Skips are usually the same handful of reasons repeated once per file, which turned
                // a summary into a wall of near-identical lines. Grouped and counted instead, with a
                // couple of examples — the detail that matters is the reason, not the roll call.
                foreach (var group in removed.Skipped
                             .GroupBy(SkipReason, StringComparer.OrdinalIgnoreCase)
                             .OrderByDescending(g => g.Count()))
                {
                    summaryLines.Add(group.Count() == 1
                        ? $"Left alone: {group.First()}"
                        : $"Left alone ({group.Count()}x): {group.Key}");
                }
            }
            if (result.Audio.Count > 0) summaryLines.Add($"{result.Audio.Count} audio file(s) restored.");
            if (result.Streams.Count > 0) summaryLines.Add($"{result.Streams.Count} streamed track(s) restored.");
            if (result.UnarchivedFailed.Count > 0) summaryLines.Add($"{result.UnarchivedFailed.Count} game file(s) could not be written and were skipped.");
            if (plan.Ambiguous.Count > 0)
                summaryLines.Add(
                    $"{plan.Ambiguous.Count} file(s) exist both inside an archive and loose in your game folder, with different contents — " +
                    "the archived copy was restored and the loose copy was left alone.");
            if (tooLargeCount > 0) summaryLines.Add($"{tooLargeCount} audio file(s) were too large to restore and were skipped.");
            if (failedCount > 0) summaryLines.Add($"{failedCount} audio file(s) failed to read and were skipped.");
            if (unmatchedCount > 0) summaryLines.Add($"{unmatchedCount} file(s) in the backup folder didn't match anything in your game.");
            if (makeModBackup) summaryLines.Add($"Your previously installed mod files were backed up to: {modBackupFolder}");
            ActivityLog.Note($"uninstall: {speed.Describe()}");
            ActivityLog.Census("after uninstalling");

            // SAFT's own dialog, not a MessageBox. Two reasons, one already proven on a real screen:
            // a MessageBox is sized by Windows, and this summary grows a line per problem, so at
            // 960x544 it has already pushed its own OK button off the bottom of the display where it
            // could not be pressed. The second is that a MessageBox is a native window built by Wine,
            // and this app's history is a long list of native window work behaving differently there.
            // ConfirmDialog measures against the real screen, scrolls rather than overflowing, logs
            // itself, and is disposed.
            using (var summary = ConfirmDialog.Note(string.Join("\n", summaryLines), "OK"))
                summary.ShowDialog(this);
            ReportSlowStorage(speed);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Uninstall failed: {ex.Message}", "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UninstallButton.Enabled = true;
        }
    }
}
