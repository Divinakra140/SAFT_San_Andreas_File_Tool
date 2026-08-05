namespace SAFT.Core.Tests;

/// <summary>
/// Shared test scratch space. Deliberately placed on the same volume as the repo
/// (not the system temp dir) so nothing gets written to internal storage.
/// </summary>
internal static class TestScratch
{
    private static readonly string Root = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".tools", "test-tmp");

    public static string NewDir()
    {
        var dir = Path.Combine(Path.GetFullPath(Root), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
