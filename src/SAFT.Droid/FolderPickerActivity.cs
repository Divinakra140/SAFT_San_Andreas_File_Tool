using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace SAFT.Droid;

/// <summary>
/// A plain folder browser: volumes at the top level, folders below that, and a button that says
/// "use this one".
///
/// SAFT builds its own rather than using Android's document picker, for one reason that decides it:
/// the document picker hands back an opaque URI, and SAFT.Core works in file paths. Every format
/// reader, every installer, all 288 tests take a path and open it. Translating a URI into something
/// that can seek around a 900 MB archive means either copying the whole game somewhere first or
/// rewriting the engine, and neither is happening. With all-files access granted, ordinary paths
/// work — so the picker deals in ordinary paths.
///
/// It is also what the user expects. Every sideloaded app that touches a game folder - Winlator,
/// JoiPlay, RetroArch, Dolphin - opens a browser and lets you point at it. Guessing where the game
/// lives is not a feature; it is a way of being wrong about someone's storage layout.
/// </summary>
/// <remarks>
/// ConfigurationChanges lists what this screen handles itself. Without it, Android's answer to a
/// rotated device is to destroy the activity and build a new one, which threw away the folder being
/// browsed and left an empty list. Since the layout is built in code and reads the same either way
/// up, there is nothing to rebuild - so we keep the screen and let it turn.
/// </remarks>
[Activity(
    Label = "Choose a folder",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize
        | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.KeyboardHidden)]
public class FolderPickerActivity : Activity
{
    public const string ExtraStartPath = "saft.start_path";
    public const string ExtraPrompt = "saft.prompt";
    public const string ExtraChosenPath = "saft.chosen_path";

    /// <summary>Null means the volume list — the level above any real path.</summary>
    private string? _current;

    private TextView _pathLabel = null!;
    private ListView _list = null!;
    private Button _useThis = null!;
    private Button _up = null!;
    private List<Entry> _entries = new();

    private sealed record Entry(string Label, string? Path, bool IsUp);

    /// <summary>
    /// The row that goes up a level.
    ///
    /// It used to read ".." — correct to anyone who has used a terminal, and close to invisible to
    /// everyone else. It is the one row in the list that does something different from all the
    /// others, so it says what it does, in a bigger typeface than the folders below it.
    ///
    /// The glyph is ▲ rather than ⬆ because the arrow's head is what reads as "up" at a glance, and
    /// ⬆ renders on this device as a thin stem with a small point on top — closer to a line than an
    /// arrow. ▲ is all head.
    /// </summary>
    private const string UpLabel = "▲   Up one folder";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var prompt = Intent?.GetStringExtra(ExtraPrompt) ?? "Choose a folder";
        Title = prompt;

        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.SetPadding(16, 16, 16, 16);

        _pathLabel = new TextView(this) { Text = prompt };
        _pathLabel.SetPadding(8, 8, 8, 16);
        layout.AddView(_pathLabel);

        // Both actions sit in a fixed row rather than only in the list, because a folder with sixty
        // subfolders scrolls the way out of it off the top of the screen.
        var actions = new LinearLayout(this) { Orientation = Orientation.Horizontal };

        _up = new Button(this) { Text = "▲  Up" };
        _up.Click += (_, _) => GoUp();
        actions.AddView(_up, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        _useThis = new Button(this) { Text = "Use this folder" };
        _useThis.Click += (_, _) => ChooseCurrent();
        actions.AddView(_useThis, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 2f));

        layout.AddView(actions);

        _list = new ListView(this);
        _list.ItemClick += (_, e) => Open(_entries[e.Position]);
        layout.AddView(_list, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));

        SetContentView(layout);

        var start = Intent?.GetStringExtra(ExtraStartPath);
        Show(!string.IsNullOrEmpty(start) && Directory.Exists(start) ? start : null);
    }

    private void ChooseCurrent()
    {
        if (_current is null)
        {
            Toast.MakeText(this, "Open a volume first", ToastLength.Short)?.Show();
            return;
        }

        var data = new Intent();
        data.PutExtra(ExtraChosenPath, _current);
        SetResult(Result.Ok, data);
        Finish();
    }

    private void Open(Entry entry)
    {
        if (entry.IsUp) GoUp();
        else if (entry.Path is not null) Show(entry.Path);
    }

    /// <summary>
    /// Stepping up out of a volume root lands on the volume list rather than /storage, which is a
    /// real directory but not one that means anything to somebody looking for their game.
    /// </summary>
    private void GoUp()
    {
        if (_current is null) return;

        var parent = Path.GetDirectoryName(_current);
        Show(string.IsNullOrEmpty(parent) || parent == "/storage" || parent == "/" ? null : parent);
    }

    /// <summary>
    /// Lists a folder OFF the UI thread, then draws the result on it.
    ///
    /// The split is not ceremony. A folder on a slow SD card can take seconds to enumerate, and
    /// Android judges an app unresponsive after five - the first version of this probe walked
    /// storage on the UI thread and earned exactly that dialog. Listing is I/O; the UI thread's job
    /// is to draw.
    /// </summary>
    private void Show(string? folder)
    {
        _current = folder;
        _pathLabel.Text = folder is null ? "Storage volumes" : folder;
        _useThis.Enabled = folder is not null;
        _up.Enabled = folder is not null;

        Task.Run(() =>
        {
            var entries = folder is null ? Volumes() : Children(folder);
            RunOnUiThread(() =>
            {
                _entries = entries;
                _list.Adapter = new FolderRows(this, entries.Select(e => e.Label).ToList(), entries[0].IsUp);
            });
        });
    }

    /// <summary>
    /// Internal storage and every removable volume, named so a person can tell them apart. Android
    /// mounts an SD card as a volume id like 6432-3163, which is accurate and useless.
    /// </summary>
    private static List<Entry> Volumes()
    {
        var entries = new List<Entry>();

        var shared = Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
        if (!string.IsNullOrEmpty(shared) && Directory.Exists(shared))
            entries.Add(new Entry("Internal storage", shared, false));

        try
        {
            foreach (var dir in Directory.GetDirectories("/storage").OrderBy(d => d))
            {
                var name = Path.GetFileName(dir);
                if (name is "emulated" or "self") continue;
                if (!Directory.Exists(dir)) continue;
                entries.Add(new Entry($"SD card  ({name})", dir, false));
            }
        }
        catch (Exception)
        {
            // A volume we are not allowed to list is simply not offered, the same rule the Windows
            // build follows for folders it cannot read.
        }

        if (entries.Count == 0)
            entries.Add(new Entry("No storage visible — is all-files access granted?", null, false));

        return entries;
    }

    private static List<Entry> Children(string folder)
    {
        var entries = new List<Entry> { new(UpLabel, null, true) };

        try
        {
            foreach (var sub in Directory.GetDirectories(folder).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.')) continue;
                entries.Add(new Entry(name, sub, false));
            }
        }
        catch (Exception ex)
        {
            entries.Add(new Entry($"[cannot open: {ex.GetType().Name}]", null, false));
        }

        return entries;
    }

    /// <summary>
    /// Draws the list, giving the "up one folder" row its own weight.
    ///
    /// Every row shares one layout, so the size and boldness are set on BOTH branches rather than
    /// only on the up row — Android recycles these views as the list scrolls, and a row styled once
    /// and never unstyled would hand its appearance to whichever folder scrolled into its place.
    /// </summary>
    private sealed class FolderRows(Context context, IList<string> labels, bool firstIsUp)
        : ArrayAdapter<string>(context, Android.Resource.Layout.SimpleListItem1, labels)
    {
        public override View GetView(int position, View? convertView, ViewGroup parent)
        {
            var view = base.GetView(position, convertView, parent);
            if (view is TextView row)
            {
                var isUp = firstIsUp && position == 0;
                row.SetTextSize(Android.Util.ComplexUnitType.Sp, isUp ? 22 : 17);
                row.SetTypeface(null, isUp ? Android.Graphics.TypefaceStyle.Bold : Android.Graphics.TypefaceStyle.Normal);
            }

            return view;
        }
    }

    public override void OnBackPressed()
    {
        // Back walks up the tree before it leaves the picker, which is what the button next to the
        // screen is for on a handheld.
        if (_current is not null)
        {
            GoUp();
            return;
        }

        base.OnBackPressed();
    }
}
