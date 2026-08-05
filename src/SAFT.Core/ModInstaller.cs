namespace SAFT.Core;

/// <summary>A mod-source file that was matched to one or more original archive entries and copied into place.</summary>
public sealed record ModInstallRouted(string FileName, IReadOnlyList<string> ArchiveRelativePaths);

/// <summary>A mod-source .wav/.ogg matched to an unpacked sound/track by its "Package/Bank_NNN/sound_NNN.wav" or "Station/Track_NNN.ogg" path and copied into place.</summary>
public sealed record ModInstallAudioRouted(string MatchKey);

public sealed record ModInstallResult(
    IReadOnlyList<ModInstallRouted> Routed, IReadOnlyList<string> Unmatched,
    IReadOnlyList<ModInstallAudioRouted> AudioRouted, IReadOnlyList<string> AudioUnmatched);

public sealed record ModInstallProgress(int FilesDone, int FilesTotal, string CurrentFile);

/// <summary>
/// Auto-sorts a folder of loose mod-replacement files into an already-extracted install. Models,
/// textures, collision, and animations are matched using the manifest, to figure out which archive
/// (and extension bucket) each file originally came from — so the user doesn't have to know or
/// guess which .img a given .dff/.txd belongs to. Sound effects and streamed audio are matched the
/// same way DirectModInstaller matches them against a live game, except here the target is the
/// already-unpacked .wav/.ogg sitting in the extraction folder (only possible for
/// packages/stations the manifest records as having actually been unpacked — extracting audio is
/// opt-in). Only replacements (files whose name/path matches something that already existed) can
/// be routed this way; a mod adding a brand-new file under a new name has no original entry to
/// match against; those are reported back as unmatched.
/// </summary>
public static class ModInstaller
{
    /// <summary>
    /// Scans <paramref name="modSourceFolder"/> recursively (mod authors' own subfolder
    /// organization is ignored for models/textures/collision/animations, matching there is by
    /// filename only; audio still needs its Package/Bank_NNN/sound_NNN.wav or
    /// Station/Track_NNN.ogg path, same as everywhere else in SAFT) and copies every matched file
    /// into place, overwriting whatever's there. Filesystem/mod clutter (readmes, desktop.ini,
    /// etc., see <see cref="FileFilters"/>) is skipped automatically. If a name matches entries in
    /// more than one original archive, the file is copied into all of them.
    /// </summary>
    public static ModInstallResult Install(
        string extractionRoot, string modSourceFolder, IProgress<ModInstallProgress>? progress = null)
    {
        var manifest = SaftManifest.Load(extractionRoot);

        var archivesByFileName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in manifest.Archives)
        {
            foreach (var name in archive.OriginalEntryOrder)
            {
                if (!archivesByFileName.TryGetValue(name, out var archivePaths))
                {
                    archivePaths = new List<string>();
                    archivesByFileName[name] = archivePaths;
                }

                if (!archivePaths.Contains(archive.RelativePath))
                    archivePaths.Add(archive.RelativePath);
            }
        }

        var unpackedAudioPackages = new HashSet<string>(manifest.UnpackedAudioPackages, StringComparer.OrdinalIgnoreCase);
        var unpackedStreamStations = new HashSet<string>(manifest.UnpackedStreamStations, StringComparer.OrdinalIgnoreCase);

        var routed = new List<ModInstallRouted>();
        var unmatched = new List<string>();
        var audioRouted = new List<ModInstallAudioRouted>();
        var audioUnmatched = new List<string>();

        var sourceFiles = Directory.EnumerateFiles(modSourceFolder, "*", SearchOption.AllDirectories).ToList();
        for (var i = 0; i < sourceFiles.Count; i++)
        {
            var sourcePath = sourceFiles[i];
            var fileName = Path.GetFileName(sourcePath);
            progress?.Report(new ModInstallProgress(i + 1, sourceFiles.Count, fileName));

            if (FileFilters.IsIgnoredFile(fileName)) continue;

            if (fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                var matchKey = FileFilters.GetLastPathSegments(sourcePath, 3); // Package/Bank_NNN/sound_NNN.wav
                if (matchKey is not null && TryRouteAudio(extractionRoot, "sfx", unpackedAudioPackages, matchKey, sourcePath))
                    audioRouted.Add(new ModInstallAudioRouted(matchKey));
                else
                    audioUnmatched.Add(matchKey ?? fileName);
                continue;
            }

            if (fileName.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
            {
                var matchKey = FileFilters.GetLastPathSegments(sourcePath, 2); // Station/Track_NNN.ogg
                if (matchKey is not null && TryRouteAudio(extractionRoot, "streams", unpackedStreamStations, matchKey, sourcePath))
                    audioRouted.Add(new ModInstallAudioRouted(matchKey));
                else
                    audioUnmatched.Add(matchKey ?? fileName);
                continue;
            }

            if (!archivesByFileName.TryGetValue(fileName, out var archiveRelativePaths))
            {
                unmatched.Add(fileName);
                continue;
            }

            var bucket = ImgEntry.GetBucketFolderName(fileName);
            foreach (var archiveRelativePath in archiveRelativePaths)
            {
                var destDir = Path.Combine(extractionRoot, archiveRelativePath, bucket);
                Directory.CreateDirectory(destDir);
                File.Copy(sourcePath, Path.Combine(destDir, fileName), overwrite: true);
            }

            routed.Add(new ModInstallRouted(fileName, archiveRelativePaths));
        }

        return new ModInstallResult(routed, unmatched, audioRouted, audioUnmatched);
    }

    /// <summary>
    /// Copies a mod .wav/.ogg over the matching unpacked sound/track file, if (and only if) the
    /// top-level name in <paramref name="matchKey"/> is a package/station the manifest actually
    /// recorded as unpacked AND the exact unpacked file it names still exists — a package/station
    /// left compressed at extraction time has no per-item files to match against here at all.
    /// </summary>
    private static bool TryRouteAudio(
        string extractionRoot, string audioKind, HashSet<string> unpackedNames, string matchKey, string sourcePath)
    {
        var topLevelName = matchKey[..matchKey.IndexOf('/')];
        if (!unpackedNames.Contains(topLevelName)) return false;

        var destPath = Path.Combine(extractionRoot, "audio", audioKind, matchKey.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(destPath)) return false;

        File.Copy(sourcePath, destPath, overwrite: true);
        return true;
    }
}
