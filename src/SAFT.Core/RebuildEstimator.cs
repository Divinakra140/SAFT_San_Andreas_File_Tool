namespace SAFT.Core;

/// <summary>
/// Storage-size projections for the three ways a rebuild can be installed, derived from just
/// three measured numbers so all three stay consistent with each other and with what actually
/// gets written.
/// </summary>
public sealed record RebuildSizeEstimate(
    long GameRootTotalBytes,
    long OriginalArchivesTotalBytes,
    long RebuiltArchivesTotalBytes)
{
    /// <summary>Everything in the original install that isn't one of the archives being rebuilt (exe, audio, movies, data files, ...).</summary>
    public long LooseFilesTotalBytes => GameRootTotalBytes - OriginalArchivesTotalBytes;

    /// <summary>Size of a full new playable copy: loose files + freshly rebuilt archives.</summary>
    public long NewFolderTotalBytes => LooseFilesTotalBytes + RebuiltArchivesTotalBytes;

    /// <summary>Installing in place with .img.bak backups kept: the new game size plus a backup copy of each original archive.</summary>
    public long InPlaceWithBackupTotalBytes => NewFolderTotalBytes + OriginalArchivesTotalBytes;

    /// <summary>Installing in place with no backups: same final size as a fresh copy, just done in the original location.</summary>
    public long InPlaceNoBackupTotalBytes => NewFolderTotalBytes;
}

public static class RebuildEstimator
{
    public static RebuildSizeEstimate Estimate(string extractionRoot)
    {
        var manifest = SaftManifest.Load(extractionRoot);

        var gameRootTotal = DirectorySize(manifest.GameRootPath);
        var originalArchivesTotal = manifest.Archives.Sum(a =>
        {
            var path = Path.Combine(manifest.GameRootPath, a.RelativePath);
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        });
        var rebuiltArchivesTotal = manifest.Archives.Sum(a => Rebuilder.EstimateRebuiltArchiveSize(extractionRoot, a));

        return new RebuildSizeEstimate(gameRootTotal, originalArchivesTotal, rebuiltArchivesTotal);
    }

    private static long DirectorySize(string path) =>
        Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(p => !FileFilters.IsIgnoredFile(Path.GetFileName(p)))
            .Sum(p => new FileInfo(p).Length);
}
