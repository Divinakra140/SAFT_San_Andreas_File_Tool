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
        // The build time is in the title so there is never any doubt about which exe is running. A
        // whole round of testing was spent unsure whether a fix was even in the copy being launched.
        var built = File.GetLastWriteTime(Environment.ProcessPath ?? AppContext.BaseDirectory);
        Text = $"{Edition.Name} 2.1.3 — {Edition.Tagline}   [build {built:yyyy-MM-dd HH:mm}]";
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
            // Tighter at the top than the sides: the logo sits at the very top of the left column
            // and every pixel of head-room there is one the list does not have to give up.
            Padding = new Padding(12, 4, 12, 12),
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

        // Sized so the whole panel fits a 960x544 screen, which the original did not - it came to
        // 577px against about 490 available, so the last three entries were cut off the bottom.
        //
        // What works is no normalisation at all: ONE scale factor applied to every word's natural
        // size. The bullet dot is the same glyph in all eleven images and measures 6-7px in every one
        // of them, which proves the source art is already drawn to a consistent size - the boxes
        // differ only because of ascenders and descenders. Every clever per-word metric tried here
        // (bounding box, x-height, median column, 35th percentile) made it WORSE by stretching words
        // that merely had short boxes. Rendered side by side, uniform scaling is plainly the evenest.
        //
        // The header is "Modify:" rather than "Replaces:" because SAFT does both to these file types,
        // and because the adds/replaces distinction is not one a player installing a modpack has any
        // use for.
        var y = 0;
        AddCentered(panel, "SAFT.Assets.panel.logo.png", 190, 172, ref y, 8);
        AddCentered(panel, "SAFT.Assets.panel.modify.png", 112, 38, ref y, 10);

        // The first five carry a boost on top of the shared scale: 1.22x for Models and Audio,
        // 1.34x for Collision, Textures and Animations, which still read small at 1.22.
        //
        // Uniform scaling is right in principle - the bullet dot proves the art is drawn to one size -
        // but these five have no descender and only short ascenders, so their bounding boxes are
        // 20-21px where the rest are 22-28. Scaled uniformly they measure the same and still LOOK
        // smaller, because what the eye compares is the drawn word, not the box around it. The boost
        // is judged by eye against the rest, which is the only instrument that applies here: rendered
        // at 1.12, 1.22 and 1.32 side by side, 1.12 still left them looking small and 1.32 overshot
        // far enough that "Map Data" through "Text" started looking small instead.
        AddCentered(panel, "SAFT.Assets.panel.models.png", 82, 21, ref y, 4);
        AddCentered(panel, "SAFT.Assets.panel.collision.png", 102, 22, ref y, 4);
        AddCentered(panel, "SAFT.Assets.panel.textures.png", 108, 22, ref y, 4);
        AddCentered(panel, "SAFT.Assets.panel.animations.png", 119, 22, ref y, 4);
        AddCentered(panel, "SAFT.Assets.panel.audio.png", 66, 20, ref y, 4);
        AddCentered(panel, "SAFT.Assets.panel.map_data.png", 84, 20, ref y, 4);
        AddCentered(panel, "SAFT.Assets.panel.paths.png", 59, 19, ref y, 4);
        AddCentered(panel, "SAFT.Assets.panel.data_tables.png", 112, 19, ref y, 4);
        AddCentered(panel, "SAFT.Assets.panel.text.png", 52, 18, ref y, 4);
        AddCentered(panel, "SAFT.Assets.panel.cutscenes.png", 108, 20, ref y, 4);
        AddCentered(panel, "SAFT.Assets.panel.particle_effects.png", 155, 23, ref y, 0);

        // The byline lives BELOW the list, in whatever space is left, and shows only when there is
        // room for it. On a 960x544 handheld the list reaches the bottom of the screen and the byline
        // simply is not drawn; at any larger size it appears, centred in the gap.
        //
        // Two lines rather than one, so it can be set large enough to read as a signature rather than
        // as a footnote.
        const int listBottom = 494, bylineBlock = 71, bylineGap = 5;
        var byLine1 = AddFloating(panel, "SAFT.Assets.panel.by_line1.png", 49, 32);
        var byLine2 = AddFloating(panel, "SAFT.Assets.panel.by_line2.png", 123, 34);

        // Only Top and Visible are ever assigned here. Nothing is resized, so this cannot feed back
        // into the parent's layout - which is exactly how the title panel's resize handler managed to
        // recurse until the process died.
        void PlaceByline()
        {
            var space = panel.Height - listBottom;
            var show = space >= bylineBlock + 20;
            byLine1.Visible = byLine2.Visible = show;
            if (!show) return;

            var top = listBottom + (space - bylineBlock) / 2;
            byLine1.Top = top;
            byLine2.Top = top + byLine1.Height + bylineGap;
        }
        panel.Resize += (_, _) => PlaceByline();
        PlaceByline();

        return panel;
    }

    /// <summary>Horizontally centred, but positioned vertically by the caller rather than stacked.</summary>
    private PictureBox AddFloating(Control parent, string logicalName, int width, int height)
    {
        var pictureBox = new PictureBox
        {
            Image = EmbeddedImages.Load(logicalName),
            SizeMode = PictureBoxSizeMode.Zoom,
            Left = (SidePanelWidth - width) / 2,
            Width = width,
            Height = height,
            Visible = false,
            BackColor = Color.Transparent,
        };
        parent.Controls.Add(pictureBox);
        return pictureBox;
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

        // One title image instead of a wordmark plus a byline.
        //
        // The four words were supplied separately at 735x339 each and are composed offline into
        // title_bold.png at 1544x161, reproducing the reference arrangement exactly: the reference
        // fixes the relative width and baseline of each word, and every word is scaled DOWN from its
        // high-resolution source, never up. That is what lets the title be drawn much larger than the
        // old 480x56 wordmark without going soft.
        //
        // "- By Divinakra" is gone, which is what buys the height. It is still in the window title,
        // the exe's file properties and the README.
        // Docked and zoomed, with NO resize handler.
        //
        // The first version of this sized the image from a Resize handler. That is a loop: this panel
        // sits in an AutoSize table row, so changing the child's bounds changes the row's preferred
        // height, which resizes the panel, which fires Resize again. The recursion is a
        // StackOverflowException - uncatchable by design, so the process simply vanished on startup
        // with no exception and no crash log, on Windows and Winlator alike.
        //
        // PictureBoxSizeMode.Zoom already does the whole job: it fits the image inside the control,
        // preserves the aspect ratio and centres it. A fixed panel height and Dock.Fill get the same
        // result with no layout code of ours to go wrong.
        var titlePanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 74,
            Padding = new Padding(8, 5, 8, 5),
            BackColor = Color.Transparent,
        };
        var wordmark = new PictureBox
        {
            Image = EmbeddedImages.Load("SAFT.Assets.panel.title_bold.png"),
            SizeMode = PictureBoxSizeMode.Zoom,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
        };
        titlePanel.Controls.Add(wordmark);

        // The "work window": opaque white regardless of the clouds behind everything else.
        var workArea = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(4) };
        var tabs = new TabControl { Dock = DockStyle.Fill };

        // Tabs sized to share the full width, and taller than default.
        //
        // Two tabs left at their natural width occupy a corner of the strip and read as unfinished -
        // the five-tab layout filled it by accident, not by design. Fixed sizing also buys height,
        // which matters more than it looks: this is used on a handheld, where a 20px tab is a poor
        // target for a thumb or a d-pad cursor.
        //
        // TCS_FIXEDWIDTH is plain comctl32, the same widget family already in use everywhere here -
        // deliberately not the multiline/right-justify route, which is a different layout path and a
        // worse thing to be discovering the behaviour of under Wine.
        // Only in the two-tab build.
        //
        // Fixed width divides the strip evenly, which is what makes two tabs fill it instead of
        // huddling in the corner. With five tabs that same division gives each about 140px, and
        // "Install Mod(s) without extraction" does not fit in 140px - it just gets clipped. The Dev
        // build has enough tabs to fill the strip on its own, so it keeps the default sizing that
        // measures each caption.
#if !MODDEV
        // #if rather than a runtime check on the constant, which the compiler correctly reports as
        // unreachable code in whichever build it does not apply to.
        tabs.SizeMode = TabSizeMode.Fixed;
        tabs.ItemSize = new Size(160, 34);

        void FitTabsToWidth()
        {
            if (tabs.TabPages.Count == 0) return;
            var each = (tabs.ClientSize.Width - 8) / tabs.TabPages.Count;
            var wanted = new Size(each, 34);
            if (each > 20 && tabs.ItemSize != wanted) tabs.ItemSize = wanted;
        }

        // Guarded for the same reason the title panel no longer has a Resize handler at all: setting
        // a property from inside the event that property can raise is how you get infinite recursion,
        // and the symptom is a process that disappears without an exception.
        var fittingTabs = false;
        tabs.Resize += (_, _) =>
        {
            if (fittingTabs) return;
            fittingTabs = true;
            try { FitTabsToWidth(); } finally { fittingTabs = false; }
        };
#endif
        workArea.Controls.Add(tabs);

#if MODDEV
        // The three extraction-based tabs exist only in the Dev build. See Edition.
        BuildExtractTab(tabs);
        BuildInstallIntoExtractedTab(tabs);
        BuildRebuildTab(tabs);
#endif

        BuildDirectInstallTab(tabs);
        BuildUninstallTab(tabs);

#if !MODDEV
        FitTabsToWidth();
#endif

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
            // Not red any more. It sat directly above the slow-extraction warning, and two blocks of
            // red text in a row read as one block that the eye skips - which is why the size warning
            // was being missed. This is now the reference figure you come back to; the popup on scan
            // is what actually stops you.
            "Warning, extracted game files will take up approximately 999.9GB of storage.", bold: true, color: Color.FromArgb(0, 60, 130));
        ExtractWarningText.Dock = DockStyle.None;
        AutoHeightWrap(ExtractWarningText, EstimatedContentWidth);
        PlaceRow(top, ExtractWarningText, ref y, ExtractWarningText.Height);
        ExtractWarningText.Text = "";

        // Separate from the size warning above because it is a different decision: that one is about
        // whether the storage fits, this one is about whether to use this tab at all. Extraction
        // writes over twenty thousand individual files, and on a phone that is slow no matter how
        // efficient SAFT is - the per-file cost belongs to Android's storage layer, not to us.
        ExtractSlowWarningText = BuildWrappedLabel(
            "Warning: even without audio, extracting the whole game writes over 20,000 separate files " +
            "and can take a long time on Winlator. Use Windows for this if you can. To casually load a " +
            "modpack, use \"Install Mod(s) without extraction\" instead - it does not extract anything " +
            "and takes seconds.",
            bold: true, color: Color.DarkRed);
        ExtractSlowWarningText.Dock = DockStyle.None;
        AutoHeightWrap(ExtractSlowWarningText, EstimatedContentWidth);
        PlaceRow(top, ExtractSlowWarningText, ref y, ExtractSlowWarningText.Height, 4);

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

        // The third option here used to be "install over original game files without .img backups".
        // Removed for the same reason as the one on the direct-install tab: it was the only way to
        // reach a state SAFT cannot undo, and it saved nothing anyone had asked to save.
        RebuildDestRow = BuildBrowseRow("Output folder:", 110, out var rebuildDestBox, OnBrowseRebuildDest);
        RebuildDestBox = rebuildDestBox;
        PlaceRow(top, RebuildDestRow, ref y, 26, 4);

        InPlaceWarningText = BuildWrappedLabel(
            "This will overwrite the archives in your game install. A .img.bak backup of each original is made automatically before the first overwrite, inside the corresponding folder within the rebuilt game directory.",
            color: Color.DarkRed);
        InPlaceWarningText.Dock = DockStyle.None;
        AutoHeightWrap(InPlaceWarningText, EstimatedContentWidth);
        InPlaceWarningText.Visible = false;

        // Shown only for the in-place option, hidden for "rebuild into a new folder", so it keeps
        // its own slot rather than sharing one with the no-backup warning that used to sit here.
        var warningWidth = Math.Max(1, top.ClientSize.Width);
        InPlaceWarningText.SetBounds(0, y, warningWidth, InPlaceWarningText.Height);
        InPlaceWarningText.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        top.Controls.Add(InPlaceWarningText);
        y += InPlaceWarningText.Height + 4;

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
        var tab = new TabPage(Edition.IncludesModDeveloperTabs ? "Install Mod(s) without extraction" : "Install Mods");
        tabs.TabPages.Add(tab);

        // Scrolls rather than overflowing. The progress strip below draws over anything that spills
        // past it, so on a 544px-tall screen the Install button was being half-covered. A scrollbar
        // when the content genuinely doesn't fit is better than a button you can't click.
        var top = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10), AutoScroll = true, BackColor = Color.White };
        tab.Controls.Add(top);

        var y = 0;
        PlaceRow(top, BuildBrowseRow("Game folder:", 110, out var directGameFolderBox, OnBrowseDirectGameFolder), ref y, 26);
        DirectGameFolderBox = directGameFolderBox;

        PlaceRow(top, BuildBrowseRow("Mod folder:", 110, out var directModFolderBox, OnBrowseDirectModFolder), ref y, 26);
        DirectModFolderBox = directModFolderBox;

        // Backups are not optional here any more.
        //
        // The old "replace without backups" choice looked like a storage/speed trade and was neither.
        // Backups hold only the individual entries that get replaced, never whole archives - a mod
        // touching a dozen files costs a few megabytes against a 4.7 GB game - and turning them off
        // silently disabled the Uninstall tab outright, because restoring is the only thing that tab
        // does and the backup folder is the only place it can restore from. Nobody ticking a box on
        // this tab was choosing to give that up.
        DirectBackupNoticeText = BuildWrappedLabel(
            "Every original file is backed up before it is replaced. This is what the Uninstall tab " +
            "restores from, so it is always done.");
        DirectBackupNoticeText.Dock = DockStyle.None;
        AutoHeightWrap(DirectBackupNoticeText, EstimatedContentWidth);
        PlaceRow(top, DirectBackupNoticeText, ref y, DirectBackupNoticeText.Height, 4);

        DirectBackupDestRow = BuildBrowseRow("Backup folder:", 110, out var directBackupDestBox, OnBrowseDirectBackupDest);
        DirectBackupDestBox = directBackupDestBox;
        PlaceRow(top, DirectBackupDestRow, ref y, 26);

        var description = BuildWrappedLabel(
            // "without extracting them" only means something next to a tab that extracts. In the
            // two-tab build there is no such tab, so the sentence describes a contrast the reader
            // cannot see and would have to invent.
            (Edition.IncludesModDeveloperTabs
                ? "This is the fast and popular option that installs your mod files directly into the live game archives, without extracting them. "
                : "This installs your mod files directly into the live game archives. ") +
            "If you get a message about rebuilding, just say yes, its a necessary step if your mod files are any bigger than the vanilla files they replace. Audio mod files need to be in the correct nested folder structure and cannot be loose files, all other asset types can be loose. See Readme.txt for more info on audio. Adding new files to the game that weren't there before works with this option but takes a bit longer than pure file replacement and will trigger a few questions for you to answer before the installation.",
            color: Color.Gray);
        description.Dock = DockStyle.None;
        AutoHeightWrap(description, EstimatedContentWidth);
        PlaceRow(top, description, ref y, description.Height);

        DirectInstallButton = new Button { Text = "Install Mod into Game Files", Enabled = false };
        DirectInstallButton.Click += OnDirectInstall;
        PlaceButton(top, DirectInstallButton, ref y, 30);

        // The scroll region is stated explicitly rather than left for WinForms to infer from child
        // positions. Inferred, it did not account for the whole layout on a short screen, so the
        // Install button stayed half-hidden under the progress strip with no scrollbar offered.
        // Includes room below the button so it is never flush against that strip.
        top.AutoScrollMinSize = new Size(0, y + 12);

        // Progress is pinned to the bottom of the tab rather than flowing after the content above.
        // Flowed, its position depends on how tall everything else happened to render, and under
        // Winlator that pushed the bars just off the bottom edge, leaving a counter ticking up with
        // no visible bar. Docked, they are on screen whatever the content does.
        //
        // Added AFTER the content panel and deliberately NOT brought to the front. WinForms docks in
        // reverse z-order, so the later control docks first and reserves its strip, leaving the Fill
        // panel above to take exactly what remains. Calling BringToFront here inverts that: the Fill
        // panel then claims the whole tab and this strip is painted over the top of it, which is
        // what kept clipping the Install button.
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Color.White };
        tab.Controls.Add(bottom);

        var by = 4;
        DirectSubProgressText = new Label { Text = " ", Font = new Font(Font.FontFamily, 8f), ForeColor = Color.Gray };
        PlaceRow(bottom, DirectSubProgressText, ref by, 18, 2);

        DirectSubProgressBar = new ProgressBar { Height = 8 };
        PlaceRow(bottom, DirectSubProgressBar, ref by, 8, 6);

        DirectProgressBar = new ProgressBar { Height = 18 };
        PlaceRow(bottom, DirectProgressBar, ref by, 18);
    }

    // ================= TAB 5: UNINSTALL MOD(S) =================

    private void BuildUninstallTab(TabControl tabs)
    {
        var tab = new TabPage(Edition.IncludesModDeveloperTabs ? "Uninstall Mod(s)" : "Uninstall Mods");
        tabs.TabPages.Add(tab);

        // The height here is only a starting value; the panel is resized to its actual content at the
        // end of this method, so adding a row does not need it adjusted.
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

        // Directly under the folder picker, in red, because it describes what that one choice does
        // and nothing else on this tab changes it. Uninstalling restores every name-matched file in
        // the folder, and does not check whether a file needed restoring - so the folder IS the
        // selection, and a broad folder quietly takes more mods out than the user meant.
        var backupFolderWarning = BuildWrappedLabel(
            "Choose your backup folder carefully; any files in it will overwrite any of the name-matched " +
            "files in your game directory, so if you don't want certain mods uninstalled, select a more " +
            "specific backup folder that only contains the files you want uninstalled.",
            color: Color.FromArgb(170, 30, 30));
        backupFolderWarning.Dock = DockStyle.None;
        AutoHeightWrap(backupFolderWarning, EstimatedContentWidth);
        PlaceRow(top, backupFolderWarning, ref y, backupFolderWarning.Height, 4);

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
