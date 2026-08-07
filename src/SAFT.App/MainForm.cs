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
    private RadioButton InPlaceNoBackupOption = null!;
    private Panel RebuildDestRow = null!;
    private TextBox RebuildDestBox = null!;
    private Label InPlaceWarningText = null!;
    private Label NoBackupWarningText = null!;
    private Button RebuildButton = null!;
    private Label RebuildSubProgressText = null!;
    private ProgressBar RebuildSubProgressBar = null!;
    private ProgressBar RebuildProgressBar = null!;

    // ---- Tab 4: Install without extraction ----
    private TextBox DirectGameFolderBox = null!;
    private TextBox DirectModFolderBox = null!;
    private RadioButton DirectBackupOption = null!;
    private RadioButton DirectNoBackupOption = null!;
    private Panel DirectBackupDestRow = null!;
    private TextBox DirectBackupDestBox = null!;
    private Label DirectNoBackupWarningText = null!;
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

    private static string? BrowseForFolder(string description)
    {
        using var dialog = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true };
        return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
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

        GameFolderBox.Text = folder;
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

        var progress = new Progress<ExtractionProgress>(p =>
        {
            SetScaledProgress(ExtractProgressBar, p.ArchiveIndex, p.ArchiveCount, p.FilesDone, p.FilesTotal);

            ExtractSubProgressBar.Maximum = Math.Max(1, p.FilesTotal);
            ExtractSubProgressBar.Value = Math.Clamp(p.FilesDone, 0, ExtractSubProgressBar.Maximum);
            ExtractSubProgressText.Text = $"[{p.ArchiveIndex}/{p.ArchiveCount}] {p.CurrentArchive} — file {p.FilesDone:N0} of {p.FilesTotal:N0}";
        });

        try
        {
            var manifest = await Task.Run(() => Extractor.Extract(gameFolder, destFolder, includeAudio, progress));
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

            InstallSubProgressText.Text = "Done.";
            var unmatchedCount = result.Unmatched.Count + result.AudioUnmatched.Count;
            MessageBox.Show(
                $"Done. Routed {result.Routed.Count} file(s), {result.AudioRouted.Count} audio file(s)." +
                (unmatchedCount > 0 ? $" {unmatchedCount} file(s) didn't match anything and were left unplaced." : "") +
                "\n\nGo to the Rebuild tab when you're ready to build the archives.",
                "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Install failed: {ex.Message}", "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        NoBackupWarningText.Visible = InPlaceNoBackupOption.Checked;
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
        else if (!newFolder) // in-place, no backup — the destructive option
        {
            var confirm = new ConfirmDialog(
                "WARNING! This is an irreversible and permanent replacement of your game files, so make sure to back up the clean game in case any mods are no longer preferred in the future. No backup will be made by SAFT. Continue?",
                "yes, install without backups",
                "no, cancel this rebuild");
            confirm.ShowDialog(this);
            if (!confirm.Result) return;
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

        DirectGameFolderBox.Text = folder;
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

    private void OnDirectBackupModeChanged(object? sender, EventArgs e)
    {
        if (!_uiReady) return;

        var noBackup = DirectNoBackupOption.Checked;
        DirectBackupDestRow.Visible = !noBackup;
        DirectNoBackupWarningText.Visible = noBackup;
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

        var makeBackups = DirectBackupOption.Checked;
        if (makeBackups && string.IsNullOrWhiteSpace(DirectBackupDestBox.Text))
        {
            MessageBox.Show("Pick a backup folder first, or switch to the no-backup option.", "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!makeBackups)
        {
            var confirm = new ConfirmDialog(
                "WARNING! No backups will be made. Replaced files cannot be recovered through SAFT — make sure you have a clean copy of the game elsewhere. Continue?",
                "yes, replace files without backups",
                "no, cancel this mod installation");
            confirm.ShowDialog(this);
            if (!confirm.Result) return;
        }

        DirectProgressBar.Value = 0;
        DirectSubProgressBar.Value = 0;
        DirectSubProgressText.Text = "Checking mod files against the live game…";
        DirectInstallButton.Enabled = false;

        try
        {
            var plan = await Task.Run(() => DirectModInstaller.Plan(gameFolder, modFolder));

            if (plan.Matches.Count == 0 && plan.AudioMatches.Count == 0 && plan.StreamMatches.Count == 0)
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
                + (audioToApply.Count > 0 ? 1 : 0) + (streamsToApply.Count > 0 ? 1 : 0);
            var progress = new Progress<DirectInstallProgress>(p =>
            {
                SetScaledProgress(DirectProgressBar, p.ArchiveIndex, Math.Max(groupCountForProgress, p.ArchiveCount), p.FilesDone, p.FilesTotal);

                DirectSubProgressBar.Maximum = Math.Max(1, p.FilesTotal);
                DirectSubProgressBar.Value = Math.Clamp(p.FilesDone, 0, DirectSubProgressBar.Maximum);
                DirectSubProgressText.Text =
                    $"[{p.ArchiveIndex}/{p.ArchiveCount}] {p.CurrentArchive}: {p.Stage} — file {p.FilesDone:N0} of {p.FilesTotal:N0}";
            });

            var backupFolder = makeBackups ? DirectBackupDestBox.Text : null;
            var result = await Task.Run(() => DirectModInstaller.Apply(plan, backupFolder, progress));

            DirectSubProgressText.Text = "Done.";

            var filesReplaced = result.Archives.Sum(s => s.FilesReplaced);
            var tooLargeCount = plan.AudioMatchesTooLarge.Count + plan.StreamMatchesTooLarge.Count;
            var failedCount = result.AudioFailed.Count + result.StreamFailed.Count;
            var unmatchedFileCount = plan.Unmatched.Count + plan.AudioUnmatched.Count + plan.StreamUnmatched.Count;

            var summaryLines = new List<string>
            {
                $"Mod installation complete. {filesReplaced} file(s) replaced across {result.Archives.Count} archive(s)."
            };
            if (result.Audio.Count > 0) summaryLines.Add($"{result.Audio.Count} audio file(s) patched.");
            if (result.Streams.Count > 0) summaryLines.Add($"{result.Streams.Count} streamed track(s) patched.");
            if (tooLargeCount > 0) summaryLines.Add($"{tooLargeCount} audio file(s) were too large to replace and were skipped.");
            if (failedCount > 0) summaryLines.Add($"{failedCount} audio file(s) failed to read and were skipped.");
            if (unmatchedFileCount > 0) summaryLines.Add($"{unmatchedFileCount} file(s) didn't match anything in your game and were left unplaced.");
            summaryLines.Add(makeBackups ? $"Originals of every replaced file were backed up to: {backupFolder}" : "No backups were made.");

            MessageBox.Show(string.Join("\n", summaryLines), "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Install failed: {ex.Message}", "SAFT", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

        UninstallGameFolderBox.Text = folder;
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
            var plan = await Task.Run(() => DirectModInstaller.Plan(gameFolder, backupFolder));

            if (plan.Matches.Count == 0 && plan.AudioMatches.Count == 0 && plan.StreamMatches.Count == 0)
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
                + (audioToApply.Count > 0 ? 1 : 0) + (streamsToApply.Count > 0 ? 1 : 0);
            var progress = new Progress<DirectInstallProgress>(p =>
            {
                SetScaledProgress(UninstallProgressBar, p.ArchiveIndex, Math.Max(groupCountForProgress, p.ArchiveCount), p.FilesDone, p.FilesTotal);

                UninstallSubProgressBar.Maximum = Math.Max(1, p.FilesTotal);
                UninstallSubProgressBar.Value = Math.Clamp(p.FilesDone, 0, UninstallSubProgressBar.Maximum);
                UninstallSubProgressText.Text =
                    $"[{p.ArchiveIndex}/{p.ArchiveCount}] {p.CurrentArchive}: {p.Stage} — file {p.FilesDone:N0} of {p.FilesTotal:N0}";
            });

            var modBackupFolder = makeModBackup ? UninstallBackupDestBox.Text : null;
            var result = await Task.Run(() => DirectModInstaller.Apply(plan, modBackupFolder, progress));

            UninstallSubProgressText.Text = "Done.";

            var filesRestored = result.Archives.Sum(s => s.FilesReplaced);
            var tooLargeCount = plan.AudioMatchesTooLarge.Count + plan.StreamMatchesTooLarge.Count;
            var failedCount = result.AudioFailed.Count + result.StreamFailed.Count;
            var unmatchedCount = plan.Unmatched.Count + plan.AudioUnmatched.Count + plan.StreamUnmatched.Count;

            var summaryLines = new List<string>
            {
                $"Uninstall complete. {filesRestored} file(s) restored across {result.Archives.Count} archive(s)."
            };
            if (result.Audio.Count > 0) summaryLines.Add($"{result.Audio.Count} audio file(s) restored.");
            if (result.Streams.Count > 0) summaryLines.Add($"{result.Streams.Count} streamed track(s) restored.");
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
