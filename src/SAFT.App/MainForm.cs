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
    private static HashSet<string> GameFileNames(string gameRoot)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var found in GameScanner.FindArchives(gameRoot))
        {
            try
            {
                using var archive = ImgArchive.Open(found.AbsolutePath);
                foreach (var entry in archive.Entries) names.Add(entry.Name);
            }
            catch
            {
                // An unreadable archive just means those names look "new"; the addition popups then
                // ask rather than acting, which is the safe direction.
            }
        }

        foreach (var group in UnarchivedIndex.Build(gameRoot)) names.Add(group.Key);
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

        using var wait = ConfirmDialog.Wait(
            "If you are on Winlator and about to launch the game, wait 60 seconds for the game to " +
            "recover from its recent surgery. Rushing into a launch can sometimes cause freezes on " +
            "the first launch. only time heals some wounds.",
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
            var ok = ConfirmDialog.Acknowledgement(verdict.Message, "OK", verdict.Severity);
            ActivityLog.Note("dialog: Fine acknowledgement constructed, showing it");
            ok.ShowDialog(this);
            ActivityLog.Note("dialog: Fine acknowledgement closed");
            return true;
        }

        var confirm = new ConfirmDialog(
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
        var confirm = new ConfirmDialog(
            $"Your mod folder contains {additions.SlotsRequired} new object(s) that are not in your game " +
            $"directory. These would take up {additions.SlotsRequired} of your game's object slots. SAFT can " +
            $"add them because there are currently {additions.SlotsAvailable} available and compatible slots.\n\n" +
            "Would you like SAFT to add them, and write backup-logs of how they were added, so you can " +
            $"cleanly uninstall them later? This would leave you with {slotsAfter} empty slots.",
            "Yes, add new assets",
            "No, replacements only");
        confirm.ShowDialog(this);

        if (!confirm.Result) return false;

        MessageBox.Show(
            "Adding new assets…\n\n" +
            "Please note that any assets added through other tools or methods will not be uninstallable " +
            "through SAFT. But if SAFT added it in, SAFT can remove it later.",
            "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

        var confirm = new ConfirmDialog(
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

        var confirm = new ConfirmDialog(
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
    private static AddedMod? FindInstalledMod(string backupFolder, string modName)
    {
        if (string.IsNullOrWhiteSpace(backupFolder)) return null;

        try
        {
            return AdditionsManifest.Load(backupFolder)?.Mods
                .FirstOrDefault(m => m.Name.Equals(modName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // An unreadable manifest just means we can't tell, and installing normally is no worse
            // than today's behaviour.
            return null;
        }
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

        var confirm = new ConfirmDialog(
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
        var confirm = new ConfirmDialog(
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

        var confirm = new ConfirmDialog(
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
                ConfirmDialog.Acknowledgement(
                    $"Extracting this game will write about {FormatSize(totalBytes)} into:\n{destination}\n\n" +
                    $"That is over 20,000 separate files. On Windows this takes a few minutes. On Winlator " +
                    "it can take a long time - creating that many files one at a time is slow on Android's " +
                    "storage, and it is the slowest thing SAFT does by a wide margin.\n\n" +
                    "You only need this tab to rebuild the game from scratch. To install a mod, use " +
                    "\"Install Mod(s) without extraction\" instead - it changes only the files your mod " +
                    "touches and takes seconds.",
                    "OK, I understand",
                    StreamingSeverity.Caution).ShowDialog(this);
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
            var plan = await Task.Run(() => DirectModInstaller.Plan(gameFolder, modFolder, ActivityLog.Note));
            ActivityLog.Note($"install: plan has {plan.Matches.Count} archived + {plan.UnarchivedMatches.Count} loose match(es)");

            WarnAboutRefusedScripts(plan.RefusedScripts);

            // Everything the game already has, so the scanner can tell an addition from a replacement.
            Analysing("Checking what your game already has…");
            var existing = await Task.Run(() => GameFileNames(gameFolder));
            ActivityLog.Note($"install: game holds {existing.Count} known file name(s); scanning for additions");

            Analysing("Checking what this mod adds…");
            var additions = await Task.Run(() => AdditionScanner.Scan(gameFolder, modFolder, existing.Contains));

            // What the mod does to the streaming budget — this applies to replacement-only mods too,
            // which is where an over-heavy pack quietly stops the world rendering.
            Analysing("Checking how much this mod adds to what your game has to load…");
            var replacementSizes = await Task.Run(() => ReplacementSizes(plan));
            ActivityLog.Note("install: measuring streaming impact");
            var impact = await Task.Run(() => StreamingImpact.Measure(gameFolder, replacementSizes, ActivityLog.Note));

            // Full, so the last step reads as finished rather than as stopped three quarters of the
            // way along while the popup is being built.
            DirectSubProgressBar.Value = analysisSteps;
            DirectSubProgressText.Text = "Checks complete.";
            // The baseline goes in whether or not this mod adds anything: it describes the game being
            // installed into, which matters just as much for a pure replacement.
            var verdict = StreamingAdvice.Compose(
                additions.HasAdditions ? additions.Density : null, impact, additions.Density.Baseline);
            ActivityLog.Note($"install: verdict {verdict.Severity}, within range {verdict.WithinRange}");

            if (!ConfirmStreamingImpact(verdict))
            {
                DirectSubProgressText.Text = "Cancelled.";
                return;
            }

            var installAdditions = false;
            if (additions.HasAdditions)
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
            var modName = Path.GetFileName(modFolder.TrimEnd(Path.DirectorySeparatorChar));
            var alreadyInstalled = installAdditions ? FindInstalledMod(DirectBackupDestBox.Text, modName) : null;
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

                var confirm = new ConfirmDialog(
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
            var result = await Task.Run(() => DirectModInstaller.Apply(plan, backupFolder, progress));

            AdditionInstallResult? added = null;
            if (installAdditions && additions is not null)
            {
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
                    DirectSubProgressText.Text = "Removing the previously installed copy…";
                    var priorManifest = AdditionsManifest.Load(backupFolder);
                    if (priorManifest is not null)
                    {
                        await Task.Run(() => AdditionUninstaller.Remove(
                            gameFolder, priorManifest, new[] { modName }, additionProgress));
                        priorManifest.Save(backupFolder);
                    }

                    // The old copy's assets and ids are gone, so what counts as "new" has changed.
                    DirectSubProgressText.Text = "Rechecking what this mod adds…";
                    var names = await Task.Run(() => GameFileNames(gameFolder));
                    additions = await Task.Run(() => AdditionScanner.Scan(gameFolder, modFolder, names.Contains));
                }

                added = await Task.Run(() => AdditionInstaller.Apply(gameFolder, additions, modName, additionProgress));

                // The record of what was added lives in the backup folder, alongside the originals of
                // anything replaced — an added asset has no vanilla counterpart, so without this the
                // uninstall tab would have no way to know the addition ever happened.
                if (backupFolder is not null)
                {
                    var manifest = AdditionsManifest.Load(backupFolder) ?? new AdditionsManifest { GameRootPath = gameFolder };

                    // Belt and braces: if a record under this name somehow survived, replace it
                    // rather than sitting alongside it.
                    manifest.Mods.RemoveAll(m => m.Name.Equals(modName, StringComparison.OrdinalIgnoreCase));
                    manifest.Mods.Add(added.Recorded);
                    manifest.Save(backupFolder);
                }
            }

            DirectSubProgressText.Text = "Done.";

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

            MessageBox.Show(string.Join("\n", summaryLines), "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Information);

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
                var confirm = new ConfirmDialog(
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

                var confirm = new ConfirmDialog(
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

            var modBackupFolder = makeModBackup ? UninstallBackupDestBox.Text : null;
            ActivityLog.Note($"uninstall: restoring, mod backup {(modBackupFolder is null ? "off" : "to " + modBackupFolder)}");
            var result = await Task.Run(() => DirectModInstaller.Apply(plan, modBackupFolder, progress));
            ActivityLog.Note($"uninstall: restore finished - {result.Archives.Sum(s => s.FilesReplaced)} entry/entries across {result.Archives.Count} archive(s), {result.Unarchived.Count} loose file(s)");

            // Additions come out first: their removal rebuilds the archive, and doing it before the
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
                    AdditionUninstaller.Remove(gameFolder, additionsManifest, modsToRemove, removalProgress));
                ActivityLog.Note($"uninstall: removal finished - {removed.ArchiveEntriesRemoved} asset(s), {removed.DataLinesRemoved} map line(s), {removed.FreedObjectIds.Count} slot(s) freed");

                // The record is rewritten so a later uninstall doesn't try to remove all this again.
                additionsManifest.Save(backupFolder);
            }

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

            MessageBox.Show(string.Join("\n", summaryLines), "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
