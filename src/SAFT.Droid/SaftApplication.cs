using Android.App;
using Android.Runtime;

namespace SAFT.Droid;

/// <summary>
/// Exists for one reason: to install the crash handlers before anything else runs.
///
/// They used to go in at the top of MainActivity.OnCreate, which is too late for a whole class of
/// failure. Android resolves an activity's theme and builds its window BEFORE the first line of
/// OnCreate — so a bad theme reference, or anything else that goes wrong on the way in, threw with
/// no handler attached and no try/catch reached. The result on the device was "SAFT keeps stopping"
/// and an empty Download folder: the exact silence this project has already lost days to.
///
/// Application.OnCreate runs before any activity exists. Handlers installed here cover everything.
/// </summary>
// No label or icon here: those are set in Properties/AndroidManifest.xml, and declaring them in two
// places is how you end up with a merge that quietly wins the wrong way.
[Application]
public class SaftApplication : Application
{
    /// <summary>
    /// PUBLIC, and it matters. The runtime constructs this class reflectively when Android starts
    /// the process; a protected constructor cannot be reached that way, and the app dies with
    /// "Unable to instantiate application" before a single line of managed startup code runs — which
    /// is, with some irony, before the crash handlers this class exists to install.
    /// </summary>
    public SaftApplication(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer)
    {
    }

    public override void OnCreate()
    {
        base.OnCreate();
        CrashLog.Install(GetExternalFilesDir(null)?.AbsolutePath);
    }
}
