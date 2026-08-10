using SAFT.Core;

namespace SAFT.Core.Tests;

public class FolderAccessTests
{
    [Fact]
    public void Reports_an_ordinary_folder_as_writable_and_leaves_nothing_behind()
    {
        var folder = TestScratch.NewDir();

        var result = FolderAccess.CheckWritable(folder);

        Assert.True(result.CanWrite);
        Assert.Null(result.Reason);
        // The probe must clean up after itself — a stray test file in someone's backup folder would
        // be confusing at best, and would show up as an unmatched file on a later install.
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public void Creates_the_folder_when_it_does_not_exist_yet()
    {
        var folder = Path.Combine(TestScratch.NewDir(), "backups", "nested");

        var result = FolderAccess.CheckWritable(folder);

        Assert.True(result.CanWrite);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void Reports_an_unusable_destination_with_a_reason_instead_of_throwing()
    {
        // A path where a FILE already sits is used rather than a chmod'd directory, because this
        // repo lives on an exFAT card which doesn't enforce Unix permissions at all — a chmod 500
        // folder there stays writable, so a permissions-based test would silently pass for the wrong
        // reason. This case fails identically on every platform and filesystem.
        var folder = TestScratch.NewDir();
        var blocked = Path.Combine(folder, "in-the-way");
        File.WriteAllText(blocked, "a file, not a folder");

        var result = FolderAccess.CheckWritable(blocked);

        // Turning the exception into an answer is the whole point: the caller needs to offer the
        // user another location, not crash mid-install.
        Assert.False(result.CanWrite);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Fact]
    public void Reports_a_blank_folder_path_as_unwritable()
    {
        Assert.False(FolderAccess.CheckWritable("").CanWrite);
        Assert.False(FolderAccess.CheckWritable("   ").CanWrite);
    }

    [Fact]
    public void Reports_free_space_for_a_real_folder_and_null_for_nonsense()
    {
        var free = FolderAccess.GetAvailableFreeBytes(TestScratch.NewDir());
        Assert.True(free is null or > 0);

        Assert.Null(FolderAccess.GetAvailableFreeBytes("\0invalid"));
    }
}
