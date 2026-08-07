namespace SAFT.Core;

public sealed record RebuildProgress(string CurrentArchive, int ArchiveIndex, int ArchiveCount, int FilesDone, int FilesTotal);

public sealed record RebuildSummary(string RelativePath, int Kept, int Added, int Removed, long RebuiltSizeBytes);

/// <summary>Rebuilds VER2 IMG archives — and, where applicable, loose files and unpacked audio — from a previously extracted-and-edited folder tree.</summary>
public static class Rebuilder
{
    private sealed record ArchivePlan(
        List<string> OrderedNames, Dictionary<string, string> FilesOnDisk, int Kept, int Added, int Removed);

    /// <summary>
    /// Figures out the final entry list for one archive (kept/replaced + newly added, minus
    /// deleted) without writing anything — shared by the actual writer and by size estimation,
    /// so a size shown to the user before rebuilding always matches what rebuilding produces.
    /// </summary>
    private static ArchivePlan PlanArchive(string extractionRoot, ManifestArchive archiveManifest)
    {
        var archiveDir = Path.Combine(extractionRoot, archiveManifest.RelativePath);
        if (!Directory.Exists(archiveDir))
            throw new DirectoryNotFoundException(
                $"Expected extracted folder for '{archiveManifest.RelativePath}' at '{archiveDir}' but it's missing.");

        var filesOnDisk = Directory.EnumerateFiles(archiveDir, "*", SearchOption.AllDirectories)
            .Where(p => !FileFilters.IsIgnoredFile(Path.GetFileName(p)))
            .ToDictionary(p => Path.GetFileName(p), p => p, StringComparer.OrdinalIgnoreCase);

        var orderedNames = new List<string>();
        var kept = 0;
        var removed = 0;

        foreach (var originalName in archiveManifest.OriginalEntryOrder)
        {
            if (filesOnDisk.ContainsKey(originalName))
            {
                orderedNames.Add(originalName);
                kept++;
            }
            else
            {
                removed++;
            }
        }

        var knownNames = new HashSet<string>(orderedNames, StringComparer.OrdinalIgnoreCase);
        var added = filesOnDisk.Keys
            .Where(name => !knownNames.Contains(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        orderedNames.AddRange(added);

        return new ArchivePlan(orderedNames, filesOnDisk, kept, added.Count, removed);
    }

    /// <summary>
    /// Rebuilds everything described by the manifest in <paramref name="extractionRoot"/> into
    /// <paramref name="outputRoot"/>: every archive (files present in the manifest but missing
    /// from disk are dropped; files present on disk but absent from the manifest are appended at
    /// the end), every other loose file mirrored as-is, and — for every SFX package / streamed
    /// station the manifest records as having been unpacked during extraction — a fresh copy of
    /// the original package/station with its changed sounds/tracks patched back in. A package or
    /// station NOT in the manifest's unpacked lists was left compressed at extraction time, so it's
    /// just carried over untouched as part of the ordinary loose-file copy, same as everything else.
    /// </summary>
    public static IReadOnlyList<RebuildSummary> Rebuild(
        string extractionRoot, string outputRoot, IProgress<RebuildProgress>? progress = null)
    {
        var manifest = SaftManifest.Load(extractionRoot);
        var summaries = new List<RebuildSummary>();

        var hasUnpackedAudio = manifest.UnpackedAudioPackages.Count > 0 || manifest.UnpackedStreamStations.Count > 0;
        var totalGroups = manifest.Archives.Count + 1 + (hasUnpackedAudio ? 1 : 0);

        for (var i = 0; i < manifest.Archives.Count; i++)
        {
            var archiveManifest = manifest.Archives[i];
            var archiveIndex = i;
            var plan = PlanArchive(extractionRoot, archiveManifest);

            var files = plan.OrderedNames
                .Select(name => (Name: name, OpenContent: (Func<Stream>)(() => File.OpenRead(plan.FilesOnDisk[name]))))
                .ToList();

            var outPath = Path.Combine(outputRoot, archiveManifest.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

            ImgArchive.Write(outPath, files, onFileWritten: (done, total) =>
                progress?.Report(new RebuildProgress(archiveManifest.RelativePath, archiveIndex + 1, totalGroups, done, total)));

            var rebuiltSize = new FileInfo(outPath).Length;
            summaries.Add(new RebuildSummary(archiveManifest.RelativePath, plan.Kept, plan.Added, plan.Removed, rebuiltSize));
        }

        CopyLooseFiles(extractionRoot, outputRoot, manifest, manifest.Archives.Count + 1, totalGroups, progress);

        if (hasUnpackedAudio)
            RebuildAudio(extractionRoot, outputRoot, manifest, manifest.Archives.Count + 2, totalGroups, progress);

        return summaries;
    }

    /// <summary>
    /// Copies every extracted file that isn't part of an archive's bucket folder or an unpacked
    /// audio package/station's unpack folder — i.e. everything that was mirrored as-is at
    /// extraction time and (aside from whatever edits the user made) should just be carried
    /// straight through to the rebuilt output.
    /// </summary>
    private static void CopyLooseFiles(
        string extractionRoot, string outputRoot, SaftManifest manifest, int groupIndex, int totalGroups, IProgress<RebuildProgress>? progress)
    {
        var excludedDirs = manifest.Archives.Select(a => Path.Combine(extractionRoot, a.RelativePath))
            .Concat(manifest.UnpackedAudioPackages.Select(name => Path.Combine(extractionRoot, "audio", "sfx", name)))
            .Concat(manifest.UnpackedStreamStations.Select(name => Path.Combine(extractionRoot, "audio", "streams", name)))
            .Select(dir => dir + Path.DirectorySeparatorChar)
            .ToList();

        var looseFiles = Directory.EnumerateFiles(extractionRoot, "*", SearchOption.AllDirectories)
            .Where(p => !FileFilters.IsIgnoredFile(Path.GetFileName(p)))
            .Where(p => !excludedDirs.Any(dir => p.StartsWith(dir, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        for (var i = 0; i < looseFiles.Count; i++)
        {
            var source = looseFiles[i];
            var relative = Path.GetRelativePath(extractionRoot, source);
            var destination = Path.Combine(outputRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);

            progress?.Report(new RebuildProgress("Copying game files", groupIndex, totalGroups, i + 1, Math.Max(1, looseFiles.Count)));
        }
    }

    /// <summary>
    /// For every SFX package / streamed station the manifest says was unpacked: copies the
    /// original compressed package/station (from the manifest's recorded game root, since an
    /// unpacked package isn't present in the extraction folder as a loose file) into
    /// <paramref name="outputRoot"/>, then patches in whichever individual sound/track files still
    /// exist under the extraction folder's unpack folder. A sound/track the user deleted from the
    /// unpack folder is left as the original; one too large to fit its original allocation is left
    /// as the original too (same-size-or-smaller-only, like every other audio patch in SAFT).
    /// </summary>
    private static void RebuildAudio(
        string extractionRoot, string outputRoot, SaftManifest manifest, int groupIndex, int totalGroups, IProgress<RebuildProgress>? progress)
    {
        var packageLookup = manifest.UnpackedAudioPackages.Count > 0
            ? SfxIndex.Load(manifest.GameRootPath).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, SfxPackage>(StringComparer.OrdinalIgnoreCase);
        var stationLookup = manifest.UnpackedStreamStations.Count > 0
            ? StreamIndex.Load(manifest.GameRootPath).ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, StreamStation>(StringComparer.OrdinalIgnoreCase);

        var totalUnits = manifest.UnpackedAudioPackages.Count + manifest.UnpackedStreamStations.Count;
        var unitsDone = 0;

        foreach (var packageName in manifest.UnpackedAudioPackages)
        {
            unitsDone++;
            progress?.Report(new RebuildProgress($"audio/sfx/{packageName}", groupIndex, totalGroups, unitsDone, Math.Max(1, totalUnits)));

            if (!packageLookup.TryGetValue(packageName, out var package)) continue; // no longer in the original install

            var destPath = Path.Combine(outputRoot, "audio", "sfx", packageName);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(package.AbsolutePath, destPath, overwrite: true);

            var packageDir = Path.Combine(extractionRoot, "audio", "sfx", packageName);
            if (!Directory.Exists(packageDir)) continue;

            for (var bankNum = 1; bankNum <= package.Banks.Count; bankNum++)
            {
                var bankDir = Path.Combine(packageDir, $"Bank_{bankNum:D3}");
                if (!Directory.Exists(bankDir)) continue;

                var (offset, length) = package.Banks[bankNum - 1];
                SfxBank bank;
                using (var readStream = File.OpenRead(destPath))
                    bank = SfxBank.Read(readStream, offset, length);

                for (var soundIdx = 0; soundIdx < bank.Sounds.Count; soundIdx++)
                {
                    var wavPath = Path.Combine(bankDir, $"sound_{soundIdx + 1:D3}.wav");
                    if (!File.Exists(wavPath)) continue;

                    var (pcm, _) = WavPcm.ReadMono16Wav(wavPath);
                    try { SfxBank.PatchSound(destPath, offset, length, soundIdx, pcm); }
                    catch (InvalidOperationException) { /* larger than the original allocation; leave that sound as-is */ }
                }
            }
        }

        foreach (var stationName in manifest.UnpackedStreamStations)
        {
            unitsDone++;
            progress?.Report(new RebuildProgress($"audio/streams/{stationName}", groupIndex, totalGroups, unitsDone, Math.Max(1, totalUnits)));

            if (!stationLookup.TryGetValue(stationName, out var station)) continue;

            var destPath = Path.Combine(outputRoot, "audio", "streams", stationName);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(station.AbsolutePath, destPath, overwrite: true);

            var stationDir = Path.Combine(extractionRoot, "audio", "streams", stationName);
            if (!Directory.Exists(stationDir)) continue;

            for (var trackNum = 1; trackNum <= station.Tracks.Count; trackNum++)
            {
                var oggPath = Path.Combine(stationDir, $"Track_{trackNum:D3}.ogg");
                if (!File.Exists(oggPath)) continue;

                var (offset, payloadLength) = station.Tracks[trackNum - 1];
                var newPayload = File.ReadAllBytes(oggPath);
                if (!StreamIndex.LooksLikeOgg(newPayload)) continue; // not a valid Ogg replacement; leave the original track as-is

                try { StreamIndex.PatchTrack(destPath, offset, payloadLength, newPayload); }
                catch (InvalidOperationException) { /* larger than the original allocation; leave that track as-is */ }
            }
        }
    }

    /// <summary>Exact size a single archive would be if rebuilt right now, without writing anything.</summary>
    public static long EstimateRebuiltArchiveSize(string extractionRoot, ManifestArchive archiveManifest)
    {
        var plan = PlanArchive(extractionRoot, archiveManifest);
        var lengths = plan.OrderedNames.Select(name => new FileInfo(plan.FilesOnDisk[name]).Length);
        return ImgArchive.EstimateArchiveSize(plan.OrderedNames.Count, lengths);
    }

    /// <summary>
    /// Rebuilds into a temp folder — archives, loose files, and reconstituted audio alike — then
    /// installs the entire result over <paramref name="gameRoot"/>. If <paramref name="makeBackups"/>,
    /// each original archive and each original unpacked audio package/station is copied next to
    /// itself as "&lt;name&gt;.bak" (only if a backup doesn't already exist there) before being
    /// overwritten; ordinary loose files are just overwritten, matching how in-place rebuilds have
    /// always treated them.
    /// </summary>
    public static IReadOnlyList<RebuildSummary> RebuildInPlace(
        string extractionRoot, string gameRoot, bool makeBackups, IProgress<RebuildProgress>? progress = null)
    {
        var tempOutput = Path.Combine(Path.GetTempPath(), "SAFT-rebuild-" + Guid.NewGuid());
        try
        {
            // Rebuild()'s own reports carry their own group count (e.g. "9" groups); this call has
            // one more phase after Rebuild() finishes (installing the result into gameRoot below),
            // so every report coming out of Rebuild() gets rewritten here to count that extra phase
            // too — keeps the overall progress bar's scale consistent across the whole operation
            // instead of jumping when the final phase begins. GroupCount also gets captured here so
            // the install-phase reports below can use the same, correct total.
            var groupCount = 1;
            var wrappedProgress = progress is null ? null : new Progress<RebuildProgress>(p =>
            {
                groupCount = p.ArchiveCount + 1;
                progress.Report(p with { ArchiveCount = groupCount });
            });

            var summaries = Rebuild(extractionRoot, tempOutput, wrappedProgress);

            if (makeBackups)
            {
                var manifest = SaftManifest.Load(extractionRoot);
                foreach (var relative in BackedUpRelativePaths(summaries, manifest))
                {
                    var originalPath = Path.Combine(gameRoot, relative);
                    var backupPath = originalPath + ".bak";
                    if (File.Exists(originalPath) && !File.Exists(backupPath))
                        File.Copy(originalPath, backupPath);
                }
            }

            // Previously silent — for a full game install (tens of thousands of loose files, not
            // just the handful of archives) this pass alone can take minutes, and with no progress
            // reporting at all the app looked completely frozen right after "done" showed for the
            // rebuild itself. Enumerated up front so the total is known for reporting.
            var filesToInstall = Directory.EnumerateFiles(tempOutput, "*", SearchOption.AllDirectories).ToList();
            for (var i = 0; i < filesToInstall.Count; i++)
            {
                var source = filesToInstall[i];
                var relative = Path.GetRelativePath(tempOutput, source);
                var destination = Path.Combine(gameRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                // Move, not Copy — tempOutput gets deleted whole right after this loop anyway, so
                // every file in it is disposable; Move avoids reading and writing potentially huge
                // archives (gta3.img and friends) a second time for no reason. Goes through
                // FileReplace (delete-then-move, not an atomic overwrite-move) for the same reason
                // DirectModInstaller does — see FileReplace's own comment.
                FileReplace.MoveOver(source, destination);

                if (i == 0 || i + 1 == filesToInstall.Count || (i + 1) % 25 == 0)
                    progress?.Report(new RebuildProgress("Installing into game folder", groupCount, groupCount, i + 1, filesToInstall.Count));
            }

            return summaries;
        }
        finally
        {
            if (Directory.Exists(tempOutput))
                Directory.Delete(tempOutput, recursive: true);
        }
    }

    private static IEnumerable<string> BackedUpRelativePaths(IReadOnlyList<RebuildSummary> summaries, SaftManifest manifest) =>
        summaries.Select(s => s.RelativePath)
            .Concat(manifest.UnpackedAudioPackages.Select(name => Path.Combine("audio", "sfx", name)))
            .Concat(manifest.UnpackedStreamStations.Select(name => Path.Combine("audio", "streams", name)));

    /// <summary>
    /// Rebuilds everything (archives, loose files, reconstituted audio) straight into
    /// <paramref name="outputRoot"/> — a genuinely standalone, playable second copy of the game,
    /// not just a folder of loose .img files.
    /// </summary>
    public static IReadOnlyList<RebuildSummary> RebuildNewPlayableCopy(
        string extractionRoot, string outputRoot, IProgress<RebuildProgress>? progress = null) =>
        Rebuild(extractionRoot, outputRoot, progress);
}
