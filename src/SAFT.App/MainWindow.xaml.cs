using System.IO;
using System.Windows;
using Microsoft.Win32;
using SAFT.Core;

namespace SAFT.App;

public partial class MainWindow : Window
{
    private IReadOnlyList<FoundArchive>? _scanResults;
    private SaftManifest? _loadedManifest;

    public MainWindow()
    {
        InitializeComponent();
    }

    private static string FormatSize(long bytes) => $"{bytes / 1073741824.0:0.0}GB";

    // ================= TAB 1: Extract =================

    private void OnBrowseGameFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select your GTA San Andreas game folder" };
        if (dialog.ShowDialog() != true) return;

        if (!GameScanner.LooksLikeSanAndreasInstall(dialog.FolderName))
        {
            var proceed = MessageBox.Show(
                "This folder doesn't look like a San Andreas PC install (no gta_sa.exe / models\\gta3.img found). " +
                "Use it anyway?",
                "SAFT", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (proceed != MessageBoxResult.Yes) return;
        }

        GameFolderBox.Text = dialog.FolderName;
        _scanResults = null;
        ExtractButton.IsEnabled = false;
        ScanSummaryText.Text = "";
        ExtractWarningText.Text = "";
    }

    private void OnBrowseExtractDest(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select where to extract the game's archives" };
        if (dialog.ShowDialog() == true)
            ExtractDestBox.Text = dialog.FolderName;

        _ = UpdateExtractionWarningAsync();
    }

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(GameFolderBox.Text))
        {
            MessageBox.Show("Pick a game folder first.", "SAFT", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var gameFolder = GameFolderBox.Text;
        ScanSummaryText.Text = "Scanning…";
        ExtractWarningText.Text = "";
        ExtractButton.IsEnabled = false;

        try
        {
            _scanResults = await Task.Run(() => GameScanner.FindArchives(gameFolder));
        }
        catch (Exception ex)
        {
            ScanSummaryText.Text = "";
            MessageBox.Show($"Scan failed: {ex.Message}", "SAFT", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (_scanResults.Count == 0)
        {
            ScanSummaryText.Text = "No VER2 IMG archives were found under that folder.";
            return;
        }

        ScanSummaryText.Text = $"Found {_scanResults.Count} archive(s): " +
                                string.Join(", ", _scanResults.Select(a => a.RelativePath));
        ExtractButton.IsEnabled = true;

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
        var includeAudio = ExtractAudioCheckBox.IsChecked == true;

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

    private async void OnExtractAudioOptionChanged(object sender, RoutedEventArgs e) => await UpdateExtractionWarningAsync();

    private async void OnExtract(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ExtractDestBox.Text))
        {
            MessageBox.Show("Pick a destination folder first.", "SAFT", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var gameFolder = GameFolderBox.Text;
        var destFolder = ExtractDestBox.Text;
        var includeAudio = ExtractAudioCheckBox.IsChecked == true;

        ExtractLogList.Items.Clear();
        ExtractProgressBar.Value = 0;
        ExtractSubProgressBar.Value = 0;
        ExtractSubProgressText.Text = "Starting…";
        SetExtractControlsEnabled(false);

        var progress = new Progress<ExtractionProgress>(p =>
        {
            ExtractProgressBar.Maximum = p.ArchiveCount;
            ExtractProgressBar.Value = p.ArchiveIndex - 1 + (double)p.FilesDone / Math.Max(1, p.FilesTotal);

            ExtractSubProgressBar.Maximum = p.FilesTotal;
            ExtractSubProgressBar.Value = p.FilesDone;
            ExtractSubProgressText.Text = $"[{p.ArchiveIndex}/{p.ArchiveCount}] {p.CurrentArchive} — file {p.FilesDone:N0} of {p.FilesTotal:N0}";

            if (p.FilesDone == p.FilesTotal)
                ExtractLogList.Items.Add($"[{p.ArchiveIndex}/{p.ArchiveCount}] {p.CurrentArchive} — {p.FilesTotal:N0} files extracted");
        });

        try
        {
            var manifest = await Task.Run(() => Extractor.Extract(gameFolder, destFolder, includeAudio, progress));
            ExtractSubProgressText.Text = "Done.";
            ExtractLogList.Items.Add($"Done. {manifest.Archives.Count} archive(s) extracted to {destFolder}.");
            MessageBox.Show("Extraction complete.", "SAFT", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ExtractLogList.Items.Add($"FAILED: {ex.Message}");
            MessageBox.Show($"Extraction failed: {ex.Message}", "SAFT", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetExtractControlsEnabled(true);
        }
    }

    private void SetExtractControlsEnabled(bool enabled)
    {
        ExtractButton.IsEnabled = enabled && _scanResults is { Count: > 0 };
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
            MessageBox.Show(ex.Message, "SAFT", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        var totalEntries = _loadedManifest.Archives.Sum(a => a.OriginalEntryOrder.Count);
        var summary =
            $"Extracted from: {_loadedManifest.GameRootPath}\n" +
            $"{_loadedManifest.Archives.Count} archive(s), {totalEntries} original entries. " +
            $"Extracted {_loadedManifest.ExtractedAtUtc.ToLocalTime():g}.";

        ExtractionFolderBox.Text = folderPath;
        ManifestSummaryText.Text = summary;
        RebuildButton.IsEnabled = true;

        InstallExtractionFolderBox.Text = folderPath;
        InstallManifestSummaryText.Text = summary;
        InstallButton.IsEnabled = !string.IsNullOrWhiteSpace(ModSourceFolderBox.Text);

        _ = UpdateRebuildSizeEstimateAsync(folderPath);

        return true;
    }

    private async Task UpdateRebuildSizeEstimateAsync(string extractionFolder)
    {
        const string newFolderBase = "Rebuild into a new folder (safe, non-destructive)";
        const string inPlaceBase = "Install over the original game files (backs up each archive as .img.bak first, inside the corresponding folder within the rebuilt game directory)";

        NewFolderOptionText.Text = $"{newFolderBase} — calculating size…";
        InPlaceWithBackupOptionText.Text = $"{inPlaceBase} — calculating size…";

        try
        {
            var estimate = await Task.Run(() => RebuildEstimator.Estimate(extractionFolder));
            if (ExtractionFolderBox.Text != extractionFolder) return; // a different folder was picked meanwhile

            NewFolderOptionText.Text =
                $"{newFolderBase} adds a second playable game folder totaling {FormatSize(estimate.NewFolderTotalBytes)} in the output folder";
            InPlaceWithBackupOptionText.Text =
                $"{inPlaceBase} replacing original {FormatSize(estimate.GameRootTotalBytes)} game with a " +
                $"{FormatSize(estimate.InPlaceWithBackupTotalBytes)} total output game (including .img.bak clean backups)";
        }
        catch (Exception ex)
        {
            NewFolderOptionText.Text = newFolderBase;
            InPlaceWithBackupOptionText.Text = inPlaceBase;
            RebuildLogList.Items.Add($"Couldn't calculate rebuild size estimate: {ex.Message}");
        }
    }

    private void OnBrowseExtractionFolderForInstall(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the folder you extracted the game to" };
        if (dialog.ShowDialog() == true)
            TryLoadManifest(dialog.FolderName);
    }

    private void OnBrowseModSourceFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the folder containing the mod's files" };
        if (dialog.ShowDialog() != true) return;

        ModSourceFolderBox.Text = dialog.FolderName;
        InstallButton.IsEnabled = _loadedManifest is not null;
    }

    private async void OnInstallMod(object sender, RoutedEventArgs e)
    {
        if (_loadedManifest is null)
        {
            MessageBox.Show("Pick an extracted folder first.", "SAFT", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(ModSourceFolderBox.Text))
        {
            MessageBox.Show("Pick a mod folder first.", "SAFT", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var extractionFolder = InstallExtractionFolderBox.Text;
        var modFolder = ModSourceFolderBox.Text;

        InstallLogList.Items.Clear();
        InstallSubProgressBar.Value = 0;
        InstallSubProgressText.Text = "Starting…";
        InstallButton.IsEnabled = false;

        var progress = new Progress<ModInstallProgress>(p =>
        {
            InstallSubProgressBar.Maximum = p.FilesTotal;
            InstallSubProgressBar.Value = p.FilesDone;
            InstallSubProgressText.Text = $"Checking file {p.FilesDone:N0} of {p.FilesTotal:N0}: {p.CurrentFile}";
        });

        try
        {
            var result = await Task.Run(() => ModInstaller.Install(extractionFolder, modFolder, progress));

            foreach (var r in result.Routed)
                InstallLogList.Items.Add($"{r.FileName} -> {string.Join(", ", r.ArchiveRelativePaths)}");

            foreach (var a in result.AudioRouted)
                InstallLogList.Items.Add($"{a.MatchKey} -> audio");

            if (result.Unmatched.Count > 0)
            {
                InstallLogList.Items.Add("");
                InstallLogList.Items.Add("Unmatched (no original file has this name — these look like new additions, not replacements; place them manually if that's intended):");
                foreach (var name in result.Unmatched)
                    InstallLogList.Items.Add($"  {name}");
            }

            if (result.AudioUnmatched.Count > 0)
            {
                InstallLogList.Items.Add("");
                InstallLogList.Items.Add("Unmatched audio (no unpacked sound/track has this Package/Bank_NNN/sound_NNN.wav or Station/Track_NNN.ogg path — either it doesn't exist, or that package/station was extracted without audio checked):");
                foreach (var key in result.AudioUnmatched)
                    InstallLogList.Items.Add($"  {key}");
            }

            InstallLogList.Items.Add("");
            InstallLogList.Items.Add(
                $"Done. Routed {result.Routed.Count} file(s), {result.AudioRouted.Count} audio file(s), " +
                $"{result.Unmatched.Count + result.AudioUnmatched.Count} unmatched. " +
                "Go to the Rebuild tab when you're ready to build the archives.");
            InstallSubProgressText.Text = "Done.";
        }
        catch (Exception ex)
        {
            InstallLogList.Items.Add($"FAILED: {ex.Message}");
            MessageBox.Show($"Install failed: {ex.Message}", "SAFT", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            InstallButton.IsEnabled = true;
        }
    }

    // ================= TAB 3: Rebuild from Extracted =================

    private void OnBrowseExtractionFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the folder you extracted the game to" };
        if (dialog.ShowDialog() == true)
            TryLoadManifest(dialog.FolderName);
    }

    private void OnOutputModeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        RebuildDestRow.Visibility = NewFolderOption.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        InPlaceWarningText.Visibility = InPlaceWithBackupOption.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        NoBackupWarningText.Visibility = InPlaceNoBackupOption.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBrowseRebuildDest(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select where to write the rebuilt archives" };
        if (dialog.ShowDialog() == true)
            RebuildDestBox.Text = dialog.FolderName;
    }

    private async void OnRebuild(object sender, RoutedEventArgs e)
    {
        if (_loadedManifest is null)
        {
            MessageBox.Show("Pick an extracted folder first.", "SAFT", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var extractionFolder = ExtractionFolderBox.Text;
        var gameRoot = _loadedManifest.GameRootPath;
        var newFolder = NewFolderOption.IsChecked == true;
        var withBackup = InPlaceWithBackupOption.IsChecked == true;

        if (newFolder && string.IsNullOrWhiteSpace(RebuildDestBox.Text))
        {
            MessageBox.Show("Pick an output folder first.", "SAFT", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (withBackup)
        {
            var confirm = MessageBox.Show(
                $"This will overwrite the archives inside:\n{gameRoot}\n\n" +
                "A .img.bak backup of each original is created automatically, inside the corresponding folder within the rebuilt game directory. Continue?",
                "SAFT", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }
        else if (!newFolder) // in-place, no backup — the destructive option
        {
            var confirm = new ConfirmDialog(
                "WARNING! This is an irreversible and permanent replacement of your game files, so make sure to back up the clean game in case any mods are no longer preferred in the future. No backup will be made by SAFT. Continue?",
                "yes, install without backups",
                "no, cancel this rebuild")
            { Owner = this };
            confirm.ShowDialog();
            if (!confirm.Result) return;
        }

        RebuildLogList.Items.Clear();
        RebuildProgressBar.Value = 0;
        RebuildSubProgressBar.Value = 0;
        RebuildButton.IsEnabled = false;

        var rebuildProgress = new Progress<RebuildProgress>(p =>
        {
            RebuildProgressBar.Maximum = p.ArchiveCount;
            RebuildProgressBar.Value = p.ArchiveIndex - 1 + (double)p.FilesDone / Math.Max(1, p.FilesTotal);

            RebuildSubProgressBar.Maximum = p.FilesTotal;
            RebuildSubProgressBar.Value = p.FilesDone;
            RebuildSubProgressText.Text = $"[{p.ArchiveIndex}/{p.ArchiveCount}] {p.CurrentArchive} — file {p.FilesDone:N0} of {p.FilesTotal:N0}";

            if (p.FilesDone == p.FilesTotal)
                RebuildLogList.Items.Add($"[{p.ArchiveIndex}/{p.ArchiveCount}] {p.CurrentArchive}: done");
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

            foreach (var s in summaries)
                RebuildLogList.Items.Add($"{s.RelativePath}: kept {s.Kept}, added {s.Added}, removed {s.Removed}");

            RebuildLogList.Items.Add("");
            RebuildSubProgressText.Text = "Done.";

            if (withBackup)
            {
                var backupNames = summaries.Select(s => Path.GetFileName(s.RelativePath) + ".bak").ToList();
                RebuildLogList.Items.Add("Backups saved in the corresponding folders inside the game directory:");
                foreach (var s in summaries)
                    RebuildLogList.Items.Add($"  {Path.Combine(gameRoot, s.RelativePath)}.bak");

                MessageBox.Show(
                    "Rebuild complete.\n\n" +
                    "Backups of your original archives were saved inside the corresponding folders within the game directory " +
                    "(each original .img renamed to .img.bak):\n\n" + string.Join("\n", backupNames),
                    "SAFT", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                RebuildLogList.Items.Add("Done.");
                MessageBox.Show("Rebuild complete.", "SAFT", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            RebuildLogList.Items.Add($"FAILED: {ex.Message}");
            MessageBox.Show($"Rebuild failed: {ex.Message}", "SAFT", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RebuildButton.IsEnabled = true;
        }
    }

    // ================= TAB 4: Install Mod(s) without extraction =================

    private void OnBrowseDirectGameFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select your GTA San Andreas game folder" };
        if (dialog.ShowDialog() != true) return;

        if (!GameScanner.LooksLikeSanAndreasInstall(dialog.FolderName))
        {
            var proceed = MessageBox.Show(
                "This folder doesn't look like a San Andreas PC install (no gta_sa.exe / models\\gta3.img found). " +
                "Use it anyway?",
                "SAFT", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (proceed != MessageBoxResult.Yes) return;
        }

        DirectGameFolderBox.Text = dialog.FolderName;
        UpdateDirectInstallButtonEnabled();
    }

    private void OnBrowseDirectModFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the folder containing the mod's files" };
        if (dialog.ShowDialog() == true)
            DirectModFolderBox.Text = dialog.FolderName;

        UpdateDirectInstallButtonEnabled();
    }

    private void UpdateDirectInstallButtonEnabled()
    {
        DirectInstallButton.IsEnabled =
            !string.IsNullOrWhiteSpace(DirectGameFolderBox.Text) && !string.IsNullOrWhiteSpace(DirectModFolderBox.Text);
    }

    private void OnDirectBackupModeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        var noBackup = DirectNoBackupOption.IsChecked == true;
        DirectBackupDestRow.Visibility = noBackup ? Visibility.Collapsed : Visibility.Visible;
        DirectNoBackupWarningText.Visibility = noBackup ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBrowseDirectBackupDest(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select where to save backups of replaced files" };
        if (dialog.ShowDialog() == true)
            DirectBackupDestBox.Text = dialog.FolderName;
    }

    private async void OnDirectInstall(object sender, RoutedEventArgs e)
    {
        var gameFolder = DirectGameFolderBox.Text;
        var modFolder = DirectModFolderBox.Text;

        if (string.IsNullOrWhiteSpace(gameFolder) || string.IsNullOrWhiteSpace(modFolder))
        {
            MessageBox.Show("Pick both a game folder and a mod folder first.", "SAFT", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var makeBackups = DirectBackupOption.IsChecked == true;
        if (makeBackups && string.IsNullOrWhiteSpace(DirectBackupDestBox.Text))
        {
            MessageBox.Show("Pick a backup folder first, or switch to the no-backup option.", "SAFT", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!makeBackups)
        {
            var confirm = new ConfirmDialog(
                "WARNING! No backups will be made. Replaced files cannot be recovered through SAFT — make sure you have a clean copy of the game elsewhere. Continue?",
                "yes, replace files without backups",
                "no, cancel this mod installation")
            { Owner = this };
            confirm.ShowDialog();
            if (!confirm.Result) return;
        }

        DirectLogList.Items.Clear();
        DirectProgressBar.Value = 0;
        DirectSubProgressBar.Value = 0;
        DirectSubProgressText.Text = "Checking mod files against the live game…";
        DirectInstallButton.IsEnabled = false;

        try
        {
            var plan = await Task.Run(() => DirectModInstaller.Plan(gameFolder, modFolder));

            if (plan.Matches.Count == 0 && plan.AudioMatches.Count == 0 && plan.StreamMatches.Count == 0)
            {
                DirectLogList.Items.Add("No mod files matched anything in your current install. Nothing was changed.");
                if (plan.Unmatched.Count > 0)
                {
                    DirectLogList.Items.Add("");
                    DirectLogList.Items.Add("Unmatched files:");
                    foreach (var name in plan.Unmatched)
                        DirectLogList.Items.Add($"  {name}");
                }
                if (plan.AudioUnmatched.Count > 0)
                {
                    DirectLogList.Items.Add("");
                    DirectLogList.Items.Add("Unmatched audio files (expected <Package>/Bank_NNN/sound_NNN.wav):");
                    foreach (var name in plan.AudioUnmatched)
                        DirectLogList.Items.Add($"  {name}");
                }
                if (plan.StreamUnmatched.Count > 0)
                {
                    DirectLogList.Items.Add("");
                    DirectLogList.Items.Add("Unmatched streamed audio files (expected <Station>/Track_NNN.ogg):");
                    foreach (var name in plan.StreamUnmatched)
                        DirectLogList.Items.Add($"  {name}");
                }
                DirectSubProgressText.Text = "Done.";
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
                    "no, cancel this mod installation")
                { Owner = this };
                confirm.ShowDialog();
                if (!confirm.Result)
                {
                    DirectLogList.Items.Add("Cancelled — no files were changed.");
                    DirectSubProgressText.Text = "Cancelled.";
                    return;
                }
            }
            else
            {
                DirectLogList.Items.Add("Mod files are similar enough in size to originals, no rebuilding needed, replacing each file individually…");
            }

            if (plan.AudioMatchesTooLarge.Count > 0)
                DirectLogList.Items.Add($"Note: {plan.AudioMatchesTooLarge.Count} matched audio file(s) are larger than the original sound and will be skipped (details below).");
            if (plan.StreamMatchesTooLarge.Count > 0)
                DirectLogList.Items.Add($"Note: {plan.StreamMatchesTooLarge.Count} matched streamed audio file(s) are larger than the original track and will be skipped (details below).");

            var audioToApply = plan.AudioMatchesThatFit;
            var streamsToApply = plan.StreamMatchesThatFit;
            var groupCountForProgress = plan.Matches.Select(m => m.ArchiveRelativePath).Distinct().Count()
                + (audioToApply.Count > 0 ? 1 : 0) + (streamsToApply.Count > 0 ? 1 : 0);
            var progress = new Progress<DirectInstallProgress>(p =>
            {
                DirectProgressBar.Maximum = groupCountForProgress;
                DirectProgressBar.Value = p.ArchiveIndex - 1 + (p.FilesTotal == 0 ? 0 : (double)p.FilesDone / p.FilesTotal);

                DirectSubProgressBar.Maximum = Math.Max(1, p.FilesTotal);
                DirectSubProgressBar.Value = p.FilesDone;
                DirectSubProgressText.Text =
                    $"[{p.ArchiveIndex}/{p.ArchiveCount}] {p.CurrentArchive}: {p.Stage} — file {p.FilesDone:N0} of {p.FilesTotal:N0}";
            });

            var backupFolder = makeBackups ? DirectBackupDestBox.Text : null;
            var result = await Task.Run(() => DirectModInstaller.Apply(plan, backupFolder, progress));

            foreach (var s in result.Archives)
                DirectLogList.Items.Add($"{s.ArchiveRelativePath}: {s.FilesReplaced} file(s) replaced ({(s.Rebuilt ? "rebuilt" : "patched in place")})");

            if (result.Audio.Count > 0)
                DirectLogList.Items.Add($"Audio: {result.Audio.Count} sound(s) patched in place");

            if (result.Streams.Count > 0)
                DirectLogList.Items.Add($"Streamed audio: {result.Streams.Count} track(s) patched in place");

            if (plan.AudioMatchesTooLarge.Count > 0)
            {
                DirectLogList.Items.Add("");
                DirectLogList.Items.Add("Could NOT replace (mod audio is larger than the original sound's allocated space — audio replacement only supports same-size-or-smaller, unlike models/textures):");
                foreach (var m in plan.AudioMatchesTooLarge)
                    DirectLogList.Items.Add($"  {m.MatchKey} (needs {m.NewPcmLength:N0} bytes, {m.OriginalPcmLength:N0} available)");
            }

            if (plan.StreamMatchesTooLarge.Count > 0)
            {
                DirectLogList.Items.Add("");
                DirectLogList.Items.Add("Could NOT replace (mod streamed audio is larger than the original track's allocated space — same limitation as SFX):");
                foreach (var m in plan.StreamMatchesTooLarge)
                    DirectLogList.Items.Add($"  {m.MatchKey} (needs {m.NewPayloadLength:N0} bytes, {m.OriginalPayloadLength:N0} available)");
            }

            if (plan.Unmatched.Count > 0)
            {
                DirectLogList.Items.Add("");
                DirectLogList.Items.Add("Unmatched (no original file has this name — these look like new additions, not replacements):");
                foreach (var name in plan.Unmatched)
                    DirectLogList.Items.Add($"  {name}");
            }

            if (plan.AudioUnmatched.Count > 0)
            {
                DirectLogList.Items.Add("");
                DirectLogList.Items.Add("Unmatched audio files (expected <Package>/Bank_NNN/sound_NNN.wav):");
                foreach (var name in plan.AudioUnmatched)
                    DirectLogList.Items.Add($"  {name}");
            }

            if (plan.StreamUnmatched.Count > 0)
            {
                DirectLogList.Items.Add("");
                DirectLogList.Items.Add("Unmatched streamed audio files (expected <Station>/Track_NNN.ogg):");
                foreach (var name in plan.StreamUnmatched)
                    DirectLogList.Items.Add($"  {name}");
            }

            DirectLogList.Items.Add("");
            DirectLogList.Items.Add(makeBackups
                ? $"Done. Originals of every replaced file were backed up to: {backupFolder}"
                : "Done. No backups were made.");
            DirectSubProgressText.Text = "Done.";

            MessageBox.Show("Mod installation complete.", "SAFT", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DirectLogList.Items.Add($"FAILED: {ex.Message}");
            MessageBox.Show($"Install failed: {ex.Message}", "SAFT", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            DirectInstallButton.IsEnabled = true;
        }
    }
}
