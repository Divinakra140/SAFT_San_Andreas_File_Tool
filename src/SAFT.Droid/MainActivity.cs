// Stopwatch by name only: importing System.Diagnostics wholesale collides with Android's Activity.
using Stopwatch = System.Diagnostics.Stopwatch;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;

namespace SAFT.Droid;

/// <summary>
/// SAFT's Android front end: the five things the Windows build does, on one screen.
///
/// The engine underneath is SAFT.Core exactly as the Windows build ships it — every format reader,
/// every installer, all 288 tests, unchanged. Only the face is new, because only the face was ever
/// Windows-specific.
///
/// Two rules this screen keeps, both learned by breaking them:
///
///   1. NOTHING that touches a file runs on the UI thread. Not a walk, not a scan, not a stat. The
///      first version of this app walked storage on the UI thread and Android offered to kill it.
///   2. The user says where their folders are. SAFT does not guess, and does not go looking.
/// </summary>
/// <remarks>
/// ConfigurationChanges matters here. Android's default response to a rotation is to destroy the
/// activity and build a new one — which not only empties the screen, but does it while a job may be
/// running on a background thread, leaving that work reporting progress to views belonging to an
/// activity that no longer exists. Handling the rotation ourselves keeps the screen, the text, and
/// the running work all pointing at the same place.
/// </remarks>
[Activity(
    Label = "SAFT",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize
        | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.KeyboardHidden)]
public class MainActivity : Activity
{
    private const string Prefs = "saft";

    /// <summary>
    /// One tab. The folders it needs, an optional tick box, and what its buttons do.
    ///
    /// Describing the tabs as data rather than as five hand-built screens is what keeps this file
    /// from being five copies of the same picker-and-button code. Every tab gets the same threading,
    /// the same confirmation, the same log and the same "did you fill everything in" check, because
    /// there is only one of each.
    /// </summary>
    /// <param name="Warning">
    /// Shown in red beneath the folder rows. Carried over word for word from the Windows build
    /// rather than reworded for a smaller screen: it is the one warning on this tab that describes
    /// something irreversible, and a paraphrase would be a second version of it to keep in step.
    /// </param>
    private sealed record TabSpec(
        string Name,
        string What,
        string? Warning,
        IReadOnlyList<(string Caption, string Prompt)> Folders,
        string? OptionLabel,
        (string Caption, string Prompt)? OptionFolder,
        IReadOnlyList<ActionSpec> Actions);

    /// <param name="OwnConfirm">
    /// True when the action puts up its own dialog and does not want the generic "are you sure"
    /// first. The install does: it has real findings to show — what it replaces, what it adds, what
    /// that costs your game — and a bare confirmation in front of that would just be a door before
    /// the door.
    /// </param>
    private sealed record ActionSpec(string Label, bool Writes, bool OwnConfirm, Action<Args> Run);

    /// <summary>What an action is handed: the folders it asked for, the tick box, and where to talk.</summary>
    /// <param name="Ask">
    /// Puts a question on screen from a background thread and waits for the answer. This is how the
    /// engine's findings reach the user mid-job without any of the engine knowing what a dialog is.
    /// </param>
    private sealed record Args(
        IReadOnlyList<string> Folders,
        bool Option,
        string? OptionFolder,
        Action<string> Say,
        Func<string, string, SAFT.Core.StreamingSeverity?, bool> Ask,
        Action<string, string> Tell);

    /// <summary>
    /// The tabs, as data.
    ///
    /// Two of them, matching the released SAFT.exe rather than SAFT-Dev.exe's five — the desktop
    /// build gates the extract, install-into-extracted and rebuild tabs behind
    /// <c>Edition.IncludesModDeveloperTabs</c>, and this port ships the same pair the public one
    /// does. Player Skin Mods and Limit Adjustment arrive in 2.2, and adding them is adding two
    /// records to this list: the picker rows, the confirmation, the threading and the log are built
    /// from whatever is here.
    /// </summary>
    private static readonly IReadOnlyList<TabSpec> Tabs = new List<TabSpec>
    {
        new("Install Mods",
            "Installs a mod straight into your live game, with no extraction step.",
            null,
            new[]
            {
                ("Game folder", "Choose your Grand Theft Auto San Andreas folder"),
                ("Mod folder", "Choose the folder holding the mod"),
                ("Backup folder", "Choose where to keep the original files"),
            },
            null,
            null,
            // "Check" used to sit here beside Install. It has moved into the menu: it is a diagnostic
            // - the read-only half of an install, timed - and useful when something is behaving
            // strangely, but on the main screen it competed for attention with the one button that
            // is the point of the tab.
            new[]
            {
                new ActionSpec("Install this mod", true, true, a =>
                {
                    var analysis = InstallRunner.Analyse(
                        a.Folders[0], a.Folders[1], a.Folders[2], ModNameFrom(a.Folders[1]), a.Say);

                    var findings = analysis.Describe();
                    a.Say("\n" + findings);

                    if (analysis.Refused)
                    {
                        a.Ask("SAFT will not install this", findings + "\n\nNothing has been written.",
                            SAFT.Core.StreamingSeverity.Serious);
                        a.Say("\nNOT INSTALLED.");
                        return;
                    }

                    if (!a.Ask("Install this mod?", findings + "\n\nInstall it?", analysis.Verdict.Severity))
                    {
                        a.Say("\nCancelled. Nothing was written.");
                        return;
                    }

                    InstallRunner.Apply(analysis, a.Say, a.Tell);
                }),
            }),

        // Both lines say what the Windows build says, in a third of the words. The desktop has room
        // for a paragraph explaining what a backup folder is; a handheld does not, and the warning is
        // the half that matters - it is the one describing something that cannot be undone.
        new("Uninstall Mods",
            "Restores the vanilla files from your backup folder. Objects SAFT ADDED only come out if " +
            "that same folder holds the record written when they went in.",
            // "That folder" and "a more specific folder" both leave the reader working out WHICH
            // folder is meant, on the one line where being wrong costs them their game files. Named
            // outright, twice.
            "Choose carefully — every file in your backups folder overwrites the game file with the " +
            "same name. To uninstall fewer mods, pick a more specific backups folder.",
            new[]
            {
                ("Game folder", "Choose your Grand Theft Auto San Andreas folder"),
                ("Backup folder", "Choose the folder holding your backed-up originals"),
            },
            "Keep a copy of the mod files being removed",
            ("Keep the removed mod files in", "Choose where to keep the removed mod files"),
            new[]
            {
                new ActionSpec("Uninstall mods", true, false, a =>
                    Jobs.Uninstall(a.Folders[0], a.Folders[1], a.OptionFolder, a.Say, a.Tell, a.Ask)),
            }),
    };

    /// <summary>
    /// Chosen folders, keyed by CAPTION rather than by tab.
    ///
    /// "Game folder" means the same folder on the install tab, the uninstall tab and the extract tab,
    /// so picking it once should be enough. Keying by tab would make the user walk down to the same
    /// folder three times to do three things to one game.
    /// </summary>
    private readonly Dictionary<string, string> _folders = new(StringComparer.Ordinal);

    private readonly List<TextView> _folderLabels = new();
    private readonly List<Button> _actionButtons = new();

    private LinearLayout _tabStrip = null!;
    private LinearLayout _panel = null!;
    private TextView _output = null!;
    private FrameLayout _paneFrame = null!;
    private CheckBox? _option;
    private LinearLayout? _optionFolderRow;
    private TextView? _optionFolderLabel;

    /// <summary>The two halves that swap places between portrait and landscape.</summary>
    private LinearLayout _root = null!;
    private LinearLayout _controls = null!;
    private ScrollView _controlsScroller = null!;
    private LinearLayout _header = null!;
    private ImageButton _logo = null!;
    private ProgressBar _working = null!;
    private Android.Graphics.Drawables.AnimationDrawable _toolsFolding = null!;
    private ImageView _title = null!;

    /// <summary>Everything the user reads, in black. Grey on clouds was hard to see.</summary>
    private static readonly Android.Graphics.Color Ink = Android.Graphics.Color.Black;

    private int _tab;
    private bool _busy;

    /// <summary>Set by the engine when storage struggled; shown at the end, with the cooldown.</summary>
    private string? _storageWarning;

    /// <summary>True while the first-run permission screen is up instead of the app.</summary>
    private bool _gateShowing;

    /// <summary>The caption whose picker is open, so the result knows what it was picking.</summary>
    private string? _picking;

    private const int RequestFolder = 1;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // The black strip across the top said "SAFT" directly above the app's own title art, and
        // cost that art the space it wanted.
        //
        // Hidden in code rather than switched off with a Theme attribute on this activity. The theme
        // route is what killed the previous build: a theme is resolved while the window is being
        // built, BEFORE OnCreate runs, so anything wrong with it throws where no handler of mine can
        // see it - which is why "SAFT keeps stopping" arrived with an empty crash log. Hiding the
        // bar here is one line, inside the try, and cannot fail before there is somewhere to report
        // that it did.
        try
        {
            ActionBar?.Hide();
        }
        catch (Exception)
        {
            // A device without an action bar to hide is not a problem worth stopping for.
        }

        // An exception here used to mean the app "expanded and shot you back to the home screen" with
        // nothing to read. Now the screen it fails to build is replaced by the reason it failed.
        try
        {
            BuildUi();
        }
        catch (Exception ex)
        {
            ShowStartupFailure(ex);
        }
    }

    /// <summary>Shows the failure instead of the app, so there is something to photograph.</summary>
    private void ShowStartupFailure(Exception ex)
    {
        CrashLog.Write("OnCreate", ex);

        var text = new TextView(this)
        {
            Text = "SAFT could not start.\n\n" + CrashLog.Describe(ex) +
                   (CrashLog.LastPath is null ? "" : $"\nAlso written to:\n{CrashLog.LastPath}"),
        };
        text.SetTextIsSelectable(true);
        text.SetPadding(24, 24, 24, 24);

        var scroller = new ScrollView(this);
        scroller.AddView(text);
        SetContentView(scroller);
    }

    /// <summary>Density-independent pixels, since everything here is built in code.</summary>
    private int Dp(double dp) => (int)(dp * (Resources?.DisplayMetrics?.Density ?? 2f));

    /// <summary>
    /// The first screen is the permission screen, or there is no first screen.
    ///
    /// Without all-files access there is nothing SAFT can do — every folder it needs is outside its
    /// own sandbox — so showing the full interface with every button leading to "grant access first"
    /// would be a menu of things that do not work. One door, and it is obvious.
    /// </summary>
    private void BuildUi()
    {
        if (!HasAllFilesAccess()) BuildGate();
        else BuildMain();
    }

    /// <summary>
    /// Android does not tell an app when all-files access is granted — the user leaves for Settings,
    /// turns it on, and comes back — so the gate checks again every time the app returns to the
    /// front. That return is the only signal there is.
    /// </summary>
    protected override void OnResume()
    {
        base.OnResume();

        if (!_gateShowing || !HasAllFilesAccess()) return;

        try
        {
            BuildMain();
        }
        catch (Exception ex)
        {
            ShowStartupFailure(ex);
        }
    }

    /// <summary>The clouds, cropped to fill rather than stretched — a squashed sky is worse than a cropped one.</summary>
    private FrameLayout CloudsBackdrop(View content)
    {
        var clouds = new ImageView(this);
        clouds.SetImageResource(Resource.Drawable.saft_clouds);
        clouds.SetScaleType(ImageView.ScaleType.CenterCrop);

        var stack = new FrameLayout(this);
        stack.AddView(clouds, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        stack.AddView(content, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        return stack;
    }

    /// <summary>
    /// The first-run screen: the logo, the title, why the permission is needed, and one large button
    /// in the middle of the screen. Nothing else, because nothing else works yet.
    /// </summary>
    private void BuildGate()
    {
        _gateShowing = true;

        var column = new LinearLayout(this) { Orientation = Orientation.Vertical };
        column.SetGravity(GravityFlags.Center);
        column.SetPadding(Dp(28), Dp(20), Dp(28), Dp(20));

        // Big. This screen has one job and the artwork is most of it — and the logo carries a lot of
        // its own whitespace, so a size that sounds generous on paper reads as small on the device.
        // Sized by orientation because landscape on a handheld has barely 400dp of height to spend;
        // the column scrolls either way, but it should not need to.
        var landscape = Resources?.Configuration?.Orientation == Android.Content.Res.Orientation.Landscape;

        var logo = new ImageView(this);
        logo.SetImageResource(Resource.Drawable.saft_logo);
        logo.SetScaleType(ImageView.ScaleType.FitCenter);
        column.AddView(logo, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(landscape ? 250 : 330)));

        var title = new ImageView(this);
        title.SetImageResource(Resource.Drawable.saft_title);
        title.SetScaleType(ImageView.ScaleType.FitCenter);
        column.AddView(title, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(landscape ? 58 : 66)) { TopMargin = Dp(4) });

        var why = new TextView(this)
        {
            Text = "SAFT needs access to all files so it can reach your game — on internal storage " +
                   "or on your SD card.\n\nTurn on \"Allow access to manage all files\", then come back.",
        };
        why.SetTextSize(Android.Util.ComplexUnitType.Sp, 15);
        why.SetTextColor(Ink);

        // Gravity, not TextAlignment. TextAlignment defers to the view's inherited text direction and
        // quietly does nothing in a layout like this one; Gravity centres the lines within the view,
        // which is what was actually wanted.
        why.Gravity = GravityFlags.Center;
        why.SetPadding(Dp(8), Dp(20), Dp(8), Dp(24));
        column.AddView(why);

        var grant = BigButton("Grant file access");
        grant.SetTextSize(Android.Util.ComplexUnitType.Sp, 19);
        grant.SetMinimumHeight(Dp(72));
        grant.Click += (_, _) => RequestAllFilesAccess();
        column.AddView(grant, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // Scrolls, because this whole column does not fit on a handheld turned sideways.
        var scroller = new ScrollView(this);
        scroller.AddView(column);
        scroller.FillViewport = true;

        SetContentView(CloudsBackdrop(scroller));
    }

    private void BuildMain()
    {
        _gateShowing = false;
        LoadFolders();

        // The tool logo IS the menu button, in the actual top-left corner where the action bar used
        // to put the word "SAFT". Three grey lines would be a generic control in the one place the
        // app has to introduce itself; the swiss army knife does both jobs at once.
        _logo = new ImageButton(this);
        _logo.SetImageResource(Resource.Drawable.saft_logo);
        _logo.SetScaleType(ImageView.ScaleType.FitCenter);
        // No frame. It read as a button with one, but the artwork carries the corner better without
        // it — and now that the logo spins while work is happening, it is visibly a live thing rather
        // than decoration, which was what the border was compensating for.
        _logo.SetBackgroundColor(Android.Graphics.Color.Transparent);
        _logo.SetPadding(0, 0, 0, 0);
        _logo.Click += (_, _) => ShowMenu(_logo);

        _toolsFolding = BuildFoldingTools();

        _title = new ImageView(this);
        _title.SetImageResource(Resource.Drawable.saft_title);

        _title.SetScaleType(ImageView.ScaleType.FitCenter);

        _header = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        _header.SetGravity(GravityFlags.CenterVertical);
        _header.AddView(_logo);
        _header.AddView(_title, new LinearLayout.LayoutParams(
            0, ViewGroup.LayoutParams.MatchParent, 1f) { LeftMargin = Dp(8) });

        _tabStrip = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        _panel = new LinearLayout(this) { Orientation = Orientation.Vertical };

        // Sits directly under the title, where the eye already is, and holds its space when hidden so
        // nothing below it jumps when a job starts.
        _working = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal)
        {
            Indeterminate = true,
            Visibility = ViewStates.Invisible,
        };

        _controls = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _controls.AddView(_header);
        _controls.AddView(_working, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(10)));
        _controls.AddView(_tabStrip);
        _controls.AddView(_panel);

        // The log: ONE view.
        //
        // It was a ScrollView wrapping a TextView, wrapped again in a FrameLayout that painted the
        // background - three views tiling the same space. The log drew grey behind its text whichever
        // of them was told to be white, through a dozen builds of colours, alphas, drawables, fading
        // edges and compositing theories. A single view cannot have a seam with itself.
        //
        // Scrolling comes from ScrollingMovementMethod rather than a parent ScrollView, which is what
        // that class is for. The cost is that the text is no longer selectable - the two are mutually
        // exclusive - so "Copy log" in the menu does that job instead, and does it better than
        // dragging selection handles down two hundred lines on a handheld.
        _output = new TextView(this);
        _output.SetTextSize(Android.Util.ComplexUnitType.Sp, 13);
        _output.SetTextColor(Ink);
        _output.SetBackgroundColor(Android.Graphics.Color.White);
        _output.SetPadding(Dp(8), Dp(8), Dp(8), Dp(8));
        _output.MovementMethod = new Android.Text.Method.ScrollingMovementMethod();
        _output.VerticalScrollBarEnabled = true;
        _output.SetHorizontallyScrolling(false);

        _paneFrame = new FrameLayout(this);
        _paneFrame.SetBackgroundColor(Ink);
        _paneFrame.SetPadding(Dp(2), Dp(2), Dp(2), Dp(2));
        _paneFrame.AddView(_output, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // The controls scroll too.
        //
        // They did not, and the uninstall tab proved why: adding the warning text pushed the Uninstall
        // button off the bottom of a landscape screen, where it could not be reached at all. A tab is
        // as tall as whatever it needs to say, and some of them need to say a lot.
        _controlsScroller = new ScrollView(this);
        _controlsScroller.AddView(_controls);

        // Background REMOVED, not set to a colour.
        //
        // A ScrollView here comes with a grey backing from the theme, and SetBackgroundColor never
        // replaced it - that is the grey that haunted the log for a dozen builds while every view in
        // that stack was told to be white. Taking the log's ScrollView away made the log white and
        // moved the grey to this column, which is what finally identified it. Null clears the
        // drawable outright instead of trying to paint over it.
        _controlsScroller.Background = null;

        // And the edge effects, which only appear once the column is tall enough to scroll. The grey
        // wash came back the moment bigger text pushed the content past the viewport, which is what
        // gave this away - it was never there while everything fit.
        _controlsScroller.VerticalFadingEdgeEnabled = false;
        _controlsScroller.HorizontalFadingEdgeEnabled = false;
        _controlsScroller.OverScrollMode = OverScrollMode.Never;

        _root = new LinearLayout(this);
        _root.SetPadding(Dp(10), Dp(6), Dp(10), Dp(6));
        _root.AddView(_controlsScroller);
        _root.AddView(_paneFrame);

        SetContentView(CloudsBackdrop(_root));

        BuildTabStrip();
        ShowTab(0);
        ApplyOrientation();

        Say($"SAFT — build {BuildStamp()} — Android {Build.VERSION.Release}, {Build.SupportedAbis?.FirstOrDefault()}");
    }

    /// <summary>
    /// Behind the logo: the things you touch once, or never, or only when something is wrong.
    /// </summary>
    private void ShowMenu(View anchor)
    {
        const string CopyItem = "Copy log";
        const string VerifyItem = "Verify what's in my game";
        const string CheckItem = "Check what this mod adds";
        var accessItem = HasAllFilesAccess() ? "File access: granted" : "Grant file access";
        var buildItem = $"Build {BuildStamp()}";

        var popup = new PopupMenu(this, anchor);
        popup.Menu?.Add(CopyItem);
        popup.Menu?.Add(VerifyItem);
        popup.Menu?.Add(CheckItem);
        popup.Menu?.Add(accessItem);
        popup.Menu?.Add(buildItem);

        popup.MenuItemClick += (_, e) =>
        {
            var chosen = e.Item?.TitleFormatted?.ToString();

            if (chosen == CopyItem) CopyLog();
            else if (chosen == VerifyItem) RunVerify();
            else if (chosen == CheckItem) RunCheck();
            else if (chosen == accessItem) RequestAllFilesAccess();

            // The build line is there to be read, so that "which build is this" is answerable
            // without scrolling the log back to the top. Touching it does nothing.
        };

        popup.Show();
    }

    /// <summary>
    /// Puts the whole log on the clipboard.
    ///
    /// This is what selectable text used to be for. The log is the thing people send when something
    /// goes wrong, and a couple of hundred lines of it is not something anyone should be dragging
    /// selection handles through on a handheld.
    /// </summary>
    private void CopyLog()
    {
        var text = _output.Text ?? "";
        if (text.Length == 0)
        {
            Say("\nNothing in the log to copy yet.");
            return;
        }

        var clipboard = (ClipboardManager?)GetSystemService(ClipboardService);
        if (clipboard is null)
        {
            Say("\nThis device has no clipboard service.");
            return;
        }

        clipboard.PrimaryClip = ClipData.NewPlainText("SAFT log", text);
        Toast.MakeText(this, $"Copied {text.Split('\n').Length} lines", ToastLength.Short)?.Show();
    }

    /// <summary>
    /// The read-only half of an install, run from the menu. It writes nothing, so it needs no
    /// confirmation — and it works off whichever game and mod folder the install tab is pointed at.
    /// </summary>
    private void RunCheck()
    {
        var game = _folders.GetValueOrDefault("Game folder");
        var mod = _folders.GetValueOrDefault("Mod folder");

        if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(mod))
        {
            Say("\nChoose a game folder and a mod folder first.");
            return;
        }

        Run(new ActionSpec("Check", false, false, a => InstallRunner.Check(a.Folders[0], a.Folders[1], a.Say)),
            new Args(new[] { game, mod }, false, null, Say, Ask, Tell));
    }

    /// <summary>
    /// Opens the game and reports what is actually in it, file by file, against the mod and against
    /// the backed-up originals. Writes nothing.
    /// </summary>
    private void RunVerify()
    {
        var game = _folders.GetValueOrDefault("Game folder");
        var mod = _folders.GetValueOrDefault("Mod folder");
        var backup = _folders.GetValueOrDefault("Backup folder");

        if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(mod) || string.IsNullOrEmpty(backup))
        {
            Say("\nChoose the game folder, the mod folder and the backup folder first.");
            return;
        }

        Run(new ActionSpec("Verify", false, false,
                a => Verify.Report(a.Folders[0], a.Folders[1], a.Folders[2], a.Say)),
            new Args(new[] { game, mod, backup }, false, null, Say, Ask, Tell));
    }

    /// <summary>
    /// Two arrangements of the same pieces.
    ///
    /// PORTRAIT stacks everything: header, tabs, folder rows, buttons, and the log filling whatever
    /// is left. LANDSCAPE puts the controls and the log side by side, because on a handheld turned
    /// sideways there is barely 400dp of height — stacking would leave the log four lines tall while
    /// half the width sat empty. The header shrinks too, since vertical space is the scarce thing.
    ///
    /// This is called on rotation as well as at startup. The activity is not rebuilt when the device
    /// turns (see the ConfigurationChanges on the attribute above), so a running job keeps running
    /// and the log keeps its text — only the arrangement changes.
    /// </summary>
    private void ApplyOrientation()
    {
        var landscape = Resources?.Configuration?.Orientation == Android.Content.Res.Orientation.Landscape;

        _root.Orientation = landscape ? Orientation.Horizontal : Orientation.Vertical;

        // The log does not need half the screen. The controls are what the user is aiming at, so
        // they get the larger share and the log gets what is left, which is still far more than the
        // few lines it needs at a glance - it scrolls, and it is all still there.
        // Weighted in BOTH orientations rather than wrapping in portrait: a wrapping column sizes to
        // its content and can push the log off the bottom, which is the same way the Uninstall button
        // went missing sideways. Weighted, everything always fits and the overflow scrolls.
        // Portrait sizes the controls to their CONTENT rather than to a share of the screen. A fixed
        // share left a band of empty scroller under the last button and squeezed the log for no
        // reason; wrapping puts the log's top edge directly beneath the action button and gives it
        // everything below.
        _controlsScroller.LayoutParameters = landscape
            ? new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1.5f)
            : new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

        // In landscape the log sits beside the controls, so its top would otherwise line up with the
        // top of the title. It is inset instead, by the SAME amount top and bottom — which both keeps
        // the title's own top edge clear and leaves the log visually centred in the screen rather
        // than hanging from the top of it.
        _paneFrame.LayoutParameters = landscape
            ? new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f)
                { LeftMargin = Dp(10), TopMargin = Dp(26), BottomMargin = Dp(26) }
            : new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f) { TopMargin = Dp(8) };

        // A floor under the log, so a tab whose controls are unusually tall cannot squeeze it to
        // nothing - it scrolls, but it has to be visible enough to be worth scrolling.
        _paneFrame.SetMinimumHeight(Dp(landscape ? 0 : 160));

        // Taller than before, because the action bar that used to sit above it is gone and the title
        // art is the thing worth spending that space on.
        var headerHeight = Dp(landscape ? 96 : 110);
        _header.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, headerHeight);

        // Square, and filling the header now that there is no border around it to leave room for.
        var logoSize = headerHeight;
        _logo.LayoutParameters = new LinearLayout.LayoutParams(logoSize, logoSize);
    }

    public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);

        // The gate sizes its artwork by orientation, so turning the device while it is up means
        // rebuilding it. It holds no state worth preserving - it is one button.
        if (_gateShowing) BuildGate();
        else ApplyOrientation();
    }

    /// <summary>
    /// A button sized for a thumb on a handheld rather than a mouse on a desktop. 48dp is Android's
    /// stated minimum for a touch target; these are bigger, because the alternative to hitting the
    /// right one is rewriting a 900 MB archive you did not mean to touch.
    /// </summary>
    /// <summary>
    /// A white face with a black edge, which is every button in SAFT.
    ///
    /// The default button background is the theme's grey, and the tab faces were built with
    /// Color.Argb - which does not render as asked on this device, the same trap that hid the log's
    /// grey for a dozen builds. Color.White is a named property and does.
    /// </summary>
    private Android.Graphics.Drawables.GradientDrawable ButtonFace(int strokeDp)
    {
        var face = new Android.Graphics.Drawables.GradientDrawable();
        face.SetColor(Android.Graphics.Color.White);
        face.SetCornerRadius(Dp(4));
        face.SetStroke(Dp(strokeDp), Ink);
        return face;
    }

    private Button BigButton(string text)
    {
        var button = new Button(this) { Text = text };
        button.SetTextSize(Android.Util.ComplexUnitType.Sp, 13);
        button.SetMinimumHeight(Dp(36));
        button.SetTextColor(Ink);
        button.Background = ButtonFace(1);
        return button;
    }

    private void BuildTabStrip()
    {
        _tabStrip.RemoveAllViews();

        for (var i = 0; i < Tabs.Count; i++)
        {
            var index = i;
            var button = BigButton(Tabs[i].Name);
            button.Click += (_, _) => ShowTab(index);
            _tabStrip.AddView(button, new LinearLayout.LayoutParams(
                0, ViewGroup.LayoutParams.WrapContent, 1f));
        }
    }

    /// <summary>Rebuilds the panel for one tab: its folder rows, its tick box, its buttons.</summary>
    private void ShowTab(int index)
    {
        _tab = index;
        var spec = Tabs[index];

        _panel.RemoveAllViews();
        _folderLabels.Clear();
        _actionButtons.Clear();
        _option = null;
        _optionFolderRow = null;
        _optionFolderLabel = null;

        // The selected tab is the OUTLINED one, not the bold one.
        //
        // Bold-versus-regular was the wrong signal: it made the unselected tab look greyed out, and
        // greyed out means unpressable. Both now read as equally live - black, bold, identical - and
        // selection is carried by a thick dark border, which says "you are here" rather than "that
        // one is off".
        //
        // Buttons rather than a real tab widget, because a row of them behaves better under a thumb
        // than a TabHost on a handheld.
        for (var i = 0; i < _tabStrip.ChildCount; i++)
        {
            if (_tabStrip.GetChildAt(i) is not Button tab) continue;

            tab.Background = ButtonFace(i == index ? 4 : 1);
            tab.SetTextColor(Ink);
            tab.SetTypeface(null, Android.Graphics.TypefaceStyle.Bold);
        }

        var what = new TextView(this) { Text = spec.What };
        what.SetPadding(0, Dp(4), 0, 0);
        what.SetTextSize(Android.Util.ComplexUnitType.Sp, 15);
        what.SetTextColor(Ink);
        what.SetTypeface(null, Android.Graphics.TypefaceStyle.Bold);
        _panel.AddView(what);

        foreach (var (caption, prompt) in spec.Folders)
            _folderLabels.Add(AddPickerRow(caption, prompt).Label);

        if (spec.Warning is not null)
        {
            var warning = new TextView(this) { Text = spec.Warning };
            warning.SetTextSize(Android.Util.ComplexUnitType.Sp, 15);
            warning.SetTextColor(Android.Graphics.Color.Rgb(170, 30, 30));
            warning.SetTypeface(null, Android.Graphics.TypefaceStyle.Bold);
            warning.SetPadding(0, Dp(10), 0, Dp(2));
            _panel.AddView(warning);
        }

        if (spec.OptionLabel is not null)
        {
            _option = new CheckBox(this) { Text = spec.OptionLabel };
            _option.SetTextColor(Ink);
            _option.SetTextSize(Android.Util.ComplexUnitType.Sp, 15);
            _option.SetTypeface(null, Android.Graphics.TypefaceStyle.Bold);
            _panel.AddView(_option);

            if (spec.OptionFolder is { } optional)
            {
                // Present but hidden until it is relevant. A folder field for something you have not
                // asked for is one more thing to read past on a screen that already has enough.
                var (container, label) = AddPickerRow(optional.Caption, optional.Prompt);
                _optionFolderRow = container;
                _optionFolderLabel = label;
                container.Visibility = ViewStates.Gone;

                _option.CheckedChange += (_, e) =>
                    container.Visibility = e.IsChecked ? ViewStates.Visible : ViewStates.Gone;
            }
        }

        var buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        foreach (var action in spec.Actions)
        {
            var button = BigButton(action.Label);
            button.Click += (_, _) => Start(action);

            // The action that writes gets the wider half. On the install tab that is "Install this
            // mod" against "Check", which is the right way round: one is the point of the screen.
            buttons.AddView(button, new LinearLayout.LayoutParams(
                0, ViewGroup.LayoutParams.WrapContent, action.Writes ? 2f : 1f));
            _actionButtons.Add(button);
        }

        // A lone action button is centred and narrow. Two of them share the row and earn its full
        // width; one stretched across the column just looks like the screen is missing something
        // beside it. Equal spacers either side do the centring, so the button keeps its weight and
        // the row stays a plain LinearLayout.
        if (spec.Actions.Count == 1)
        {
            buttons.AddView(new View(this), 0, new LinearLayout.LayoutParams(0, 1, 0.75f));
            buttons.AddView(new View(this), new LinearLayout.LayoutParams(0, 1, 0.75f));
        }

        _panel.AddView(buttons);

        ShowChosenFolders();
        SetBusy(_busy);
    }

    /// <summary>
    /// A caption, the folder under it, and a Choose button — wrapped in one container so the whole
    /// row can be shown or hidden as a unit.
    /// </summary>
    /// <summary>
    /// Two lines per folder: the name with its Choose button immediately beside it, and the chosen
    /// path underneath.
    ///
    /// The button used to be pinned to the right edge, a screen's width away from the caption it
    /// belonged to, with the path stretched between them — so "Game folder" and the Choose button
    /// sitting nearest to it were on different rows, and picking the wrong one was easy. Putting the
    /// button against its own caption removes the guess: the pairing is what you see, not something
    /// you work out.
    /// </summary>
    private (LinearLayout Container, TextView Label) AddPickerRow(string caption, string prompt)
    {
        var container = new LinearLayout(this) { Orientation = Orientation.Vertical };
        container.SetPadding(0, Dp(6), 0, 0);

        var top = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        top.SetGravity(GravityFlags.CenterVertical);

        var heading = new TextView(this) { Text = caption };
        heading.SetTextSize(Android.Util.ComplexUnitType.Sp, 20);
        heading.SetTypeface(null, Android.Graphics.TypefaceStyle.Bold);
        heading.SetTextColor(Ink);
        top.AddView(heading);

        var button = new Button(this) { Text = "Choose" };
        button.SetMinimumHeight(Dp(40));
        button.SetTextColor(Ink);
        button.Background = ButtonFace(1);
        button.Click += (_, _) => PickFolder(caption, prompt);
        top.AddView(button, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { LeftMargin = Dp(10) });

        container.AddView(top);

        // One line, ellipsised in the MIDDLE. A path's two informative ends are the volume it is on
        // and the folder it names; the twelve characters in between are what can be spared.
        var label = new TextView(this);
        label.SetTextSize(Android.Util.ComplexUnitType.Sp, 15);
        label.SetTextColor(Ink);
        label.SetTypeface(null, Android.Graphics.TypefaceStyle.Bold);
        label.SetSingleLine(true);
        label.Ellipsize = Android.Text.TextUtils.TruncateAt.Middle;
        container.AddView(label);

        _panel.AddView(container);
        return (container, label);
    }

    private void PickFolder(string caption, string prompt)
    {
        if (!HasAllFilesAccess())
        {
            Say("\nFile access is not granted, so there is nothing to browse. Grant it first.");
            return;
        }

        _picking = caption;

        var intent = new Intent(this, typeof(FolderPickerActivity));
        intent.PutExtra(FolderPickerActivity.ExtraPrompt, prompt);

        // Reopening where the last pick left off, because these folders are usually neighbours and
        // nobody wants to walk down from the volume list every time.
        var start = _folders.GetValueOrDefault(caption) ?? _folders.Values.FirstOrDefault();
        if (!string.IsNullOrEmpty(start)) intent.PutExtra(FolderPickerActivity.ExtraStartPath, start);

        StartActivityForResult(intent, RequestFolder);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != Result.Ok || _picking is null) return;

        var chosen = data?.GetStringExtra(FolderPickerActivity.ExtraChosenPath);
        if (string.IsNullOrEmpty(chosen)) return;

        _folders[_picking] = chosen;
        _picking = null;

        SaveFolders();
        ShowChosenFolders();
    }

    private void ShowChosenFolders()
    {
        var spec = Tabs[_tab];

        for (var i = 0; i < _folderLabels.Count && i < spec.Folders.Count; i++)
            _folderLabels[i].Text = _folders.GetValueOrDefault(spec.Folders[i].Caption) ?? "not chosen";

        if (_optionFolderLabel is not null && spec.OptionFolder is { } optional)
            _optionFolderLabel.Text = _folders.GetValueOrDefault(optional.Caption) ?? "not chosen";
    }

    private void LoadFolders()
    {
        var prefs = GetSharedPreferences(Prefs, FileCreationMode.Private);
        if (prefs is null) return;

        foreach (var caption in Tabs.SelectMany(t => t.Folders).Select(f => f.Caption).Distinct())
        {
            var value = prefs.GetString(caption, null);
            if (!string.IsNullOrEmpty(value)) _folders[caption] = value;
        }
    }

    private void SaveFolders()
    {
        var editor = GetSharedPreferences(Prefs, FileCreationMode.Private)?.Edit();
        if (editor is null) return;

        foreach (var (caption, path) in _folders) editor.PutString(caption, path);
        editor.Apply();
    }

    /// <summary>
    /// Asks before running, then runs it off the UI thread.
    ///
    /// Anything that writes gets named in the dialog along with the folder it writes to. That dialog
    /// is the last moment at which the wrong folder is a mistake rather than an afternoon of putting
    /// it back — and on this screen the same "Game folder" is shared by three tabs, so it is worth
    /// showing what is about to happen to what.
    /// </summary>
    private void Start(ActionSpec action)
    {
        var spec = Tabs[_tab];

        var folders = new List<string>();
        foreach (var (caption, _) in spec.Folders)
        {
            var path = _folders.GetValueOrDefault(caption);
            if (string.IsNullOrEmpty(path))
            {
                Say($"\nChoose a {caption.ToLowerInvariant()} first.");
                return;
            }

            folders.Add(path);
        }

        var option = _option?.Checked ?? false;
        string? optionFolder = null;

        // Only required when the tick box that reveals it is ticked - and then genuinely required,
        // because the alternative is quietly not keeping files the user asked to keep.
        if (option && spec.OptionFolder is { } optional)
        {
            optionFolder = _folders.GetValueOrDefault(optional.Caption);
            if (string.IsNullOrEmpty(optionFolder))
            {
                Say($"\nChoose where to keep the removed mod files, or untick the box.");
                return;
            }
        }

        var args = new Args(folders, option, optionFolder, Say, Ask, Tell);

        // An action that shows its own findings does not get a bare confirmation in front of it.
        if (!action.Writes || action.OwnConfirm)
        {
            Run(action, args);
            return;
        }

        var lines = spec.Folders.Select((f, i) => $"{f.Caption}:\n{folders[i]}").ToList();
        if (optionFolder is not null && spec.OptionFolder is { } shown)
            lines.Add($"{shown.Caption}:\n{optionFolder}");

        new AlertDialog.Builder(this)
            .SetTitle(action.Label + "?")!
            .SetMessage(string.Join("\n", lines) + "\n\nThis writes to your files. Do not close SAFT while it runs.")!
            .SetPositiveButton("Go", (_, _) => Run(action, args))!
            .SetNegativeButton("Cancel", (System.EventHandler<DialogClickEventArgs>?)null)!
            .Show();
    }

    /// <summary>
    /// Asks a question from a background thread and waits for the answer.
    ///
    /// The job that calls this is not on the UI thread and cannot show a dialog; the UI thread cannot
    /// be made to wait for one without freezing. So the dialog is posted to the UI thread and the
    /// BACKGROUND thread blocks on the result — which is safe precisely because it is the background
    /// one. The dialog is not cancellable by tapping outside it: a half-answered question about
    /// whether to write to somebody's game is not a state worth having.
    /// </summary>
    private bool Ask(string title, string message, SAFT.Core.StreamingSeverity? severity = null)
    {
        var answer = new TaskCompletionSource<bool>();

        RunOnUiThread(() =>
        {
            var builder = new AlertDialog.Builder(this)
                .SetTitle(title)!
                .SetMessage(message)!
                .SetCancelable(false)!
                .SetPositiveButton("Continue", (_, _) => answer.TrySetResult(true))!
                .SetNegativeButton("Cancel", (_, _) => answer.TrySetResult(false))!;

            if (severity is { } level) builder = builder.SetIcon(SeverityIcon(level))!;

            builder.Show();
        });

        return answer.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// The traffic light, drawn rather than shipped as three more PNGs.
    ///
    /// Green tick, amber "i", red "!" — the same three states StreamingAdvice already reports, so the
    /// severity is legible before a word of the message is read. Drawn in code because it is two
    /// shapes and a letter, and three drawables at four densities each is twelve files to keep in
    /// step with an enum that has three values.
    /// </summary>
    private Android.Graphics.Drawables.Drawable SeverityIcon(SAFT.Core.StreamingSeverity severity)
    {
        var (colour, glyph) = severity switch
        {
            SAFT.Core.StreamingSeverity.Fine => (Android.Graphics.Color.Argb(255, 34, 139, 34), "✓"),
            SAFT.Core.StreamingSeverity.Caution => (Android.Graphics.Color.Argb(255, 218, 145, 0), "i"),
            _ => (Android.Graphics.Color.Argb(255, 190, 30, 30), "!"),
        };

        var size = Dp(48);
        var bitmap = Android.Graphics.Bitmap.CreateBitmap(size, size, Android.Graphics.Bitmap.Config.Argb8888!)!;
        var canvas = new Android.Graphics.Canvas(bitmap);

        using var fill = new Android.Graphics.Paint { AntiAlias = true, Color = colour };
        canvas.DrawCircle(size / 2f, size / 2f, size / 2f, fill);

        using var ink = new Android.Graphics.Paint
        {
            AntiAlias = true,
            Color = Android.Graphics.Color.White,
            TextSize = size * 0.62f,
            TextAlign = Android.Graphics.Paint.Align.Center,
        };
        ink.SetTypeface(Android.Graphics.Typeface.Create(
            Android.Graphics.Typeface.Default, Android.Graphics.TypefaceStyle.Bold));

        // Centred on the glyph's own box rather than on the font's line box, so a tick and an
        // exclamation mark both sit in the middle of the circle instead of near the baseline.
        var bounds = new Android.Graphics.Rect();
        ink.GetTextBounds(glyph, 0, glyph.Length, bounds);
        canvas.DrawText(glyph, size / 2f, size / 2f + bounds.Height() / 2f, ink);

        return new Android.Graphics.Drawables.BitmapDrawable(Resources, bitmap);
    }

    /// <summary>
    /// Recorded rather than shown. The engine calls this the moment it notices the storage struggled,
    /// which is mid-job; putting a dialog up then would interrupt work still in progress. It is shown
    /// at the end instead, inside the cooldown that follows every write.
    /// </summary>
    private void Tell(string title, string message) => _storageWarning = message;

    /// <summary>
    /// A minute's pause after anything that wrote to the game, with the option to skip it.
    ///
    /// SAFT can now finish an install in seconds, and that is the problem: an SD card that has just
    /// taken a burst of writes is still flushing them long after SAFT says it is done, and a game
    /// launched into that stalls on its first load. The user blames the mod, because the mod is what
    /// changed. Being fast made this MORE likely, not less - the tool now gets out of the way while
    /// the card is still busy.
    ///
    /// Sixty seconds is a pause, not a fix. It is dismissible on purpose: someone installing three
    /// mods in a row should not be made to sit through it each time, and someone whose game is on
    /// internal storage does not need it at all. The point is that nobody launches the game without
    /// having been told.
    /// </summary>
    /// <param name="seconds">
    /// Zero means no countdown - just the warning. That is the internal-storage case: there is
    /// nothing to wait for, but if the write was measurably slow it is still worth saying so.
    /// </param>
    private void ShowCooldown(int seconds)
    {
        var remaining = seconds;

        // Taken once, up front. Read from the field inside Text() and it vanishes on the first tick,
        // because the field is cleared as soon as the dialog is shown - so the real warning appeared
        // for one second and then reverted to the generic line.
        var warning = _storageWarning;
        _storageWarning = null;

        var body = new TextView(this);
        body.SetPadding(Dp(24), Dp(20), Dp(24), Dp(8));
        body.SetTextSize(Android.Util.ComplexUnitType.Sp, 15);
        body.SetTextColor(Ink);

        string Text() =>
            (warning is null
                ? "Your game is on an SD card, and it is still finishing these writes. Give it a " +
                  "moment before you launch, or the first load can stall.\n\n"
                : warning + "\n\n") +
            (seconds <= 0 ? "" : remaining > 0 ? $"Ready in {remaining}s" : "Ready — you can launch the game.");

        body.Text = Text();

        var dialog = new AlertDialog.Builder(this)
            .SetTitle(warning is not null ? "Your SD card is struggling" : "Let your SD card settle")!
            .SetView(body)!
            .SetCancelable(true)!
            .SetPositiveButton("Dismiss", (_, _) => { })!
            .Create()!;

        dialog.Show();

        // Ticks on the UI thread and stops itself the moment the dialog goes away, so dismissing it
        // early does not leave a timer running against a window that no longer exists.
        var handler = new Android.OS.Handler(Android.OS.Looper.MainLooper!);
        void Tick()
        {
            if (!dialog.IsShowing) return;

            remaining--;
            body.Text = Text();

            if (remaining > 0) handler.PostDelayed(Tick, 1000);
        }

        if (seconds > 0) handler.PostDelayed(Tick, 1000);
    }

    /// <summary>
    /// Is this folder on an SD card rather than internal storage?
    ///
    /// This decides whether the cooldown means anything. An SD card is still flushing writes after
    /// SAFT reports finished, and a game launched into that stalls on its first load; internal
    /// storage does not behave that way, so making somebody wait for it would be a minute of nothing
    /// followed by a habit of ignoring the dialog.
    ///
    /// Android puts internal shared storage under /storage/emulated/0 and mounts every removable
    /// volume as /storage/&lt;id&gt;, so the path is the answer.
    /// </summary>
    private static bool IsOnRemovableStorage(string path)
    {
        var internalRoot = Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
        if (!string.IsNullOrEmpty(internalRoot) &&
            path.StartsWith(internalRoot, StringComparison.OrdinalIgnoreCase))
            return false;

        return path.StartsWith("/storage/", StringComparison.OrdinalIgnoreCase);
    }

    private void Run(ActionSpec action, Args args)
    {
        _output.Text = "";
        SetBusy(true);

        // Keeps the screen on for the duration. A 900 MB archive rewrite outlasts the screen timeout,
        // and a device that sleeps mid-job is a device throttling its CPU through the one operation
        // that should not be interrupted.
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);

        Say($"{action.Label}…\n");

        Task.Run(() =>
        {
            var clock = Stopwatch.StartNew();
            try
            {
                action.Run(args);
                Say($"\ntook {clock.Elapsed.TotalSeconds:N1} s");

                // After anything that wrote - but only where the wait means something. See below.
                if (action.Writes && args.Folders.Count > 0)
                {
                    var onSdCard = IsOnRemovableStorage(args.Folders[0]);
                    if (onSdCard || _storageWarning is not null)
                        RunOnUiThread(() => ShowCooldown(onSdCard ? 60 : 0));
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write(action.Label, ex);
                Say($"\nFAILED: {ex.GetType().Name}: {ex.Message}");
                if (CrashLog.LastPath is not null) Say($"Written to {CrashLog.LastPath}");
            }
            finally
            {
                RunOnUiThread(() =>
                {
                    SetBusy(false);
                    Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
                });
            }
        });
    }

    /// <summary>
    /// Locks the controls while a job runs. Two jobs at once over the same archive is not a race
    /// worth having, and the tab strip stays live only so the log can still be read.
    /// </summary>
    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var button in _actionButtons) button.Enabled = !busy;
        SpinLogo(busy);
    }

    /// <summary>
    /// Shows the working bar while a job runs.
    ///
    /// This was a spinning logo, which moved constantly and was unpleasant to sit and watch for the
    /// minute a rewrite can take. A bar says the same thing - something is happening, do not close
    /// this - without asking anyone to look at it.
    /// </summary>
    private void SpinLogo(bool working)
    {
        _working.Visibility = working ? ViewStates.Visible : ViewStates.Invisible;

        if (working)
        {
            _logo.SetImageDrawable(_toolsFolding);
            _toolsFolding.Start();
        }
        else
        {
            _toolsFolding.Stop();
            _logo.SetImageResource(Resource.Drawable.saft_logo);
        }
    }

    /// <summary>
    /// The tool folding its blades in and out, played while a job runs.
    ///
    /// Eight hand-drawn frames of the logo rather than a generic spinner, and it says the same thing
    /// the bar underneath says - something is happening, do not close this - in the app's own voice.
    /// It replaced a rotating copy of the logo, which moved constantly and was unpleasant to watch
    /// for the minute an archive rewrite can take. This one has a rhythm instead of a spin.
    ///
    /// Built once and reused: an AnimationDrawable holds all eight bitmaps, and building a fresh one
    /// every time a job started would decode them again each time.
    /// </summary>
    private Android.Graphics.Drawables.AnimationDrawable BuildFoldingTools()
    {
        var frames = new[]
        {
            Resource.Drawable.saft_logo_01, Resource.Drawable.saft_logo_02,
            Resource.Drawable.saft_logo_03, Resource.Drawable.saft_logo_04,
            Resource.Drawable.saft_logo_05, Resource.Drawable.saft_logo_06,
            Resource.Drawable.saft_logo_07, Resource.Drawable.saft_logo_08,
        };

        var animation = new Android.Graphics.Drawables.AnimationDrawable();
        foreach (var frame in frames)
        {
            var drawable = Resources?.GetDrawable(frame, Theme);
            if (drawable is not null) animation.AddFrame(drawable, 110);
        }

        animation.OneShot = false;
        return animation;
    }

    private static string ModNameFrom(string modFolder) => Path.GetFileName(modFolder.TrimEnd('/'));

    /// <summary>
    /// When this APK was built, printed on the first line.
    ///
    /// It exists because "did the new build actually install" is otherwise unanswerable from the
    /// device, and this project has twice gone round a full test cycle on a stale artifact believing
    /// it was the new one. A build that cannot identify itself wastes the time of whoever tests it.
    /// </summary>
    private string BuildStamp()
    {
        try
        {
            return PackageManager?.GetPackageInfo(PackageName!, 0)?.VersionName ?? "unknown";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static bool HasAllFilesAccess() =>
        Build.VERSION.SdkInt < BuildVersionCodes.R || Android.OS.Environment.IsExternalStorageManager;

    /// <summary>
    /// Sends the user to the system screen for all-files access. It cannot be granted by a prompt —
    /// Android insists the user turns it on themselves, which is the price of it being powerful
    /// enough to rewrite a game archive in place.
    /// </summary>
    private void RequestAllFilesAccess()
    {
        if (HasAllFilesAccess())
        {
            Say("Already granted.");
            return;
        }

        try
        {
            var intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission,
                Android.Net.Uri.Parse("package:" + PackageName));
            StartActivity(intent);
        }
        catch (Exception ex)
        {
            Say($"Could not open the permission screen ({ex.GetType().Name}). " +
                "Settings > Apps > SAFT > Permissions > All files access.");
        }
    }

    /// <summary>
    /// Appends a line and follows it down. Safe to call from any thread, which matters because most
    /// callers are background ones and SAFT.Core's own progress callbacks arrive on whichever thread
    /// happens to be doing the work.
    /// </summary>
    private void Say(string line) => RunOnUiThread(() =>
    {
        _output.Text += line + "\n";
        // Follow the tail. A TextView scrolled by ScrollingMovementMethod has no FullScroll, so the
        // bottom is worked out from the laid-out text - and only once it HAS been laid out, which is
        // why this is posted rather than run inline.
        _output.Post(() =>
        {
            var laid = _output.Layout;
            if (laid is null) return;

            var bottom = laid.GetLineTop(_output.LineCount) - _output.Height
                       + _output.PaddingTop + _output.PaddingBottom;
            _output.ScrollTo(0, bottom > 0 ? bottom : 0);
        });
    });
}
