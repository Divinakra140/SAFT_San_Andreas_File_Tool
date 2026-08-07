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
        var logDir = Path.GetDirectoryName(destinationPath)!;
        var backupPath = destinationPath + BackupSuffix;

        // Self-heal from a previous run that crashed between the rename-away and the delete-old
        // steps below, leaving no file at destinationPath but the original still safely sitting
        // under its backup name.
        DiagnosticLog.Write(logDir, $"FileReplace.MoveOver: checking for an orphaned backup at '{backupPath}'");
        if (!File.Exists(destinationPath) && File.Exists(backupPath))
        {
            DiagnosticLog.Write(logDir, "Orphaned backup found with no destination file — restoring it before continuing");
            File.Move(backupPath, destinationPath);
            DiagnosticLog.Write(logDir, "Orphaned backup restored");
        }

        if (!File.Exists(destinationPath))
        {
            DiagnosticLog.Write(logDir, $"No existing file at '{destinationPath}' — plain move, nothing to preserve");
            File.Move(sourcePath, destinationPath);
            DiagnosticLog.Write(logDir, "Plain move completed");
            return;
        }

        DiagnosticLog.Write(logDir, "Clearing read-only attribute on the destination, if set");
        ClearReadOnly(destinationPath);

        if (File.Exists(backupPath))
        {
            DiagnosticLog.Write(logDir, $"Deleting stale backup at '{backupPath}' before reusing that name");
            File.Delete(backupPath);
        }

        DiagnosticLog.Write(logDir, $"Renaming original '{destinationPath}' -> '{backupPath}'");
        File.Move(destinationPath, backupPath);
        DiagnosticLog.Write(logDir, "Original renamed to backup successfully — destination is now free");

        try
        {
            DiagnosticLog.Write(logDir, $"Moving replacement '{sourcePath}' -> '{destinationPath}'");
            File.Move(sourcePath, destinationPath);
            DiagnosticLog.Write(logDir, "Replacement moved into place successfully");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(logDir, $"Move of replacement FAILED ({ex.GetType().Name}: {ex.Message}) — restoring original from backup");
            // The replacement didn't make it — put the original back exactly where it was rather
            // than leaving destinationPath missing.
            File.Move(backupPath, destinationPath);
            DiagnosticLog.Write(logDir, "Original restored from backup after failed replacement");
            throw;
        }

        DiagnosticLog.Write(logDir, $"Deleting backup '{backupPath}' now that the replacement is confirmed in place");
        File.Delete(backupPath);
        DiagnosticLog.Write(logDir, "MoveOver finished successfully");
    }

    private static void ClearReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }
}
