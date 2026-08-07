namespace SAFT.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            // A completely silent "the app just doesn't open" is the hardest failure to diagnose —
            // surface whatever actually went wrong, even this early in startup, instead of that.
            MessageBox.Show(ex.ToString(), "SAFT failed to start", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
