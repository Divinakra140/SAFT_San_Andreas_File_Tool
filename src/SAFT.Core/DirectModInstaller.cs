namespace SAFT.Core;

/// <summary>One mod file matched directly against a live archive entry (no extraction involved).</summary>
public sealed record DirectInstallMatch(
    string FileName,
    string ModFilePath,
    string ArchiveRelativePath,
    string ArchiveAbsolutePath,
    string EntryName,
    bool RequiresRebuild);

/// <summary>
/// One mod .wav matched against a live SFX sound slot by its "Package/Bank_NNN/sound_NNN.wav"
/// path (that 3-segment key is what makes the match unambiguous — every bank in every package
/// starts its own "sound_001.wav", so the bare filename alone is meaningless here).
/// </summary>
public sealed record DirectAudioMatch(
    string MatchKey,
    string ModFilePath,
    string PackageAbsolutePath,
    string PackageRelativePath,
    long BankHeaderOffset,
    long BankLength,
    int SoundIndex,
    long OriginalPcmLength,
    long NewPcmLength,
    int NewSampleRate)
{
    /// <summary>
    /// SFX sounds are packed back-to-back with no slack — unlike IMG archives there's no "rebuild
    /// the whole thing" fallback for an oversized replacement (that would cascade through every
    /// later bank in the package), so a sound that doesn't fit simply can't be replaced.
    /// </summary>
    public bool Fits => NewPcmLength <= OriginalPcmLength;
}

/// <summary>
/// One mod .ogg matched against a live streamed-audio track by its "Station/Track_NNN.ogg" path —
/// the same 2-segment convention SAAT's own stream exporter uses.
/// </summary>
public sealed record DirectStreamMatch(
    string MatchKey,
    string ModFilePath,
    string StationAbsolutePath,
    string StationRelativePath,
    long HeaderOffset,
    long OriginalPayloadLength,
    long NewPayloadLength)
{
    /// <summary>Same reasoning as SFX: tracks are packed back-to-back, so no rebuild fallback for an oversized replacement.</summary>
    public bool Fits => NewPayloadLength <= OriginalPayloadLength;
}

/// <summary>
/// One mod file matched against a game file that lives loose in the game folder rather than inside
/// an archive — map placement data (.ipl/.ide), path nodes, data tables, loose textures. Plain file
/// replacement, no directory table or sector maths involved.
/// </summary>
public sealed record DirectUnarchivedMatch(
    string FileName,
    string ModFilePath,
    string RelativePath,
    string AbsolutePath);

/// <summary>
/// A mod file whose name matches both an archive entry and an unarchived game file, where those two
/// game copies hold <em>different</em> content — so they're two distinct live assets that merely
/// share a name and SAFT can't know which one the mod meant. Reported rather than guessed at; the
/// archived copy is still replaced, the unarchived one is left alone.
/// </summary>
public sealed record DirectAmbiguousMatch(string FileName, string ArchiveRelativePath, string UnarchivedRelativePath);

/// <summary>
/// Which kind of game script SAFT declined to install. The refusal reason is identical for both
/// (see <see cref="FileFilters.IsGameScriptFile"/>), but what the user would have to do to install
/// it by hand is completely different, so they're reported apart.
/// </summary>
public enum RefusedScriptKind
{
    /// <summary>The main script, loose at data/script/main.scm — a single file that can simply be dragged over.</summary>
    MainScript,

    /// <summary>A streamed script living inside script.img — only reachable by extracting the game and rebuilding it.</summary>
    StreamedScript,
}

public sealed record RefusedScript(string FileName, RefusedScriptKind Kind);

public sealed record DirectInstallPlan(
    string GameRoot,
    IReadOnlyList<DirectInstallMatch> Matches,
    IReadOnlyList<string> Unmatched,
    int TotalArchivesInGame,
    IReadOnlyList<DirectAudioMatch> AudioMatches,
    IReadOnlyList<string> AudioUnmatched,
    IReadOnlyList<DirectStreamMatch> StreamMatches,
    IReadOnlyList<string> StreamUnmatched,
    IReadOnlyList<DirectUnarchivedMatch> UnarchivedMatches,
    IReadOnlyList<DirectAmbiguousMatch> Ambiguous,
    IReadOnlyList<RefusedScript> RefusedScripts)
{
    /// <summary>
    /// True if any matched file is too big to fit in its original entry's allocated space, meaning
    /// at least one archive can't be simply byte-patched and needs a full rebuild instead.
    /// </summary>
    public bool AnyArchiveNeedsRebuild => Matches.Any(m => m.RequiresRebuild);

    /// <summary>Relative paths of the specific archives that need a full rebuild (not just a patch), in name order.</summary>
    public IReadOnlyList<string> ArchivesNeedingRebuild =>
        Matches.Where(m => m.RequiresRebuild)
            .Select(m => m.ArchiveRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<DirectAudioMatch> AudioMatchesThatFit => AudioMatches.Where(m => m.Fits).ToList();
    public IReadOnlyList<DirectAudioMatch> AudioMatchesTooLarge => AudioMatches.Where(m => !m.Fits).ToList();

    public IReadOnlyList<DirectStreamMatch> StreamMatchesThatFit => StreamMatches.Where(m => m.Fits).ToList();
    public IReadOnlyList<DirectStreamMatch> StreamMatchesTooLarge => StreamMatches.Where(m => !m.Fits).ToList();
}

public sealed record DirectInstallProgress(string CurrentArchive, int ArchiveIndex, int ArchiveCount, string Stage, int FilesDone, int FilesTotal);

public sealed record DirectInstallSummary(string ArchiveRelativePath, int FilesReplaced, bool Rebuilt);

public sealed record DirectAudioSummary(string MatchKey, bool BackedUp);

public sealed record DirectStreamSummary(string MatchKey, bool BackedUp);

/// <summary>One matched sound/track that couldn't actually be patched — a corrupted or unreadable mod file, most often — with the reason, so it's reported clearly instead of aborting everything else.</summary>
public sealed record DirectAudioFailure(string MatchKey, string Reason);

public sealed record DirectStreamFailure(string MatchKey, string Reason);

public sealed record DirectUnarchivedSummary(string RelativePath, bool BackedUp);

public sealed record DirectUnarchivedFailure(string RelativePath, string Reason);

public sealed record DirectInstallResult(
    IReadOnlyList<DirectInstallSummary> Archives, IReadOnlyList<DirectAudioSummary> Audio, IReadOnlyList<DirectStreamSummary> Streams,
    IReadOnlyList<DirectAudioFailure> AudioFailed, IReadOnlyList<DirectStreamFailure> StreamFailed,
    IReadOnlyList<DirectUnarchivedSummary> Unarchived, IReadOnlyList<DirectUnarchivedFailure> UnarchivedFailed);

/// <summary>
/// Installs mod-replacement files straight into a game's live archives, SFX banks, and streamed
/// audio, with no extraction step. A file that fits within the space its original entry already
/// occupies can be patched in place — the directory table doesn't move, only those bytes change.
/// For IMG entries, one too big for that forces a full rebuild of just that one archive; for SFX
/// sounds and streamed tracks, one too big simply can't be replaced (packed back-to-back with no
/// slack, so growing one would cascade through everything after it).
/// </summary>
public static class DirectModInstaller
{
    /// <summary>
    /// Scans <paramref name="modSourceFolder"/> and matches files by name against every archive
    /// entry, SFX sound, and streamed track currently in the game at <paramref name="gameRoot"/>,
    /// without changing anything — callers should inspect
    /// <see cref="DirectInstallPlan.AnyArchiveNeedsRebuild"/>, <see cref="DirectInstallPlan.AudioMatchesTooLarge"/>,
    /// and <see cref="DirectInstallPlan.StreamMatchesTooLarge"/> before calling <see cref="Apply"/>.
    /// </summary>
    public static DirectInstallPlan Plan(string gameRoot, string modSourceFolder)
    {
        var foundArchives = GameScanner.FindArchives(gameRoot);
        var index = new Dictionary<string, List<(FoundArchive Archive, ImgEntry Entry)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var found in foundArchives)
        {
            using var archive = ImgArchive.Open(found.AbsolutePath);
            foreach (var entry in archive.Entries)
            {
                if (!index.TryGetValue(entry.Name, out var targets))
                {
                    targets = new List<(FoundArchive, ImgEntry)>();
                    index[entry.Name] = targets;
                }
                targets.Add((found, entry));
            }
        }

        var audioIndex = BuildAudioIndex(gameRoot);
        var streamIndex = BuildStreamIndex(gameRoot);
        var unarchivedIndex = UnarchivedIndex.Build(gameRoot);

        var matches = new List<DirectInstallMatch>();
        var unmatched = new List<string>();
        var audioMatches = new List<DirectAudioMatch>();
        var audioUnmatched = new List<string>();
        var streamMatches = new List<DirectStreamMatch>();
        var streamUnmatched = new List<string>();
        var unarchivedMatches = new List<DirectUnarchivedMatch>();
        var ambiguous = new List<DirectAmbiguousMatch>();
        var refusedScripts = new List<RefusedScript>();

        foreach (var sourcePath in Directory.EnumerateFiles(modSourceFolder, "*", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(sourcePath);
            if (FileFilters.IsIgnoredFile(fileName)) continue;

            // Game scripts are refused outright, archived or not — see FileFilters.IsGameScriptFile
            // for why. Reported separately from "unmatched" so the app can explain the refusal
            // rather than leaving the user thinking SAFT simply didn't recognise the file. Which
            // kind it is decides the manual instructions: a script the game keeps inside script.img
            // can only be swapped by extracting and rebuilding, whereas main.scm is one loose file.
            if (FileFilters.IsGameScriptFile(fileName))
            {
                var kind = index.ContainsKey(fileName) ? RefusedScriptKind.StreamedScript : RefusedScriptKind.MainScript;
                refusedScripts.Add(new RefusedScript(fileName, kind));
                continue;
            }

            if (fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                var matchKey = FileFilters.GetLastPathSegments(sourcePath, 3); // Package/Bank_NNN/sound_NNN.wav
                if (matchKey is not null && audioIndex.TryGetValue(matchKey, out var slot))
                {
                    try
                    {
                        var (pcm, sampleRate) = WavPcm.ReadMono16Wav(sourcePath);
                        audioMatches.Add(new DirectAudioMatch(
                            matchKey, sourcePath, slot.Package.AbsolutePath, slot.PackageRelativePath,
                            slot.BankHeaderOffset, slot.BankLength, slot.SoundIndex, slot.OriginalPcmLength,
                            pcm.Length, sampleRate));
                    }
                    catch (Exception ex)
                    {
                        // A corrupted/unreadable .wav must not stop scanning the rest of the mod
                        // folder — report it clearly instead.
                        audioUnmatched.Add($"{matchKey} (unreadable: {ex.Message})");
                    }
                }
                else
                {
                    audioUnmatched.Add(matchKey ?? fileName);
                }
                continue;
            }

            if (fileName.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
            {
                var matchKey = FileFilters.GetLastPathSegments(sourcePath, 2); // Station/Track_NNN.ogg
                if (matchKey is not null && streamIndex.TryGetValue(matchKey, out var track))
                {
                    var newLength = new FileInfo(sourcePath).Length;
                    streamMatches.Add(new DirectStreamMatch(
                        matchKey, sourcePath, track.Station.AbsolutePath, track.StationRelativePath,
                        track.HeaderOffset, track.OriginalPayloadLength, newLength));
                }
                else
                {
                    streamUnmatched.Add(matchKey ?? fileName);
                }
                continue;
            }

            var inArchives = index.TryGetValue(fileName, out var targets);
            var inGameFolder = unarchivedIndex.TryGetValue(fileName, out var unarchivedTargets);

            if (!inArchives && !inGameFolder)
            {
                unmatched.Add(fileName);
                continue;
            }

            if (inArchives)
            {
                var newSectors = (new FileInfo(sourcePath).Length + ImgEntry.SectorSize - 1) / ImgEntry.SectorSize;
                foreach (var (found, entry) in targets!)
                {
                    var requiresRebuild = newSectors > entry.SizeSectors;
                    matches.Add(new DirectInstallMatch(fileName, sourcePath, found.RelativePath, found.AbsolutePath, entry.Name, requiresRebuild));
                }
            }

            if (!inGameFolder) continue;

            foreach (var unarchived in unarchivedTargets!)
            {
                // A name found in both worlds is the one case where SAFT can't just act: it's
                // either the same asset duplicated (San Andreas ships the 64 path-node tables
                // byte-identically in data/Paths/ AND inside gta3.img — there, updating only one
                // copy risks the game loading the stale other one), or two unrelated assets that
                // happen to share a name. Comparing the game's own two copies is what tells those
                // apart, so the decision comes from the data rather than a guess about intent.
                if (inArchives && !ArchivedAndUnarchivedAgree(targets!, unarchived))
                {
                    ambiguous.Add(new DirectAmbiguousMatch(fileName, targets![0].Archive.RelativePath, unarchived.RelativePath));
                    continue;
                }

                unarchivedMatches.Add(new DirectUnarchivedMatch(
                    fileName, sourcePath, unarchived.RelativePath, unarchived.AbsolutePath));
            }
        }

        return new DirectInstallPlan(
            gameRoot, matches, unmatched, foundArchives.Count, audioMatches, audioUnmatched, streamMatches, streamUnmatched,
            unarchivedMatches, ambiguous, refusedScripts);
    }

    /// <summary>
    /// Whether the game's own archived and unarchived copies of a same-named file hold identical
    /// content. Any archive copy differing is enough to call it a disagreement — better to ask than
    /// to replace an asset the mod may not have meant.
    /// </summary>
    private static bool ArchivedAndUnarchivedAgree(
        List<(FoundArchive Archive, ImgEntry Entry)> targets, UnarchivedFile unarchived)
    {
        foreach (var (found, entry) in targets)
        {
            try
            {
                using var archive = ImgArchive.Open(found.AbsolutePath);
                using var entryStream = archive.OpenEntry(entry);
                if (!UnarchivedIndex.ContentMatches(entryStream, entry.SizeSectors * (long)ImgEntry.SectorSize, unarchived.AbsolutePath))
                    return false;
            }
            catch
            {
                // Unreadable for any reason means "can't establish they're the same", which lands
                // on the ask-don't-guess side.
                return false;
            }
        }
        return true;
    }

    private sealed record AudioSlot(
        SfxPackage Package, string PackageRelativePath, long BankHeaderOffset, long BankLength, int SoundIndex, long OriginalPcmLength);

    private static Dictionary<string, AudioSlot> BuildAudioIndex(string gameRoot)
    {
        var index = new Dictionary<string, AudioSlot>(StringComparer.OrdinalIgnoreCase);
        foreach (var pkg in SfxIndex.Load(gameRoot))
        {
            using var stream = File.OpenRead(pkg.AbsolutePath);
            var packageRelativePath = Path.GetRelativePath(gameRoot, pkg.AbsolutePath);

            for (var bankNum = 1; bankNum <= pkg.Banks.Count; bankNum++)
            {
                var (offset, length) = pkg.Banks[bankNum - 1];
                var bank = SfxBank.Read(stream, offset, length);
                for (var soundIdx = 0; soundIdx < bank.Sounds.Count; soundIdx++)
                {
                    var key = $"{pkg.Name}/Bank_{bankNum:D3}/sound_{soundIdx + 1:D3}.wav";
                    index[key] = new AudioSlot(pkg, packageRelativePath, offset, length, soundIdx, bank.GetPcmLength(soundIdx));
                }
            }
        }
        return index;
    }

    private sealed record StreamSlot(StreamStation Station, string StationRelativePath, long HeaderOffset, long OriginalPayloadLength);

    private static Dictionary<string, StreamSlot> BuildStreamIndex(string gameRoot)
    {
        var index = new Dictionary<string, StreamSlot>(StringComparer.OrdinalIgnoreCase);
        foreach (var station in StreamIndex.Load(gameRoot))
        {
            var stationRelativePath = Path.GetRelativePath(gameRoot, station.AbsolutePath);
            for (var trackNum = 1; trackNum <= station.Tracks.Count; trackNum++)
            {
                var (offset, payloadLength) = station.Tracks[trackNum - 1];
                var key = $"{station.Name}/Track_{trackNum:D3}.ogg";
                index[key] = new StreamSlot(station, stationRelativePath, offset, payloadLength);
            }
        }
        return index;
    }

    /// <summary>
    /// Carries out a plan from <see cref="Plan"/>. If <paramref name="backupOutputFolder"/> is
    /// given, every original entry/sound/track about to be replaced is copied there before it's
    /// touched; pass null to skip backups entirely (destructive, irreversible). Matches that don't
    /// fit (<see cref="DirectAudioMatch.Fits"/>/<see cref="DirectStreamMatch.Fits"/> false) are
    /// silently skipped here — filter the plan first if you need to warn about them.
    /// </summary>
    public static DirectInstallResult Apply(
        DirectInstallPlan plan, string? backupOutputFolder, IProgress<DirectInstallProgress>? progress = null)
    {
        var summaries = new List<DirectInstallSummary>();
        var byArchive = plan.Matches.GroupBy(m => m.ArchiveRelativePath).ToList();
        var audioToApply = plan.AudioMatchesThatFit;
        var streamsToApply = plan.StreamMatchesThatFit;
        var totalGroups = byArchive.Count + (audioToApply.Count > 0 ? 1 : 0) + (streamsToApply.Count > 0 ? 1 : 0)
            + (plan.UnarchivedMatches.Count > 0 ? 1 : 0);

        for (var i = 0; i < byArchive.Count; i++)
        {
            var group = byArchive[i].ToList();
            var archiveRelativePath = group[0].ArchiveRelativePath;
            var archiveAbsolutePath = group[0].ArchiveAbsolutePath;
            var needsRebuild = group.Any(m => m.RequiresRebuild);
            var archiveIndex = i + 1;

            if (backupOutputFolder is not null)
            {
                BackupOriginals(archiveAbsolutePath, archiveRelativePath, group, backupOutputFolder,
                    (done, total) => progress?.Report(new DirectInstallProgress(
                        archiveRelativePath, archiveIndex, totalGroups, "Backing up originals", done, total)));
            }

            if (needsRebuild)
            {
                RebuildArchiveWithReplacements(archiveAbsolutePath, group,
                    (done, total) => progress?.Report(new DirectInstallProgress(
                        archiveRelativePath, archiveIndex, totalGroups, "Rebuilding (mod files are larger than the originals)", done, total)));
            }
            else
            {
                PatchArchiveInPlace(archiveAbsolutePath, group,
                    (done, total) => progress?.Report(new DirectInstallProgress(
                        archiveRelativePath, archiveIndex, totalGroups, "Patching in place", done, total)));
            }

            summaries.Add(new DirectInstallSummary(archiveRelativePath, group.Count, needsRebuild));
        }

        var audioSummaries = new List<DirectAudioSummary>();
        var audioFailed = new List<DirectAudioFailure>();
        for (var i = 0; i < audioToApply.Count; i++)
        {
            var match = audioToApply[i];

            progress?.Report(new DirectInstallProgress(
                match.MatchKey, byArchive.Count + 1, totalGroups, "Patching audio in place", i + 1, audioToApply.Count));

            try
            {
                var backedUp = false;
                if (backupOutputFolder is not null)
                {
                    BackupAudioOriginal(match, backupOutputFolder);
                    backedUp = true;
                }

                var (pcm, _) = WavPcm.ReadMono16Wav(match.ModFilePath);
                PatchAudioSound(match, pcm);

                audioSummaries.Add(new DirectAudioSummary(match.MatchKey, backedUp));
            }
            catch (Exception ex)
            {
                // A single corrupted/unreadable mod file (a malformed .wav, most often) must not
                // derail everything else already patched or still queued up — record it and move
                // on to the rest, same as an oversized match is already skipped-and-reported.
                audioFailed.Add(new DirectAudioFailure(match.MatchKey, ex.Message));
            }
        }

        var streamSummaries = new List<DirectStreamSummary>();
        var streamFailed = new List<DirectStreamFailure>();
        var streamGroupIndex = byArchive.Count + (audioToApply.Count > 0 ? 1 : 0) + 1;
        for (var i = 0; i < streamsToApply.Count; i++)
        {
            var match = streamsToApply[i];

            progress?.Report(new DirectInstallProgress(
                match.MatchKey, streamGroupIndex, totalGroups, "Patching streamed audio in place", i + 1, streamsToApply.Count));

            try
            {
                var backedUp = false;
                if (backupOutputFolder is not null)
                {
                    BackupStreamOriginal(match, backupOutputFolder);
                    backedUp = true;
                }

                var newPayload = File.ReadAllBytes(match.ModFilePath);
                if (!StreamIndex.LooksLikeOgg(newPayload))
                    throw new InvalidDataException($"'{match.ModFilePath}' doesn't look like a valid Ogg file (missing 'OggS' header).");
                PatchStreamTrack(match, newPayload);

                streamSummaries.Add(new DirectStreamSummary(match.MatchKey, backedUp));
            }
            catch (Exception ex)
            {
                streamFailed.Add(new DirectStreamFailure(match.MatchKey, ex.Message));
            }
        }

        var unarchivedSummaries = new List<DirectUnarchivedSummary>();
        var unarchivedFailed = new List<DirectUnarchivedFailure>();
        var unarchivedGroupIndex = totalGroups;
        for (var i = 0; i < plan.UnarchivedMatches.Count; i++)
        {
            var match = plan.UnarchivedMatches[i];

            progress?.Report(new DirectInstallProgress(
                match.RelativePath, unarchivedGroupIndex, totalGroups, "Replacing game files", i + 1, plan.UnarchivedMatches.Count));

            try
            {
                var backedUp = false;
                if (backupOutputFolder is not null)
                {
                    var backupPath = Path.Combine(backupOutputFolder, match.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    File.Copy(match.AbsolutePath, backupPath, overwrite: true);
                    backedUp = true;
                }

                // Staged next to the target and swapped in via the same crash-safe rename path the
                // archives use, rather than writing over the original directly — an interrupted
                // copy must never be able to leave a half-written game file behind.
                var stagedPath = match.AbsolutePath + ".saft-tmp";
                File.Copy(match.ModFilePath, stagedPath, overwrite: true);
                FileReplace.MoveOver(stagedPath, match.AbsolutePath);

                unarchivedSummaries.Add(new DirectUnarchivedSummary(match.RelativePath, backedUp));
            }
            catch (Exception ex)
            {
                // Same resilience rule as audio: one unwritable file (permissions, a lock, a full
                // disk) doesn't abandon everything else in the batch.
                unarchivedFailed.Add(new DirectUnarchivedFailure(match.RelativePath, ex.Message));
            }
        }

        return new DirectInstallResult(
            summaries, audioSummaries, streamSummaries, audioFailed, streamFailed, unarchivedSummaries, unarchivedFailed);
    }

    private static void BackupAudioOriginal(DirectAudioMatch match, string backupOutputFolder)
    {
        using var stream = File.OpenRead(match.PackageAbsolutePath);
        var bank = SfxBank.Read(stream, match.BankHeaderOffset, match.BankLength);
        stream.Position = bank.GetPcmOffset(match.SoundIndex);
        var pcm = new byte[bank.GetPcmLength(match.SoundIndex)];
        stream.ReadExactly(pcm);

        var destPath = Path.Combine(backupOutputFolder, "audio", "sfx", match.MatchKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        using var outFile = File.Create(destPath);
        WavPcm.WriteMono16Wav(outFile, pcm, bank.Sounds[match.SoundIndex].SampleRate);
    }

    private static void PatchAudioSound(DirectAudioMatch match, byte[] newPcm) =>
        SfxBank.PatchSound(match.PackageAbsolutePath, match.BankHeaderOffset, match.BankLength, match.SoundIndex, newPcm);

    private static void BackupStreamOriginal(DirectStreamMatch match, string backupOutputFolder)
    {
        using var stream = File.OpenRead(match.StationAbsolutePath);
        var payloadOffset = match.HeaderOffset + StreamIndex.TrackHeaderSize;
        stream.Position = payloadOffset;
        var encrypted = new byte[match.OriginalPayloadLength];
        stream.ReadExactly(encrypted);
        var decrypted = StreamXor.Transform(encrypted, payloadOffset);

        var destPath = Path.Combine(backupOutputFolder, "audio", "streams", match.MatchKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        File.WriteAllBytes(destPath, decrypted);
    }

    private static void PatchStreamTrack(DirectStreamMatch match, byte[] newPayload) =>
        StreamIndex.PatchTrack(match.StationAbsolutePath, match.HeaderOffset, match.OriginalPayloadLength, newPayload);

    private static void BackupOriginals(
        string archiveAbsolutePath, string archiveRelativePath, IReadOnlyList<DirectInstallMatch> matches,
        string backupOutputFolder, Action<int, int>? onProgress)
    {
        using var archive = ImgArchive.Open(archiveAbsolutePath);
        for (var i = 0; i < matches.Count; i++)
        {
            var entry = archive.Entries.First(e => e.Name.Equals(matches[i].EntryName, StringComparison.OrdinalIgnoreCase));
            var bucket = ImgEntry.GetBucketFolderName(entry.Name);
            var destDir = Path.Combine(backupOutputFolder, archiveRelativePath, bucket);
            Directory.CreateDirectory(destDir);

            using (var src = archive.OpenEntry(entry))
            using (var dst = new FileStream(Path.Combine(destDir, entry.Name), FileMode.Create, FileAccess.Write, FileShare.None))
                src.CopyTo(dst);

            onProgress?.Invoke(i + 1, matches.Count);
        }
    }

    /// <summary>
    /// Overwrites exactly each entry's existing byte range with the new content, zero-padding any
    /// leftover space up to the original allocation. The directory table (offsets, sizes, order)
    /// never changes, since by construction every replacement fits within what was already there.
    /// </summary>
    private static void PatchArchiveInPlace(string archiveAbsolutePath, IReadOnlyList<DirectInstallMatch> matches, Action<int, int>? onProgress)
    {
        List<(ImgEntry Entry, string ModFilePath)> targets;
        using (var archive = ImgArchive.Open(archiveAbsolutePath))
        {
            targets = matches
                .Select(m => (
                    Entry: archive.Entries.First(e => e.Name.Equals(m.EntryName, StringComparison.OrdinalIgnoreCase)),
                    m.ModFilePath))
                .ToList();
        } // read handle must close before we reopen the same path for writing

        using var writeStream = new FileStream(archiveAbsolutePath, FileMode.Open, FileAccess.Write, FileShare.Read);
        for (var i = 0; i < targets.Count; i++)
        {
            var (entry, modFilePath) = targets[i];
            writeStream.Position = entry.ByteOffset;
            using (var content = File.OpenRead(modFilePath))
                content.CopyTo(writeStream);

            var remaining = entry.ByteSize - new FileInfo(modFilePath).Length;
            if (remaining > 0)
                writeStream.Write(new byte[remaining]);

            onProgress?.Invoke(i + 1, targets.Count);
        }
    }

    /// <summary>
    /// Rebuilds one archive from its own current entries (unchanged ones streamed straight from
    /// the still-open original; matched ones from the mod files) into a temp file, then swaps it
    /// in — used only for archives where at least one replacement doesn't fit in place.
    /// </summary>
    private static void RebuildArchiveWithReplacements(string archiveAbsolutePath, IReadOnlyList<DirectInstallMatch> matches, Action<int, int>? onProgress)
    {
        var logDir = Path.GetDirectoryName(archiveAbsolutePath)!;
        DiagnosticLog.Write(logDir, $"RebuildArchiveWithReplacements: starting for '{archiveAbsolutePath}' ({matches.Count} replacement(s))");

        var replacementsByName = matches.ToDictionary(m => m.EntryName, m => m.ModFilePath, StringComparer.OrdinalIgnoreCase);
        var tempPath = archiveAbsolutePath + ".saft-tmp";

        // A previous attempt that failed after finishing the rebuild but before the final move
        // (exactly what just happened) leaves this file behind — always start from a guaranteed-
        // fresh path rather than relying on ImgArchive.Write's FileMode.Create to correctly
        // overwrite whatever's already there.
        DiagnosticLog.Write(logDir, $"Checking for leftover temp file at '{tempPath}'");
        if (File.Exists(tempPath))
        {
            DiagnosticLog.Write(logDir, "Leftover temp file found — deleting it");
            File.Delete(tempPath);
            DiagnosticLog.Write(logDir, "Leftover temp file deleted");
        }

        DiagnosticLog.Write(logDir, "Opening original archive for reading");
        using (var archive = ImgArchive.Open(archiveAbsolutePath))
        {
            DiagnosticLog.Write(logDir, $"Original archive opened successfully — {archive.Entries.Count} entries");

            var files = archive.Entries
                .Select(entry => (
                    Name: entry.Name,
                    OpenContent: (Func<Stream>)(() =>
                        replacementsByName.TryGetValue(entry.Name, out var modPath)
                            ? File.OpenRead(modPath)
                            : archive.OpenEntry(entry))))
                .ToList();

            DiagnosticLog.Write(logDir, $"Starting ImgArchive.Write to '{tempPath}' with {files.Count} file(s)");
            ImgArchive.Write(tempPath, files, onFileWritten: (done, total) =>
            {
                if (done == 1 || done == total || done % 2000 == 0)
                    DiagnosticLog.Write(logDir, $"Write progress: {done} of {total}");
                onProgress?.Invoke(done, total);
            });
            DiagnosticLog.Write(logDir, "ImgArchive.Write completed");
        } // read handle on the original must close before we overwrite it
        DiagnosticLog.Write(logDir, "Original archive handle closed");

        // A rename (same volume, since tempPath is right next to archiveAbsolutePath), not a full
        // second read-and-write of the whole archive — File.Copy+Delete here was doing needless
        // extra I/O of the entire archive a second time for no reason, which matters a lot more on
        // a resource-constrained platform than on a real Windows machine with I/O to spare.
        DiagnosticLog.Write(logDir, $"Moving '{tempPath}' into place over '{archiveAbsolutePath}'");
        FileReplace.MoveOver(tempPath, archiveAbsolutePath);
        DiagnosticLog.Write(logDir, "Move completed successfully — rebuild finished");
    }
}
