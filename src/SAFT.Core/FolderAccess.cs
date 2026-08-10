namespace SAFT.Core;

/// <summary>Whether a folder can actually be written to, and if not, why.</summary>
public sealed record FolderWritability(bool CanWrite, string? Reason)
{
    public static readonly FolderWritability Writable = new(true, null);
}

/// <summary>
/// Checks a destination is usable BEFORE an operation starts, rather than discovering it halfway
/// through.
///
/// Install already backs up each archive before modifying it, so an unwritable backup folder fails
/// on the first attempt and leaves the game untouched. The gap this closes is the one where writing
/// fails partway — a disk filling up, a USB drive pulled out, a network share dropping — after some
/// archives have already been changed. Probing up front also means the "pick somewhere else" prompt
/// appears before any work happens, which is far less alarming than mid-operation.
/// </summary>
public static class FolderAccess
{
    /// <summary>
    /// Creates the folder if needed and confirms a file can be written into it, cleaning up after
    /// itself. Never throws — the whole point is to turn an exception into an answer the caller can
    /// act on.
    /// </summary>
    public static FolderWritability CheckWritable(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return new FolderWritability(false, "No folder was given.");

        try
        {
            Directory.CreateDirectory(folder);

            var probePath = Path.Combine(folder, $".saft-write-test-{Guid.NewGuid():N}");
            using (var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                probe.WriteByte(0);
            }
            File.Delete(probePath);

            return FolderWritability.Writable;
        }
        catch (Exception ex)
        {
            return new FolderWritability(false, ex.Message);
        }
    }

    /// <summary>
    /// Free space on the volume holding <paramref name="folder"/>, or null if it can't be determined
    /// (an unavailable drive, or a platform that won't report it). Callers use this to warn before a
    /// large rebuild rather than running out of room mid-write.
    /// </summary>
    public static long? GetAvailableFreeBytes(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root)) return null;
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch
        {
            return null;
        }
    }
}
