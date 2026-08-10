namespace SAFT.Core;

public sealed record ExtractionProgress(string CurrentArchive, int ArchiveIndex, int ArchiveCount, int FilesDone, int FilesTotal);

/// <summary>
/// Extracts a San Andreas install into an organized folder tree: every IMG archive unpacked into
/// named buckets, every other loose file mirrored as-is (so the extraction folder is a genuinely
/// complete workspace, not just "the archives"), and — optionally, since it's a much bigger and
/// slower pass — every SFX/streamed-audio package unpacked into individual sound/track files too.
/// </summary>
public static class Extractor
{
    private const int WavHeaderOverheadBytes = 44; // RIFF/WAVE/fmt /data chunk headers WavPcm.WriteMono16Wav adds

    /// <summary>
    /// Every file extraction will write, as its exact byte size — one list shared by both size
    /// estimators below so they can never drift apart, and by nothing else (computing this touches
    /// every SFX bank header when <paramref name="includeAudio"/> is true, so it isn't free).
    /// </summary>
    private static IEnumerable<long> EnumerateExtractedFileSizes(string gameRoot, IReadOnlyList<FoundArchive> archives, bool includeAudio)
    {
        foreach (var found in archives)
        {
            using var archive = ImgArchive.Open(found.AbsolutePath);
            foreach (var entry in archive.Entries)
                yield return entry.ByteSize;
        }

        var excluded = new HashSet<string>(archives.Select(a => a.AbsolutePath), StringComparer.OrdinalIgnoreCase);
        var sfxPackages = includeAudio ? SfxIndex.Load(gameRoot) : Array.Empty<SfxPackage>();
        var streamStations = includeAudio ? StreamIndex.Load(gameRoot) : Array.Empty<StreamStation>();
        if (includeAudio)
        {
            foreach (var pkg in sfxPackages) excluded.Add(pkg.AbsolutePath);
            foreach (var station in streamStations) excluded.Add(station.AbsolutePath);
        }

        foreach (var path in Directory.EnumerateFiles(gameRoot, "*", SearchOption.AllDirectories))
        {
            if (excluded.Contains(path) || FileFilters.IsIgnoredFile(Path.GetFileName(path))) continue;
            yield return new FileInfo(path).Length;
        }

        if (!includeAudio) yield break;

        foreach (var pkg in sfxPackages)
        {
            using var stream = File.OpenRead(pkg.AbsolutePath);
            foreach (var (offset, length) in pkg.Banks)
            {
                var bank = SfxBank.Read(stream, offset, length);
                for (var i = 0; i < bank.Sounds.Count; i++)
                    yield return bank.GetPcmLength(i) + WavHeaderOverheadBytes;
            }
        }

        foreach (var station in streamStations)
            foreach (var (_, payloadLength) in station.Tracks)
                yield return payloadLength;
    }

    /// <summary>
    /// Exact total bytes extraction will write to disk. Not an approximation — every archive
    /// entry, every loose file, and (if <paramref name="includeAudio"/>) every unpacked sound/track
    /// is the same size here as what <see cref="Extract"/> actually writes.
    /// </summary>
    public static long EstimateExtractedSizeBytes(string gameRoot, IReadOnlyList<FoundArchive> archives, bool includeAudio) =>
        EnumerateExtractedFileSizes(gameRoot, archives, includeAudio).Sum();

    /// <summary>
    /// Same total, but rounded up per-file to the destination filesystem's cluster size — the real
    /// reason "extracted size" and "size on disk" can differ wildly (thousands of small files each
    /// wasting up to a full cluster). Pass the destination drive's actual cluster size (in bytes)
    /// for an accurate estimate; pass 0 (or any value &lt;= 0) to fall back to the unrounded total.
    /// </summary>
    public static long EstimateExtractedSizeOnDiskBytes(string gameRoot, IReadOnlyList<FoundArchive> archives, bool includeAudio, long clusterSizeBytes)
    {
        if (clusterSizeBytes <= 0)
            return EstimateExtractedSizeBytes(gameRoot, archives, includeAudio);

        long total = 0;
        foreach (var size in EnumerateExtractedFileSizes(gameRoot, archives, includeAudio))
            total += ((size + clusterSizeBytes - 1) / clusterSizeBytes) * clusterSizeBytes;
        return total;
    }

    /// <summary>
    /// Extracts everything found under <paramref name="gameRoot"/> into <paramref name="destination"/>:
    /// every IMG archive unpacked (mirroring its relative path, bucketed by extension), every other
    /// loose file copied as-is at its original relative path, and — if <paramref name="includeAudio"/>
    /// — every SFX package and streamed-audio station unpacked into individual sound/track files
    /// too (a much bigger, slower pass: audio alone is tens of thousands of individual files).
    /// Writes a manifest.saft.json into the destination so the result can later be rebuilt.
    /// </summary>
    public static SaftManifest Extract(string gameRoot, string destination, bool includeAudio = false, IProgress<ExtractionProgress>? progress = null)
    {
        var archives = GameScanner.FindArchives(gameRoot);
        if (archives.Count == 0)
            throw new InvalidOperationException($"No VER2 IMG archives were found under '{gameRoot}'.");

        Directory.CreateDirectory(destination);

        var sfxPackages = includeAudio ? SfxIndex.Load(gameRoot) : Array.Empty<SfxPackage>();
        var streamStations = includeAudio ? StreamIndex.Load(gameRoot) : Array.Empty<StreamStation>();

        var excludedFromLooseCopy = new HashSet<string>(archives.Select(a => a.AbsolutePath), StringComparer.OrdinalIgnoreCase);
        if (includeAudio)
        {
            foreach (var pkg in sfxPackages) excludedFromLooseCopy.Add(pkg.AbsolutePath);
            foreach (var station in streamStations) excludedFromLooseCopy.Add(station.AbsolutePath);
        }

        var totalGroups = archives.Count + 1 + (includeAudio ? 1 : 0);
        var manifestArchives = new List<ManifestArchive>();

        // Extraction is over twenty thousand files, and reporting every one of them is what made
        // this take hours under Winlator rather than minutes. See ThrottledProgress.
        var report = new ThrottledProgress<ExtractionProgress>(progress);

        // A .dff bucket is created once, not once per .dff. There are only a handful of distinct
        // buckets per archive, but the old code asked the filesystem to create one for every single
        // entry - 16,316 redundant calls for gta3.img alone, each a real syscall through Wine and
        // then through Android's storage layer.
        var bucketsMade = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // One buffer for the whole extraction, not one per file.
        //
        // Stream.CopyTo allocates a fresh 81,920-byte array every time it is called. Over 21,058
        // archive entries that is 1.7 GB of allocation churned through a 32-BIT process, whose entire
        // address space is 2 GB. The garbage collector can keep up with the volume, but the
        // fragmentation it leaves behind cannot be collected away, and the failure mode of a
        // fragmented small address space is exactly what was reported: steadily slower, then a
        // process that vanishes without an exception partway through.
        var copyBuffer = new byte[81920];

        // ---- archives: unpack each into destination/<relative path>/<extension>/<filename> ----
        for (var i = 0; i < archives.Count; i++)
        {
            var found = archives[i];
            using var archive = ImgArchive.Open(found.AbsolutePath);

            var archiveDestRoot = Path.Combine(destination, found.RelativePath);
            Directory.CreateDirectory(archiveDestRoot);

            var entryOrder = new List<string>(archive.Entries.Count);
            var filesDone = 0;

            foreach (var entry in archive.Entries)
            {
                var bucketDir = Path.Combine(archiveDestRoot, entry.Extension);
                if (bucketsMade.Add(bucketDir)) Directory.CreateDirectory(bucketDir);

                var outPath = Path.Combine(bucketDir, entry.Name);
                using (var src = archive.OpenEntry(entry))
                // bufferSize 0 turns off FileStream's own internal buffer: it would be a second
                // per-file array serving no purpose, since everything below writes in 80 KB blocks
                // that are already far larger than it.
                using (var dst = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 0))
                {
                    int read;
                    while ((read = src.Read(copyBuffer, 0, copyBuffer.Length)) > 0)
                        dst.Write(copyBuffer, 0, read);
                }

                entryOrder.Add(entry.Name);
                filesDone++;
                report.Report(new ExtractionProgress(found.RelativePath, i + 1, totalGroups, filesDone, archive.Entries.Count));
            }

            // Not throttled: the bar has to actually finish this archive rather than stop wherever
            // the last tick happened to land.
            report.ReportNow(new ExtractionProgress(found.RelativePath, i + 1, totalGroups, filesDone, archive.Entries.Count));

            manifestArchives.Add(new ManifestArchive { RelativePath = found.RelativePath, OriginalEntryOrder = entryOrder });
        }

        // ---- everything else: copied as-is, so the extraction folder is a complete, self-contained workspace ----
        var looseFiles = Directory.EnumerateFiles(gameRoot, "*", SearchOption.AllDirectories)
            .Where(p => !excludedFromLooseCopy.Contains(p) && !FileFilters.IsIgnoredFile(Path.GetFileName(p)))
            .ToList();

        for (var i = 0; i < looseFiles.Count; i++)
        {
            var source = looseFiles[i];
            var relative = Path.GetRelativePath(gameRoot, source);
            var dest = Path.Combine(destination, relative);
            var destDir = Path.GetDirectoryName(dest)!;
            if (bucketsMade.Add(destDir)) Directory.CreateDirectory(destDir);
            File.Copy(source, dest, overwrite: true);

            report.Report(new ExtractionProgress("Copying game files", archives.Count + 1, totalGroups, i + 1, Math.Max(1, looseFiles.Count)));
        }

        report.ReportNow(new ExtractionProgress("Copying game files", archives.Count + 1, totalGroups, looseFiles.Count, Math.Max(1, looseFiles.Count)));

        // ---- optional: unpack SFX banks and streamed tracks into individual files ----
        var unpackedAudioPackages = new List<string>();
        var unpackedStreamStations = new List<string>();

        if (includeAudio)
        {
            var audioTotal = CountAudioItems(sfxPackages, streamStations);
            var audioDone = 0;
            var audioGroupIndex = archives.Count + 2;

            foreach (var pkg in sfxPackages)
            {
                using var stream = File.OpenRead(pkg.AbsolutePath);
                for (var bankNum = 1; bankNum <= pkg.Banks.Count; bankNum++)
                {
                    var (offset, length) = pkg.Banks[bankNum - 1];
                    var bank = SfxBank.Read(stream, offset, length);
                    if (bank.Sounds.Count == 0) continue;

                    var bankDestDir = Path.Combine(destination, "audio", "sfx", pkg.Name, $"Bank_{bankNum:D3}");
                    Directory.CreateDirectory(bankDestDir);

                    for (var soundIdx = 0; soundIdx < bank.Sounds.Count; soundIdx++)
                    {
                        stream.Position = bank.GetPcmOffset(soundIdx);
                        var pcm = new byte[bank.GetPcmLength(soundIdx)];
                        stream.ReadExactly(pcm);

                        var destPath = Path.Combine(bankDestDir, $"sound_{soundIdx + 1:D3}.wav");
                        using var outFile = File.Create(destPath);
                        WavPcm.WriteMono16Wav(outFile, pcm, bank.Sounds[soundIdx].SampleRate);

                        audioDone++;
                        report.Report(new ExtractionProgress($"audio/sfx/{pkg.Name}", audioGroupIndex, totalGroups, audioDone, audioTotal));
                    }
                }
                unpackedAudioPackages.Add(pkg.Name);
            }

            foreach (var station in streamStations)
            {
                using var stream = File.OpenRead(station.AbsolutePath);
                if (station.Tracks.Count > 0)
                {
                    var stationDestDir = Path.Combine(destination, "audio", "streams", station.Name);
                    Directory.CreateDirectory(stationDestDir);

                    for (var trackNum = 1; trackNum <= station.Tracks.Count; trackNum++)
                    {
                        var (offset, payloadLength) = station.Tracks[trackNum - 1];
                        var payloadOffset = offset + StreamIndex.TrackHeaderSize;
                        stream.Position = payloadOffset;
                        var encrypted = new byte[payloadLength];
                        stream.ReadExactly(encrypted);
                        var decrypted = StreamXor.Transform(encrypted, payloadOffset);

                        var destPath = Path.Combine(stationDestDir, $"Track_{trackNum:D3}.ogg");
                        File.WriteAllBytes(destPath, decrypted);

                        audioDone++;
                        report.Report(new ExtractionProgress($"audio/streams/{station.Name}", audioGroupIndex, totalGroups, audioDone, audioTotal));
                    }
                }
                unpackedStreamStations.Add(station.Name);
            }

            report.ReportNow(new ExtractionProgress("audio", audioGroupIndex, totalGroups, audioTotal, Math.Max(1, audioTotal)));
        }

        var manifest = new SaftManifest
        {
            GameRootPath = gameRoot,
            ExtractedAtUtc = DateTimeOffset.UtcNow,
            Archives = manifestArchives,
            UnpackedAudioPackages = unpackedAudioPackages,
            UnpackedStreamStations = unpackedStreamStations,
        };
        manifest.Save(destination);

        return manifest;
    }

    private static int CountAudioItems(IReadOnlyList<SfxPackage> sfxPackages, IReadOnlyList<StreamStation> streamStations)
    {
        var total = 0;
        foreach (var pkg in sfxPackages)
        {
            using var stream = File.OpenRead(pkg.AbsolutePath);
            foreach (var (offset, length) in pkg.Banks)
                total += SfxBank.Read(stream, offset, length).Sounds.Count;
        }
        foreach (var station in streamStations)
            total += station.Tracks.Count;
        return total;
    }
}
