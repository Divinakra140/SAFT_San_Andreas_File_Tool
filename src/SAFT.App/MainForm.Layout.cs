using System.Drawing;

namespace SAFT.App;

/// <summary>
/// Builds the window's UI in code (WinForms has no XAML equivalent) — a functional-first port of
/// the original WPF layout: clouds background, left side panel with the S icon and feature-list
/// images, title wordmark/byline, and an opaque white work area holding the 4-tab TabControl.
/// Exact pixel-for-pixel spacing wasn't the goal here (WinForms' layout model is different enough
/// that a literal recreation isn't meaningful) — this aims to be a clean, recognizable approximation,
/// left open to visual refinement once it's actually been seen running.
/// </summary>
public partial class MainForm
{
    private const int SidePanelWidth = 220;

    /// <summary>
    /// A fixed stand-in for "how wide a tab's content area will be," used only for measuring how
    /// tall wrapped text needs to be. Deliberately NOT read from an actual control's ClientSize —
    /// at the point these labels are built (mid-constructor, before the form has ever been laid
    /// out), a control's ClientSize is still an unset placeholder, not its real eventual width, so
    /// measuring against it under-counts the width and wildly over-counts the wrapped line count —
    /// exactly what caused the log list to be crushed or disappear entirely on tabs with long
    /// description text. Sized conservatively for the window's minimum width (880), not its
    /// default (1040), so wrapping still stays safe if the window is resized down.
    /// </summary>
    private const int EstimatedContentWidth = 620;

    /// <summary>
    /// A realistic worst-case stand-in for the manifest summary text (see
    /// MainForm.cs's TryLoadManifest) — that label starts out empty and only gets its real,
    /// two-line, potentially-long-path text later, once the user actually picks a folder. Reserving
    /// space for the empty string at construction time (the same mistake <see cref="EstimatedContentWidth"/>
    /// already fixed elsewhere) meant the real text, once set, had nowhere to go but overlap
    /// whatever was placed right below it. Measuring this placeholder instead reserves enough room
    /// upfront regardless of when the real text actually arrives.
    /// </summary>
    private const string ManifestSummaryPlaceholder =
        "Extracted from: X:\\Some\\Reasonably\\Deeply\\Nested\\Game\\Install\\Folder\\Grand Theft Auto San Andreas\n" +
        "8 archive(s), 99999 original entries. Extracted 12/31/2026 11:59 PM.";

    private void BuildUi()
    {
        Text = "SAFT 1.6 — San Andreas File Tool";
        Width = 1040;
        Height = 760;
        MinimumSize = new Size(880, 600);
        StartPosition = FormStartPosition.CenterScreen;

        var background = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.StretchImage,
            Image = EmbeddedImages.Load("SAFT.Assets.clouds_panel_bg.png"),
        };
        Controls.Add(background);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(12),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SidePanelWidth));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        // Nested INSIDE background, not added as a sibling — WinForms' "transparent" BackColor only
        // resolves through a control's actual Parent chain, not whatever's behind it in Z-order. As
        // siblings, root's transparency fell through to the Form's own plain gray, hiding the clouds
        // entirely (that's the bug behind the missing clouds background).
        background.Controls.Add(root);

        root.Controls.Add(BuildSidePanel(), 0, 0);
        root.Controls.Add(BuildMainColumn(), 1, 0);
    }

    private Control BuildSidePanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };

        // All eleven labels are normalised to the same letter height (the source art was drawn at
        // differing font sizes, so each was scaled by ink-per-letter rather than by bounding box —
        // scaling by box height would have made words with tall ascenders render smaller). Widths
        // and heights below are each image's own trimmed size, so nothing is stretched.
        var y = 20;
        AddCentered(panel, "SAFT.Assets.icon_s.png", 150, 150, ref y, 32);
        AddCentered(panel, "SAFT.Assets.panel.replaces.png", 118, 34, ref y, 20);
        AddCentered(panel, "SAFT.Assets.panel.models.png", 84, 23, ref y, 6);
        AddCentered(panel, "SAFT.Assets.panel.collision.png", 96, 24, ref y, 6);
        AddCentered(panel, "SAFT.Assets.panel.textures.png", 102, 24, ref y, 6);
        AddCentered(panel, "SAFT.Assets.panel.animations.png", 112, 24, ref y, 6);
        AddCentered(panel, "SAFT.Assets.panel.audio.png", 68, 22, ref y, 6);
        AddCentered(panel, "SAFT.Assets.panel.map_data.png", 101, 24, ref y, 6);
        AddCentered(panel, "SAFT.Assets.panel.paths.png", 72, 23, ref y, 6);
        AddCentered(panel, "SAFT.Assets.panel.data_tables.png", 135, 23, ref y, 6);
        AddCentered(panel, "SAFT.Assets.panel.text.png", 63, 22, ref y, 6);
        AddCentered(panel, "SAFT.Assets.panel.cutscenes.png", 130, 24, ref y, 6);
        AddCentered(panel, "SAFT.Assets.panel.particle_effects.png", 189, 28, ref y, 0);

        return panel;
    }

    private void AddCentered(Control parent, string logicalName, int width, int height, ref int y, int marginBottom)
    {
        var pictureBox = new PictureBox
        {
            Image = EmbeddedImages.Load(logicalName),
            SizeMode = PictureBoxSizeMode.Zoom,
            Left = (SidePanelWidth - width) / 2,
            Top = y,
            Width = width,
            Height = height,
            BackColor = Color.Transparent,
        };
        parent.Controls.Add(pictureBox);
        y += height + marginBottom;
    }

    private Control BuildMainColumn()
    {
        var column = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 1,
            RowCount = 2,
        };
        column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        column.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var titlePanel = new Panel { Dock = DockStyle.Top, Height = 110, BackColor = Color.Transparent };
        var wordmark = new PictureBox
        {
            Image = EmbeddedImages.Load("SAFT.Assets.panel.title_wordmark.png"),
            SizeMode = PictureBoxSizeMode.Zoom,
            Width = 480,
            Height = 56,
            Top = 6,
            BackColor = Color.Transparent,
        };
        var byline = new PictureBox
        {
            Image = EmbeddedImages.Load("SAFT.Assets.panel.title_byline.png"),
            SizeMode = PictureBoxSizeMode.Zoom,
            Width = 220,
            Height = 48,
            Top = 64,
            BackColor = Color.Transparent,
        };
        titlePanel.Controls.Add(wordmark);
        titlePanel.Controls.Add(byline);
        titlePanel.Resize += (_, _) =>
        {
            wordmark.Left = (titlePanel.Width - wordmark.Width) / 2;
            byline.Left = (titlePanel.Width - byline.Width) / 2;
        };

        // The "work window": opaque white regardless of the clouds behind everything else.
        var workArea = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(4) };
        var tabs = new TabControl { Dock = DockStyle.Fill };
        workArea.Controls.Add(tabs);

        BuildExtractTab(tabs);
        BuildInstallIntoExtractedTab(tabs);
        BuildRebuildTab(tabs);
        BuildDirectInstallTab(tabs);
        BuildUninstallTab(tabs);

        column.Controls.Add(titlePanel, 0, 0);
        column.Controls.Add(workArea, 0, 1);
        return column;
    }

    // ---- shared small layout helpers ----

    /// <summary>
    /// Draws a button's own border/fill explicitly instead of relying on the OS's native button
    /// chrome — under Winlator (Official) a plain system Button renders as bare text with no
    /// visible border or background at all, giving no indication it's clickable. FlatStyle.Flat
    /// makes WinForms paint the border/fill itself in every environment, matching the app's plain
    /// black-on-white aesthetic and guaranteeing a visible, clickable-looking frame everywhere.
    /// </summary>
    private static void ApplyButtonChrome(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Color.Black;
        button.BackColor = Color.White;
        button.ForeColor = Color.Black;
    }

    /// <summary>Shrinks a button to fit its text plus a comfortable click margin, instead of leaving it whatever width it was constructed with.</summary>
    private static void SizeButtonToText(Button button, int horizontalPadding = 16)
    {
        var textSize = TextRenderer.MeasureText(button.Text, button.Font);
        button.Width = textSize.Width + horizontalPadding * 2;
    }

    /// <summary>
    /// Places a button sized to its own text (not stretched to the row's full width the way
    /// <see cref="PlaceRow"/> stretches everything else), centered horizontally, with explicit
    /// chrome so it reads as a pressable control on its own without relying on OS button theming.
    /// Anchor alone can't express "stay centered on resize," so this re-centers on the
    /// container's Resize event instead.
    /// </summary>
    private static void PlaceButton(Control container, Button button, ref int y, int height = 30, int gapAfter = 8)
    {
        ApplyButtonChrome(button);
        SizeButtonToText(button);
        button.Top = y;
        button.Height = height;
        button.Anchor = AnchorStyles.Top;
        container.Controls.Add(button);

        void Recenter() => button.Left = Math.Max(0, (container.ClientSize.Width - button.Width) / 2);
        Recenter();
        container.Resize += (_, _) => Recenter();

        y += height + gapAfter;
    }

    /// <summary>A "Label: [textbox][Browse…]" row, matching the repeated Grid pattern in the original XAML.</summary>
    private static Panel BuildBrowseRow(string labelText, int labelWidth, out TextBox textBox, EventHandler onBrowse)
    {
        var row = new Panel { Dock = DockStyle.Top, Height = 28, Margin = new Padding(0, 0, 0, 8) };

        var label = new Label { Text = labelText, Width = labelWidth, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleLeft };
        var button = new Button { Text = "Browse…", Width = 90, Dock = DockStyle.Right };
        button.Click += onBrowse;
        ApplyButtonChrome(button);
        var box = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Margin = new Padding(4, 0, 4, 0) };

        // Add in reverse dock order so Fill ends up correctly sandwiched between the two docked edges.
        row.Controls.Add(box);
        row.Controls.Add(button);
        row.Controls.Add(label);

        textBox = box;
        return row;
    }

    private static Label BuildWrappedLabel(string text, bool bold = false, Color? color = null)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = false,
            Dock = DockStyle.Top,
            TextAlign = ContentAlignment.TopLeft,
            Margin = new Padding(0, 0, 0, 8),
        };
        if (bold) label.Font = new Font(label.Font, FontStyle.Bold);
        if (color is { } c) label.ForeColor = c;
        return label;
    }

    /// <summary>Sets a control's height to whatever its wrapped text actually needs at the given width.</summary>
    private static void AutoHeightWrap(Control control, int width)
    {
        using var g = control.CreateGraphics();
        var size = g.MeasureString(control.Text, control.Font, width - 4);
        control.Height = (int)Math.Ceiling(size.Height) + 6;
    }

    /// <summary>Places a fixed-height row at absolute Y, anchored so it stretches horizontally with its container.</summary>
    private static void PlaceRow(Control container, Control control, ref int y, int height, int gapAfter = 8)
    {
        control.SetBounds(0, y, Math.Max(1, container.ClientSize.Width), height);
        control.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        container.Controls.Add(control);
        y += height + gapAfter;
    }

    // ================= TAB 1: EXTRACT =================

    private void BuildExtractTab(TabControl tabs)
    {
        var tab = new TabPage("Extract Game Files");
        tabs.TabPages.Add(tab);

        var top = new Panel { Dock = DockStyle.Top, Height = 230, Padding = new Padding(10) };
        tab.Controls.Add(top);

        var y = 0;
        PlaceRow(top, BuildBrowseRow("Game folder:", 110, out var gameFolderBox, OnBrowseGameFolder), ref y, 26);
        GameFolderBox = gameFolderBox;

        PlaceRow(top, BuildBrowseRow("Destination:", 110, out var extractDestBox, OnBrowseExtractDest), ref y, 26);
        ExtractDestBox = extractDestBox;

        ExtractAudioCheckBox = new CheckBox
        {
            Text = "Extract Audio Files as well? (takes much longer and extracted folder takes up more storage)",
            AutoSize = false,
        };
        ExtractAudioCheckBox.CheckedChanged += OnExtractAudioOptionChanged;
        AutoHeightWrap(ExtractAudioCheckBox, EstimatedContentWidth);
        PlaceRow(top, ExtractAudioCheckBox, ref y, Math.Max(24, ExtractAudioCheckBox.Height));

        var buttonRow = new Panel { Height = 30 };
        var scanButton = new Button { Text = "Scan", Top = 0, Height = 30 };
        scanButton.Click += OnScan;
        ApplyButtonChrome(scanButton);
        SizeButtonToText(scanButton);

        ExtractButton = new Button { Text = "Extract", Top = 0, Height = 30, Enabled = false };
        ExtractButton.Click += OnExtract;
        ApplyButtonChrome(ExtractButton);
        SizeButtonToText(ExtractButton);

        buttonRow.Controls.Add(scanButton);
        buttonRow.Controls.Add(ExtractButton);
        PlaceRow(top, buttonRow, ref y, 30);

        // buttonRow only gets its real width once PlaceRow stretches it above, so the pair is
        // centered (and re-centered on resize) after that, not before.
        const int scanExtractGap = 8;
        void CenterScanExtractPair()
        {
            var pairWidth = scanButton.Width + scanExtractGap + ExtractButton.Width;
            var left = Math.Max(0, (buttonRow.ClientSize.Width - pairWidth) / 2);
            scanButton.Left = left;
            ExtractButton.Left = left + scanButton.Width + scanExtractGap;
        }
        CenterScanExtractPair();
        buttonRow.Resize += (_, _) => CenterScanExtractPair();

        // Both start empty and only get their real (potentially long/wrapping) text once a folder
        // is actually scanned — same latent risk as the manifest summary labels on tabs 2 and 3,
        // fixed the same way: measure a realistic worst case now, not the empty string.
        ScanSummaryText = BuildWrappedLabel(
            "Found 8 archive(s): models\\gta3.img, models\\gta_int.img, models\\player.img, models\\cutscene.img, anim\\anim.img, anim\\cuts.img, data\\Paths\\carrec.img, data\\script\\script.img");
        ScanSummaryText.Dock = DockStyle.None;
        AutoHeightWrap(ScanSummaryText, EstimatedContentWidth);
        PlaceRow(top, ScanSummaryText, ref y, ScanSummaryText.Height, 4);
        ScanSummaryText.Text = "";

        ExtractWarningText = BuildWrappedLabel(
            "Warning, extracted game files will take up approximately 999.9GB of storage.", bold: true, color: Color.DarkRed);
        ExtractWarningText.Dock = DockStyle.None;
        AutoHeightWrap(ExtractWarningText, EstimatedContentWidth);
        PlaceRow(top, ExtractWarningText, ref y, ExtractWarningText.Height);
        ExtractWarningText.Text = "";

        ExtractSubProgressText = new Label { Text = " ", Font = new Font(Font.FontFamily, 8f), ForeColor = Color.Gray };
        PlaceRow(top, ExtractSubProgressText, ref y, 16, 2);

        ExtractSubProgressBar = new ProgressBar { Height = 8 };
        PlaceRow(top, ExtractSubProgressBar, ref y, 8, 6);

        ExtractProgressBar = new ProgressBar { Height = 18 };
        PlaceRow(top, ExtractProgressBar, ref y, 18);

        top.Height = y;

        // A plain white fill for the tab space below `top` — not for content, just so the area
        // isn't left showing whatever the TabPage's own unpainted background looks like under
        // Wine/Winlator (a real, painted control here proved more reliable than a bare TabPage
        // background color).
        tab.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Color.White });
    }

    // ================= TAB 2: INSTALL MOD INTO EXTRACTED =================

    private void BuildInstallIntoExtractedTab(TabControl tabs)
    {
        var tab = new TabPage("Install Mod(s) into Extracted");
        tabs.TabPages.Add(tab);

        var top = new Panel { Dock = DockStyle.Top, Height = 220, Padding = new Padding(10) };
        tab.Controls.Add(top);

        var y = 0;
        PlaceRow(top, BuildBrowseRow("Extracted Game Folder:", 150, out var installExtractionFolderBox, OnBrowseExtractionFolderForInstall), ref y, 26);
        InstallExtractionFolderBox = installExtractionFolderBox;

        InstallManifestSummaryText = BuildWrappedLabel(ManifestSummaryPlaceholder);
        InstallManifestSummaryText.Dock = DockStyle.None;
        AutoHeightWrap(InstallManifestSummaryText, EstimatedContentWidth);
        PlaceRow(top, InstallManifestSummaryText, ref y, Math.Max(18, InstallManifestSummaryText.Height));
        InstallManifestSummaryText.Text = ""; // just measured for height; nothing to show until a folder is actually picked

        PlaceRow(top, BuildBrowseRow("Mod folder:", 150, out var modSourceFolderBox, OnBrowseModSourceFolder), ref y, 26);
        ModSourceFolderBox = modSourceFolderBox;

        var description = BuildWrappedLabel(
            "Point this at the folder you unzipped a mod pack into and SAFT matches each file by name and copies it into the right archive automatically, no matter what subfolder structure the mod uses. Audio mod files need to be in the correct nested folder structure and cannot be loose files, and only match sounds/tracks you extracted with \"Extract Audio Files as well?\" checked. Files that don't match anything original are listed as unmatched below for manual placement. See Readme.txt for more info on audio.",
            color: Color.Gray);
        description.Dock = DockStyle.None;
        AutoHeightWrap(description, EstimatedContentWidth);
        PlaceRow(top, description, ref y, description.Height);

        InstallButton = new Button { Text = "Install Mod into Extracted Game Folder", Enabled = false };
        InstallButton.Click += OnInstallMod;
        PlaceButton(top, InstallButton, ref y, 30);

        InstallSubProgressText = new Label { Text = " ", Font = new Font(Font.FontFamily, 8f), ForeColor = Color.Gray };
        PlaceRow(top, InstallSubProgressText, ref y, 16, 2);

        InstallSubProgressBar = new ProgressBar { Height = 8 };
        PlaceRow(top, InstallSubProgressBar, ref y, 8);

        top.Height = y;

        tab.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Color.White });
    }

    // ================= TAB 3: REBUILD FROM EXTRACTED =================

    private void BuildRebuildTab(TabControl tabs)
    {
        var tab = new TabPage("Rebuild from Extracted");
        tabs.TabPages.Add(tab);

        var top = new Panel { Dock = DockStyle.Top, Height = 340, Padding = new Padding(10) };
        tab.Controls.Add(top);

        var y = 0;
        PlaceRow(top, BuildBrowseRow("Extracted Game Folder:", 150, out var extractionFolderBox, OnBrowseExtractionFolder), ref y, 26);
        ExtractionFolderBox = extractionFolderBox;

        ManifestSummaryText = BuildWrappedLabel(ManifestSummaryPlaceholder);
        ManifestSummaryText.Dock = DockStyle.None;
        AutoHeightWrap(ManifestSummaryText, EstimatedContentWidth);
        PlaceRow(top, ManifestSummaryText, ref y, Math.Max(18, ManifestSummaryText.Height), 4);
        ManifestSummaryText.Text = ""; // just measured for height; nothing to show until a folder is actually picked

        // These two get longer once the real size estimate loads (extra "totaling X GB" text
        // appended) — measure a realistic worst-case final string for height, same technique as
        // ManifestSummaryPlaceholder, instead of a flat guessed pixel buffer on top of the short
        // "calculating…" placeholder (which reserved more room than actually needed).
        NewFolderOption = new RadioButton { Checked = true, AutoSize = false };
        NewFolderOption.CheckedChanged += OnOutputModeChanged;
        NewFolderOption.Text = "Rebuild into a new folder (safe, non-destructive) adds a second playable game folder totaling 999.9GB in the output folder";
        AutoHeightWrap(NewFolderOption, EstimatedContentWidth);
        PlaceRow(top, NewFolderOption, ref y, NewFolderOption.Height, 4);
        NewFolderOption.Text = "Rebuild into a new folder (safe, non-destructive) — calculating size…";

        InPlaceWithBackupOption = new RadioButton { AutoSize = false };
        InPlaceWithBackupOption.CheckedChanged += OnOutputModeChanged;
        InPlaceWithBackupOption.Text = "Install over the original game files (backs up each archive as .img.bak first, inside the corresponding folder within the rebuilt game directory) replacing original 999.9GB game with a 999.9GB total output game (including .img.bak clean backups)";
        AutoHeightWrap(InPlaceWithBackupOption, EstimatedContentWidth);
        PlaceRow(top, InPlaceWithBackupOption, ref y, InPlaceWithBackupOption.Height, 4);
        InPlaceWithBackupOption.Text = "Install over the original game files (backs up each archive as .img.bak first, inside the corresponding folder within the rebuilt game directory) — calculating size…";

        InPlaceNoBackupOption = new RadioButton { Text = "Install over original game files without .img backups", AutoSize = false, Height = 22 };
        InPlaceNoBackupOption.CheckedChanged += OnOutputModeChanged;
        PlaceRow(top, InPlaceNoBackupOption, ref y, 22, 4);

        RebuildDestRow = BuildBrowseRow("Output folder:", 110, out var rebuildDestBox, OnBrowseRebuildDest);
        RebuildDestBox = rebuildDestBox;
        PlaceRow(top, RebuildDestRow, ref y, 26, 4);

        InPlaceWarningText = BuildWrappedLabel(
            "This will overwrite the archives in your game install. A .img.bak backup of each original is made automatically before the first overwrite, inside the corresponding folder within the rebuilt game directory.",
            color: Color.DarkRed);
        InPlaceWarningText.Dock = DockStyle.None;
        AutoHeightWrap(InPlaceWarningText, EstimatedContentWidth);
        InPlaceWarningText.Visible = false;

        NoBackupWarningText = BuildWrappedLabel(
            "WARNING! This is an irreversible and permanent replacement of your game files, so make sure to back up the clean game in case any mods are no longer preferred in the future.",
            bold: true, color: Color.DarkRed);
        NoBackupWarningText.Dock = DockStyle.None;
        AutoHeightWrap(NoBackupWarningText, EstimatedContentWidth);
        NoBackupWarningText.Visible = false;

        // These two are mutually exclusive — only one is ever visible at a time, matching the
        // radio selection above — so they share one row's worth of vertical space instead of two
        // separate stacked rows each reserving their own height.
        var warningHeight = Math.Max(InPlaceWarningText.Height, NoBackupWarningText.Height);
        var warningWidth = Math.Max(1, top.ClientSize.Width);
        InPlaceWarningText.SetBounds(0, y, warningWidth, warningHeight);
        InPlaceWarningText.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        top.Controls.Add(InPlaceWarningText);
        NoBackupWarningText.SetBounds(0, y, warningWidth, warningHeight);
        NoBackupWarningText.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        top.Controls.Add(NoBackupWarningText);
        y += warningHeight + 4;

        RebuildButton = new Button { Text = "Rebuild", Enabled = false };
        RebuildButton.Click += OnRebuild;
        PlaceButton(top, RebuildButton, ref y, 30);

        RebuildSubProgressText = new Label { Text = " ", Font = new Font(Font.FontFamily, 8f), ForeColor = Color.Gray };
        PlaceRow(top, RebuildSubProgressText, ref y, 16, 2);

        RebuildSubProgressBar = new ProgressBar { Height = 8 };
        PlaceRow(top, RebuildSubProgressBar, ref y, 8, 6);

        RebuildProgressBar = new ProgressBar { Height = 18 };
        PlaceRow(top, RebuildProgressBar, ref y, 18);

        top.Height = y;

        tab.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Color.White });
    }

    // ================= TAB 4: INSTALL MOD WITHOUT EXTRACTION =================

    private void BuildDirectInstallTab(TabControl tabs)
    {
        var tab = new TabPage("Install Mod(s) without extraction");
        tabs.TabPages.Add(tab);

        var top = new Panel { Dock = DockStyle.Top, Height = 320, Padding = new Padding(10) };
        tab.Controls.Add(top);

        var y = 0;
        PlaceRow(top, BuildBrowseRow("Game folder:", 110, out var directGameFolderBox, OnBrowseDirectGameFolder), ref y, 26);
        DirectGameFolderBox = directGameFolderBox;

        PlaceRow(top, BuildBrowseRow("Mod folder:", 110, out var directModFolderBox, OnBrowseDirectModFolder), ref y, 26);
        DirectModFolderBox = directModFolderBox;

        DirectBackupOption = new RadioButton { Text = "Back up original files before replacing (recommended)", Checked = true, AutoSize = false, Height = 22 };
        DirectBackupOption.CheckedChanged += OnDirectBackupModeChanged;
        PlaceRow(top, DirectBackupOption, ref y, 22, 4);

        DirectNoBackupOption = new RadioButton { Text = "Replace files without backups — WARNING: permanent and irreversible", AutoSize = false, Height = 22 };
        DirectNoBackupOption.CheckedChanged += OnDirectBackupModeChanged;
        PlaceRow(top, DirectNoBackupOption, ref y, 22);

        DirectBackupDestRow = BuildBrowseRow("Backup folder:", 110, out var directBackupDestBox, OnBrowseDirectBackupDest);
        DirectBackupDestBox = directBackupDestBox;
        PlaceRow(top, DirectBackupDestRow, ref y, 26);

        DirectNoBackupWarningText = BuildWrappedLabel(
            "WARNING! No backups will be made. Replaced files cannot be recovered through SAFT — make sure you have a clean copy of the game elsewhere before continuing.",
            bold: true, color: Color.DarkRed);
        DirectNoBackupWarningText.Dock = DockStyle.None;
        AutoHeightWrap(DirectNoBackupWarningText, EstimatedContentWidth);
        DirectNoBackupWarningText.Visible = false;
        PlaceRow(top, DirectNoBackupWarningText, ref y, DirectNoBackupWarningText.Height);

        var description = BuildWrappedLabel(
            "This is the fast and popular option that installs your mod files directly into the live game archives, without extracting them. If you get a message about rebuilding, just say yes, its a necessary step if your mod files are any bigger than the vanilla files they replace. Audio mod files need to be in the correct nested folder structure and cannot be loose files, all other asset types can be loose. See Readme.txt for more info on audio.",
            color: Color.Gray);
        description.Dock = DockStyle.None;
        AutoHeightWrap(description, EstimatedContentWidth);
        PlaceRow(top, description, ref y, description.Height);

        DirectInstallButton = new Button { Text = "Install Mod into Game Files", Enabled = false };
        DirectInstallButton.Click += OnDirectInstall;
        PlaceButton(top, DirectInstallButton, ref y, 30);

        DirectSubProgressText = new Label { Text = " ", Font = new Font(Font.FontFamily, 8f), ForeColor = Color.Gray };
        PlaceRow(top, DirectSubProgressText, ref y, 16, 2);

        DirectSubProgressBar = new ProgressBar { Height = 8 };
        PlaceRow(top, DirectSubProgressBar, ref y, 8, 6);

        DirectProgressBar = new ProgressBar { Height = 18 };
        PlaceRow(top, DirectProgressBar, ref y, 18);

        top.Height = y;

        tab.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Color.White });
    }

    // ================= TAB 5: UNINSTALL MOD(S) =================

    private void BuildUninstallTab(TabControl tabs)
    {
        var tab = new TabPage("Uninstall Mod(s)");
        tabs.TabPages.Add(tab);

        var top = new Panel { Dock = DockStyle.Top, Height = 320, Padding = new Padding(10) };
        tab.Controls.Add(top);

        var y = 0;

        var description = BuildWrappedLabel(
            "This function is only possible to use if you backed up your vanilla game files to an identifiable folder while modding SA. Point this at that backup folder and SAFT matches its files against your current, modded game and restores them, exactly like installing a mod but in reverse.",
            color: Color.Gray);
        description.Dock = DockStyle.None;
        AutoHeightWrap(description, EstimatedContentWidth);
        PlaceRow(top, description, ref y, description.Height);

        PlaceRow(top, BuildBrowseRow("Game folder:", 110, out var uninstallGameFolderBox, OnBrowseUninstallGameFolder), ref y, 26);
        UninstallGameFolderBox = uninstallGameFolderBox;

        PlaceRow(top, BuildBrowseRow("Backup Folder:", 110, out var uninstallBackupFolderBox, OnBrowseUninstallBackupFolder), ref y, 26);
        UninstallBackupFolderBox = uninstallBackupFolderBox;

        UninstallBackupModsCheckBox = new CheckBox
        {
            Text = "Backup installed mods before uninstalling",
            AutoSize = false,
            Height = 22,
        };
        UninstallBackupModsCheckBox.CheckedChanged += OnUninstallBackupModsOptionChanged;
        PlaceRow(top, UninstallBackupModsCheckBox, ref y, 22, 4);

        UninstallBackupDestRow = BuildBrowseRow("Backup mods to:", 110, out var uninstallBackupDestBox, OnBrowseUninstallBackupDest);
        UninstallBackupDestBox = uninstallBackupDestBox;
        UninstallBackupDestRow.Visible = false;
        PlaceRow(top, UninstallBackupDestRow, ref y, 26);

        UninstallButton = new Button { Text = "Uninstall Mod(s)", Enabled = false };
        UninstallButton.Click += OnUninstall;
        PlaceButton(top, UninstallButton, ref y, 30);

        UninstallSubProgressText = new Label { Text = " ", Font = new Font(Font.FontFamily, 8f), ForeColor = Color.Gray };
        PlaceRow(top, UninstallSubProgressText, ref y, 16, 2);

        UninstallSubProgressBar = new ProgressBar { Height = 8 };
        PlaceRow(top, UninstallSubProgressBar, ref y, 8, 6);

        UninstallProgressBar = new ProgressBar { Height = 18 };
        PlaceRow(top, UninstallProgressBar, ref y, 18);

        top.Height = y;

        tab.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Color.White });
    }
}
