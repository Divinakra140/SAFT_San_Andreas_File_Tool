using System.IO;
using System.Runtime.InteropServices;

namespace SAFT.App;

/// <summary>
/// The reason "extracted size" and "size on disk" can differ by many GB for a game with 20k+
/// small files: every file gets rounded up to a full filesystem cluster, and clusters on large
/// removable drives (exFAT SD cards especially) are often far bigger than the 2048-byte sectors
/// the IMG format itself uses. This queries the real cluster size for wherever the user is
/// actually extracting to, so the storage warning matches what they'll really see.
/// </summary>
internal static class Win32Disk
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetDiskFreeSpaceW(
        string lpRootPathName,
        out uint lpSectorsPerCluster,
        out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters,
        out uint lpTotalNumberOfClusters);

    /// <summary>
    /// Cluster size (in bytes) of the drive containing <paramref name="anyPathOnDrive"/>. The path
    /// itself doesn't need to exist yet (extraction destinations often don't) — only its drive root
    /// does. Falls back to <paramref name="fallback"/> (4096, a typical NTFS default) if the drive
    /// can't be queried for any reason.
    /// </summary>
    public static long GetClusterSizeBytes(string anyPathOnDrive, long fallback = 4096)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(anyPathOnDrive));
            if (string.IsNullOrEmpty(root)) return fallback;

            if (GetDiskFreeSpaceW(root, out var sectorsPerCluster, out var bytesPerSector, out _, out _))
            {
                var clusterSize = (long)sectorsPerCluster * bytesPerSector;
                return clusterSize > 0 ? clusterSize : fallback;
            }
        }
        catch
        {
            // Fall through to the default below — this is a "nice to have" refinement,
            // never worth failing extraction over.
        }

        return fallback;
    }
}
