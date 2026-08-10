namespace SAFT.Core;

/// <summary>A mod-source file that was matched to one or more original archive entries and copied into place.</summary>
public sealed record ModInstallRouted(string FileName, IReadOnlyList<string> ArchiveRelativePaths);

/// <summary>A mod-source .wav/.ogg matched to an unpacked sound/track by its "Package/Bank_NNN/sound_NNN.wav" or "Station/Track_NNN.ogg" path and copied into place.</summary>
public sealed record ModInstallAudioRouted(string MatchKey);

/// <summary>A mod-source file matched to a game file the extractor mirrored as-is (map data, path nodes, data tables) rather than to an archive entry.</summary>
public sealed record ModInstallUnarchivedRouted(string FileName, string RelativePath);

public sealed record ModInstallResult(
    IReadOnlyList<ModInstallRouted> Routed, IReadOnlyList<string> Unmatched,
    IReadOnlyList<ModInstallAudioRouted> AudioRouted, IReadOnlyList<string> AudioUnmatched,
    IReadOnlyList<ModInstallUnarchivedRouted> UnarchivedRouted,
    IReadOnlyList<DirectAmbiguousMatch> Ambiguous,
    IReadOnlyList<RefusedScript> RefusedScripts);

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
        // Throttled at the door, so every progress?.Report below it - including the ones passed
        // down into the private helpers - costs a UI round trip ten times a second rather than
        // once per file. See ThrottledProgress: per-file reporting is what made a full extraction
        // take hours under Winlator.
        progress = new ThrottledProgress<ModInstallProgress>(progress);

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

        var unarchivedIndex = UnarchivedIndex.BuildForExtraction(
            extractionRoot, manifest.Archives.Select(a => a.RelativePath));

        var routed = new List<ModInstallRouted>();
        var unmatched = new List<string>();
        var audioRouted = new List<ModInstallAudioRouted>();
        var audioUnmatched = new List<string>();
        var unarchivedRouted = new List<ModInstallUnarchivedRouted>();
        var ambiguous = new List<DirectAmbiguousMatch>();
        var refusedScripts = new List<RefusedScript>();

        var sourceFiles = Directory.EnumerateFiles(modSourceFolder, "*", SearchOption.AllDirectories).ToList();
        for (var i = 0; i < sourceFiles.Count; i++)
        {
            var sourcePath = sourceFiles[i];
            var fileName = Path.GetFileName(sourcePath);
            progress?.Report(new ModInstallProgress(i + 1, sourceFiles.Count, fileName));

            if (FileFilters.IsIgnoredFile(fileName)) continue;

            // Never routed, by any path — see FileFilters.IsGameScriptFile. The manifest knows which
            // scripts came out of script.img, which is what separates "extract and rebuild to do it
            // by hand" from "main.scm is one loose file you can just drag over".
            if (FileFilters.IsGameScriptFile(fileName))
            {
                var kind = archivesByFileName.ContainsKey(fileName)
                    ? RefusedScriptKind.StreamedScript
                    : RefusedScriptKind.MainScript;
                refusedScripts.Add(new RefusedScript(fileName, kind));
                continue;
            }

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

            var inArchives = archivesByFileName.TryGetValue(fileName, out var archiveRelativePaths);
            var inGameFiles = unarchivedIndex.TryGetValue(fileName, out var unarchivedTargets);

            if (!inArchives && !inGameFiles)
            {
                unmatched.Add(fileName);
                continue;
            }

            var bucket = ImgEntry.GetBucketFolderName(fileName);

            // Decided BEFORE anything is overwritten — comparing after would be comparing the mod's
            // own content against the game's, which always "differs" and would flag everything.
            // Same ask-don't-guess rule as the direct installer: a name living both inside an
            // archive and loose in the game folder is either one asset duplicated (identical
            // content, so both copies must move together) or two unrelated assets sharing a name,
            // and only the game's own bytes can tell those apart.
            var agreeingUnarchived = new List<UnarchivedFile>();
            if (inGameFiles)
            {
                foreach (var unarchived in unarchivedTargets!)
                {
                    if (inArchives && !ExtractedCopiesAgree(extractionRoot, archiveRelativePaths!, bucket, fileName, unarchived))
                        ambiguous.Add(new DirectAmbiguousMatch(fileName, archiveRelativePaths![0], unarchived.RelativePath));
                    else
                        agreeingUnarchived.Add(unarchived);
                }
            }

            if (inArchives)
            {
                foreach (var archiveRelativePath in archiveRelativePaths!)
                {
                    var destDir = Path.Combine(extractionRoot, archiveRelativePath, bucket);
                    Directory.CreateDirectory(destDir);
                    File.Copy(sourcePath, Path.Combine(destDir, fileName), overwrite: true);
                }

                routed.Add(new ModInstallRouted(fileName, archiveRelativePaths!));
            }

            foreach (var unarchived in agreeingUnarchived)
            {
                File.Copy(sourcePath, unarchived.AbsolutePath, overwrite: true);
                unarchivedRouted.Add(new ModInstallUnarchivedRouted(fileName, unarchived.RelativePath));
            }
        }

        return new ModInstallResult(routed, unmatched, audioRouted, audioUnmatched, unarchivedRouted, ambiguous, refusedScripts);
    }

    /// <summary>
    /// Whether the extracted install's archive-unpacked copy and mirrored loose copy of a
    /// same-named file hold identical content. Compared before either is overwritten, so the
    /// decision reflects the original game's own data.
    /// </summary>
    private static bool ExtractedCopiesAgree(
        string extractionRoot, List<string> archiveRelativePaths, string bucket, string fileName, UnarchivedFile unarchived)
    {
        foreach (var archiveRelativePath in archiveRelativePaths)
        {
            var archivedCopy = Path.Combine(extractionRoot, archiveRelativePath, bucket, fileName);
            if (!UnarchivedIndex.ContentMatches(archivedCopy, unarchived.AbsolutePath)) return false;
        }
        return true;
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
