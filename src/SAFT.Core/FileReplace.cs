namespace SAFT.Core;

/// <summary>
/// Puts a file where another one used to be, without ever leaving a moment where neither the
/// original nor the replacement exists. An earlier version of this deleted the destination first,
/// then moved the replacement into place — safe if the move can't fail, but a real, confirmed
/// crash under Wine happened in exactly that gap, destroying the original with nothing having
/// replaced it yet. This version renames the original out of the way first (reversible), moves the
/// replacement into place, and only deletes the renamed original after that succeeds — restoring it
/// automatically if the move fails or throws, so a failure here always leaves the original intact
/// rather than gone.
/// </summary>
internal static class FileReplace
{
    private const string BackupSuffix = ".saft-old";

    public static void MoveOver(string sourcePath, string destinationPath)
    {
        var backupPath = destinationPath + BackupSuffix;

        // Self-heal from a previous run that crashed between the rename-away and the delete-old
        // steps below, leaving no file at destinationPath but the original still safely sitting
        // under its backup name.
        if (!File.Exists(destinationPath) && File.Exists(backupPath))
            File.Move(backupPath, destinationPath);

        if (!File.Exists(destinationPath))
        {
            File.Move(sourcePath, destinationPath);
            return;
        }

        ClearReadOnly(destinationPath);

        if (File.Exists(backupPath)) File.Delete(backupPath);

        File.Move(destinationPath, backupPath);

        try
        {
            File.Move(sourcePath, destinationPath);
        }
        catch
        {
            // The replacement didn't make it — put the original back exactly where it was rather
            // than leaving destinationPath missing.
            File.Move(backupPath, destinationPath);
            throw;
        }

        File.Delete(backupPath);
    }

    private static void ClearReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }
}
