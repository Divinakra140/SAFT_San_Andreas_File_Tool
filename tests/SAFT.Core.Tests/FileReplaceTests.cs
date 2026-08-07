namespace SAFT.Core.Tests;

public class FileReplaceTests
{
    [Fact]
    public void MoveOver_moves_the_source_into_a_destination_that_does_not_exist_yet()
    {
        var dir = TestScratch.NewDir();
        var source = Path.Combine(dir, "source.txt");
        var destination = Path.Combine(dir, "destination.txt");
        File.WriteAllText(source, "new content");

        FileReplace.MoveOver(source, destination);

        Assert.False(File.Exists(source));
        Assert.Equal("new content", File.ReadAllText(destination));
    }

    [Fact]
    public void MoveOver_replaces_an_existing_destination_and_cleans_up_the_backup()
    {
        var dir = TestScratch.NewDir();
        var source = Path.Combine(dir, "source.txt");
        var destination = Path.Combine(dir, "destination.txt");
        File.WriteAllText(source, "new content");
        File.WriteAllText(destination, "old content");

        FileReplace.MoveOver(source, destination);

        Assert.False(File.Exists(source));
        Assert.Equal("new content", File.ReadAllText(destination));
        Assert.False(File.Exists(destination + ".saft-old")); // backup cleaned up on success
    }

    [Fact]
    public void MoveOver_self_heals_from_a_previous_run_that_died_right_after_renaming_the_original_away()
    {
        var dir = TestScratch.NewDir();
        var source = Path.Combine(dir, "source.txt");
        var destination = Path.Combine(dir, "destination.txt");
        var orphanedBackup = destination + ".saft-old";

        // Simulates the exact state a crash between "rename original to backup" and "move
        // replacement into place" would leave behind: no file at destination, original safely
        // sitting under the backup name.
        File.WriteAllText(orphanedBackup, "original content, orphaned by a previous crash");
        File.WriteAllText(source, "new content");

        FileReplace.MoveOver(source, destination);

        Assert.Equal("new content", File.ReadAllText(destination));
        Assert.False(File.Exists(orphanedBackup));
    }

    [Fact]
    public void MoveOver_restores_the_original_if_moving_the_replacement_fails()
    {
        var dir = TestScratch.NewDir();
        var missingSource = Path.Combine(dir, "does-not-exist.txt"); // guarantees the inner move throws
        var destination = Path.Combine(dir, "destination.txt");
        File.WriteAllText(destination, "original content, must survive a failed replacement");

        Assert.ThrowsAny<IOException>(() => FileReplace.MoveOver(missingSource, destination));

        // The whole point: a failed replacement must never leave the destination missing.
        Assert.True(File.Exists(destination));
        Assert.Equal("original content, must survive a failed replacement", File.ReadAllText(destination));
        Assert.False(File.Exists(destination + ".saft-old")); // restored, not left behind under the backup name
    }
}
