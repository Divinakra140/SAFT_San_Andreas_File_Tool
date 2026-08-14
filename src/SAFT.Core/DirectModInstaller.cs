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
    /// <summary>
    /// <paramref name="onStep"/> is a diagnostic breadcrumb sink, called before each phase. Planning
    /// is several seconds of work behind a single call, and when the process was killed part way
    /// through it there was no way to tell which phase it died in. The app passes ActivityLog.Note.
    /// </summary>
    public static DirectInstallPlan Plan(
        string gameRoot, string modSourceFolder, Action<string>? onStep = null, GameFiles? listing = null,
        GameFiles? modListing = null)
    {
        onStep?.Invoke("plan: finding archives");
        // One listing for the archive search and the loose-file index, which walked the game folder
        // three times between them. See GameFiles.
        var files = GameFiles.For(gameRoot, listing, onStep);
        var foundArchives = GameScanner.FindArchives(gameRoot, files);
        var index = new Dictionary<string, List<(FoundArchive Archive, ImgEntry Entry)>>(StringComparer.OrdinalIgnoreCase);

        onStep?.Invoke($"plan: indexing {foundArchives.Count} archive(s)");
        foreach (var found in foundArchives)
        {
            // The table only — the entries are matched against the mod's file names here, and read
            // for real later by whatever ends up patching or rebuilding. See ImgArchive.ReadDirectory.
            foreach (var entry in ImgArchive.ReadDirectory(found.AbsolutePath))
            {
                if (!index.TryGetValue(entry.Name, out var targets))
                {
                    targets = new List<(FoundArchive, ImgEntry)>();
                    index[entry.Name] = targets;
                }
                targets.Add((found, entry));
            }
        }

        // What the mod actually contains, read once and reused for the matching loop below. This
        // exists to answer one question before doing any expensive work: does this mod have audio?
        // The walk itself is shared with everything else that reads this folder — see GameFiles.
        var modFiles = GameFiles.For(modSourceFolder, modListing, onStep).Paths
            .Where(p => !FileFilters.IsIgnoredFile(Path.GetFileName(p)))
            .ToList();

        var hasWav = modFiles.Any(p => p.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
        var hasOgg = modFiles.Any(p => p.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase));

        // Only the slots this mod names are read - see LookUpAudioSlots. The keys are derived here
        // rather than inside it so the matching loop below and the lookup agree on exactly one
        // definition of what a .wav's key is.
        Dictionary<string, AudioSlot> audioIndex;
        if (hasWav)
        {
            var wantedKeys = modFiles
                .Where(p => p.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                .Select(p => FileFilters.GetLastPathSegments(p, 3))
                .Where(k => k is not null)
                .Select(k => k!)
                .ToList();

            onStep?.Invoke($"plan: {index.Count} archive entry name(s) indexed; looking up {wantedKeys.Count} sound slot(s)");
            audioIndex = LookUpAudioSlots(gameRoot, wantedKeys, onStep);
            onStep?.Invoke($"plan: {audioIndex.Count} sound slot(s) found");
        }
        else
        {
            audioIndex = new Dictionary<string, AudioSlot>(StringComparer.OrdinalIgnoreCase);
            onStep?.Invoke($"plan: {index.Count} archive entry name(s) indexed; mod has no .wav files, skipping SFX lookup");
        }

        Dictionary<string, StreamSlot> streamIndex;
        if (hasOgg)
        {
            onStep?.Invoke("plan: mod has .ogg files, indexing streamed audio");
            streamIndex = BuildStreamIndex(gameRoot);
            onStep?.Invoke($"plan: {streamIndex.Count} track(s) indexed");
        }
        else
        {
            streamIndex = new Dictionary<string, StreamSlot>(StringComparer.OrdinalIgnoreCase);
            onStep?.Invoke("plan: mod has no .ogg files, skipping streamed audio index");
        }

        onStep?.Invoke("plan: indexing loose game files");
        var unarchivedIndex = UnarchivedIndex.Build(gameRoot, files);

        onStep?.Invoke($"plan: {unarchivedIndex.Count} loose name(s); matching mod files");

        var matches = new List<DirectInstallMatch>();
        var unmatched = new List<string>();
        var audioMatches = new List<DirectAudioMatch>();
        var audioUnmatched = new List<string>();
        var streamMatches = new List<DirectStreamMatch>();
        var streamUnmatched = new List<string>();
        var unarchivedMatches = new List<DirectUnarchivedMatch>();
        var ambiguous = new List<DirectAmbiguousMatch>();
        var refusedScripts = new List<RefusedScript>();

        foreach (var sourcePath in modFiles)
        {
            var fileName = Path.GetFileName(sourcePath);

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

    internal sealed record AudioSlot(
        SfxPackage Package, string PackageRelativePath, long BankHeaderOffset, long BankLength, int SoundIndex, long OriginalPcmLength);

    /// <summary>
    /// Looks up only the sound slots this mod actually names, instead of indexing the whole game.
    ///
    /// The old version walked all nine packages and read the header of every bank in each - 710 banks,
    /// 61,993 slots on a stock game - to build a dictionary that a mod with 45 .wav files would then
    /// make 45 lookups against. Everything else was thrown away. On an SD card under Winlator one run
    /// spent 34 minutes in that loop; the crashes we chased for a day were all in the long stretch of
    /// work an audio install did before it wrote anything.
    ///
    /// It was never necessary. A mod's folder layout IS the key: Package/Bank_NNN/sound_NNN.wav says
    /// exactly which package, which bank and which slot, so the bank header can be read directly.
    /// Requests are grouped by bank so a package is opened once and each bank read once, however many
    /// sounds the mod replaces in it.
    ///
    /// A key naming a package, bank or slot the game doesn't have simply doesn't come back, which is
    /// the same answer a missing dictionary entry gave before - the caller already reports those as
    /// unmatched.
    /// </summary>
    internal static Dictionary<string, AudioSlot> LookUpAudioSlots(
        string gameRoot, IEnumerable<string> wantedKeys, Action<string>? onStep = null)
    {
        var index = new Dictionary<string, AudioSlot>(StringComparer.OrdinalIgnoreCase);

        // Grouped as package -> bank -> the slots wanted from that bank, so the reads below can be
        // driven straight off the structure rather than re-parsing keys.
        var wanted = new Dictionary<string, Dictionary<int, List<(int SoundIndex, string Key)>>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var key in wantedKeys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!TryParseAudioKey(key, out var packageName, out var bankNumber, out var soundNumber)) continue;

            if (!wanted.TryGetValue(packageName, out var banks))
            {
                banks = new Dictionary<int, List<(int, string)>>();
                wanted[packageName] = banks;
            }
            if (!banks.TryGetValue(bankNumber, out var slots))
            {
                slots = new List<(int, string)>();
                banks[bankNumber] = slots;
            }
            slots.Add((soundNumber - 1, key));
        }

        if (wanted.Count == 0) return index;

        var packages = SfxIndex.Load(gameRoot)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var (packageName, banks) in wanted)
        {
            if (!packages.TryGetValue(packageName, out var pkg)) continue;

            onStep?.Invoke($"plan: sfx {packageName}, reading {banks.Count} bank(s) of {pkg.Banks.Count}");
            using var stream = File.OpenRead(pkg.AbsolutePath);
            var packageRelativePath = Path.GetRelativePath(gameRoot, pkg.AbsolutePath);

            foreach (var (bankNumber, slots) in banks)
            {
                if (bankNumber < 1 || bankNumber > pkg.Banks.Count) continue;

                var (offset, length) = pkg.Banks[bankNumber - 1];
                var bank = SfxBank.Read(stream, offset, length);

                foreach (var (soundIndex, key) in slots)
                {
                    if (soundIndex < 0 || soundIndex >= bank.Sounds.Count) continue;
                    index[key] = new AudioSlot(
                        pkg, packageRelativePath, offset, length, soundIndex, bank.GetPcmLength(soundIndex));
                }
            }
        }

        return index;
    }

    /// <summary>
    /// Splits a Package/Bank_NNN/sound_NNN.wav key back into its three parts. The names are SAFT's
    /// own - it wrote this layout during extraction - so anything that doesn't match the shape is a
    /// .wav the user placed somewhere else, and is left to be reported as unmatched.
    /// </summary>
    internal static bool TryParseAudioKey(string key, out string packageName, out int bankNumber, out int soundNumber)
    {
        packageName = string.Empty;
        bankNumber = 0;
        soundNumber = 0;

        var parts = key.Split('/');
        if (parts.Length != 3) return false;

        if (!parts[1].StartsWith("Bank_", StringComparison.OrdinalIgnoreCase)) return false;
        if (!int.TryParse(parts[1].AsSpan("Bank_".Length), out bankNumber)) return false;

        var sound = parts[2];
        if (!sound.StartsWith("sound_", StringComparison.OrdinalIgnoreCase)) return false;
        if (!sound.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return false;
        var digits = sound["sound_".Length..^".wav".Length];
        if (!int.TryParse(digits, out soundNumber)) return false;

        packageName = parts[0];
        return packageName.Length > 0;
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
        DirectInstallPlan plan, string? backupOutputFolder, IProgress<DirectInstallProgress>? progress = null,
        Action<string>? onStep = null, IReadOnlySet<string>? deferRebuildsFor = null, StorageSpeed? speed = null)
    {
        // Breadcrumbs, per item, for the same reason Plan has them. The install path had none, so a
        // process that died in here left a log ending at whichever popup came before it - which for
        // an audio mod means 57 separate sounds and tracks with nothing to say which one it was on.
        onStep?.Invoke(
            $"apply: {plan.Matches.Count} archived, {plan.AudioMatchesThatFit.Count} sfx, " +
            $"{plan.StreamMatchesThatFit.Count} tracks, {plan.UnarchivedMatches.Count} loose");

        // Throttled at the door, so every progress?.Report below it - including the ones passed
        // down into the private helpers - costs a UI round trip ten times a second rather than
        // once per file. See ThrottledProgress: per-file reporting is what made a full extraction
        // take hours under Winlator.
        progress = new ThrottledProgress<DirectInstallProgress>(progress);

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

            onStep?.Invoke($"apply: archive {archiveIndex} {archiveRelativePath}, {group.Count} entry/entries, rebuild={needsRebuild}");

            if (backupOutputFolder is not null)
            {
                BackupOriginals(archiveAbsolutePath, archiveRelativePath, group, backupOutputFolder,
                    (done, total) => progress?.Report(new DirectInstallProgress(
                        archiveRelativePath, archiveIndex, totalGroups, "Backing up originals", done, total)));
            }

            // Adding new assets to an archive rewrites it in full - there is no way to append an
            // entry in place - so a mod that both replaces oversized files AND adds assets used to
            // rewrite the same archive TWICE in one install. On a real device that was models\gta3.img,
            // 940 MB, written out twice: the first pass took 36.8 seconds and the process was killed
            // during the second. The originals are backed up above either way, so handing these
            // replacements to the additions rewrite loses nothing and halves the heaviest thing SAFT
            // does. If that rewrite never happens, the archive is simply untouched - which is a safer
            // failure than a half-applied one.
            if (deferRebuildsFor is not null && deferRebuildsFor.Contains(archiveRelativePath))
            {
                onStep?.Invoke(
                    $"apply: archive {archiveIndex} {archiveRelativePath}, {group.Count} entry/entries " +
                    "handed to the additions rewrite (one rebuild instead of two)");
                summaries.Add(new DirectInstallSummary(archiveRelativePath, group.Count, true));
                continue;
            }

            if (needsRebuild)
            {
                RebuildArchiveWithReplacements(archiveAbsolutePath, group,
                    (done, total) => progress?.Report(new DirectInstallProgress(
                        archiveRelativePath, archiveIndex, totalGroups, "Rebuilding (mod files are larger than the originals)", done, total)),
                    speed);
            }
            else
            {
                PatchArchiveInPlace(archiveAbsolutePath, group,
                    (done, total) => progress?.Report(new DirectInstallProgress(
                        archiveRelativePath, archiveIndex, totalGroups, "Patching in place", done, total)),
                    missing => onStep?.Invoke(
                        $"apply: '{missing}' is no longer in {archiveRelativePath}, so there is nothing to " +
                        "put back - skipped"));
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

            onStep?.Invoke($"apply: sfx {i + 1}/{audioToApply.Count} {match.MatchKey} ({match.OriginalPcmLength:N0} -> {match.NewPcmLength:N0} bytes)");

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

            onStep?.Invoke($"apply: track {i + 1}/{streamsToApply.Count} {match.MatchKey} ({match.OriginalPayloadLength:N0} -> {match.NewPayloadLength:N0} bytes)");

            try
            {
                var backedUp = false;
                if (backupOutputFolder is not null)
                {
                    BackupStreamOriginal(match, backupOutputFolder);
                    backedUp = true;
                }

                // Only the first four bytes are needed to know it is an Ogg; reading the whole file
                // to check them was a multi-megabyte allocation for a four-byte question.
                if (!LooksLikeOggFile(match.ModFilePath))
                    throw new InvalidDataException($"'{match.ModFilePath}' doesn't look like a valid Ogg file (missing 'OggS' header).");
                PatchStreamTrack(match, onStep);

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

            onStep?.Invoke($"apply: loose {i + 1}/{plan.UnarchivedMatches.Count} {match.RelativePath}");

            try
            {
                var backedUp = false;
                if (backupOutputFolder is not null)
                {
                    // Still counts as backed up when one is already there — that earlier copy is the
                    // vanilla file, which is exactly what a restore wants.
                    var backupPath = Path.Combine(backupOutputFolder, match.RelativePath);
                    if (NeedsBackup(backupPath)) File.Copy(match.AbsolutePath, backupPath);
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

        onStep?.Invoke("apply: finished");
        return new DirectInstallResult(
            summaries, audioSummaries, streamSummaries, audioFailed, streamFailed, unarchivedSummaries, unarchivedFailed);
    }

    /// <summary>
    /// True when a backup still needs writing at this path.
    ///
    /// A backup is never overwritten. Installing a second mod into the same backup folder — or the
    /// same mod twice — reaches this point with the game file already modded, so writing again would
    /// replace the vanilla copy with a modded one and quietly destroy the only way back. The first
    /// copy is always the closest thing to stock, so it wins; every later attempt is skipped.
    /// </summary>
    /// <summary>Whether a file starts with the four bytes "OggS", without reading the rest of it.</summary>
    private static bool LooksLikeOggFile(string path)
    {
        using var stream = File.OpenRead(path);
        var head = new byte[4];
        return stream.Read(head, 0, 4) == 4 && StreamIndex.LooksLikeOgg(head);
    }

    private static bool NeedsBackup(string destPath)
    {
        if (File.Exists(destPath)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        return true;
    }

    private static void BackupAudioOriginal(DirectAudioMatch match, string backupOutputFolder)
    {
        var destPath = Path.Combine(backupOutputFolder, "audio", "sfx", match.MatchKey.Replace('/', Path.DirectorySeparatorChar));
        if (!NeedsBackup(destPath)) return;

        using var stream = File.OpenRead(match.PackageAbsolutePath);
        var bank = SfxBank.Read(stream, match.BankHeaderOffset, match.BankLength);
        stream.Position = bank.GetPcmOffset(match.SoundIndex);
        var pcm = new byte[bank.GetPcmLength(match.SoundIndex)];
        stream.ReadExactly(pcm);

        using var outFile = File.Create(destPath);
        WavPcm.WriteMono16Wav(outFile, pcm, bank.Sounds[match.SoundIndex].SampleRate);
    }

    private static void PatchAudioSound(DirectAudioMatch match, byte[] newPcm) =>
        SfxBank.PatchSound(match.PackageAbsolutePath, match.BankHeaderOffset, match.BankLength, match.SoundIndex, newPcm);

    /// <summary>
    /// Copies one track out to the backup folder, decrypting as it goes, a chunk at a time.
    ///
    /// This used to allocate the entire payload and then hand it to StreamXor.Transform, which
    /// CLONES its input - so backing up BEATS/Track_002, 6.3 MB in a stock game, meant two 6.3 MB
    /// arrays alive together, with File.ReadAllBytes on the replacement about to make a third. Every
    /// one of those is a Large Object Heap allocation, the LOH is never compacted, and SAFT is a
    /// 32-bit process. It died here, on the largest track of the twelve, after the eleven smaller
    /// ones had installed without complaint.
    /// </summary>
    private static void BackupStreamOriginal(DirectStreamMatch match, string backupOutputFolder)
    {
        var destPath = Path.Combine(backupOutputFolder, "audio", "streams", match.MatchKey.Replace('/', Path.DirectorySeparatorChar));
        if (!NeedsBackup(destPath)) return;

        using var stream = File.OpenRead(match.StationAbsolutePath);
        var payloadOffset = match.HeaderOffset + StreamIndex.TrackHeaderSize;
        stream.Position = payloadOffset;

        using var backup = File.Create(destPath);
        var buffer = new byte[81920];
        var remaining = match.OriginalPayloadLength;
        var position = payloadOffset;

        while (remaining > 0)
        {
            var chunk = (int)Math.Min(buffer.Length, remaining);
            stream.ReadExactly(buffer, 0, chunk);
            StreamXor.Transform(buffer.AsSpan(0, chunk), position);   // in place, no second array
            backup.Write(buffer, 0, chunk);

            position += chunk;
            remaining -= chunk;
        }
    }

    private static void PatchStreamTrack(DirectStreamMatch match, Action<string>? onStep) =>
        StreamIndex.PatchTrack(
            match.StationAbsolutePath, match.HeaderOffset, match.OriginalPayloadLength, match.ModFilePath, onStep);

    private static void BackupOriginals(
        string archiveAbsolutePath, string archiveRelativePath, IReadOnlyList<DirectInstallMatch> matches,
        string backupOutputFolder, Action<int, int>? onProgress)
    {
        using var archive = ImgArchive.Open(archiveAbsolutePath);
        for (var i = 0; i < matches.Count; i++)
        {
            var entry = archive.Entries.First(e => e.Name.Equals(matches[i].EntryName, StringComparison.OrdinalIgnoreCase));
            var bucket = ImgEntry.GetBucketFolderName(entry.Name);
            var destPath = Path.Combine(backupOutputFolder, archiveRelativePath, bucket, entry.Name);

            if (NeedsBackup(destPath))
            {
                using var src = archive.OpenEntry(entry);
                using var dst = new FileStream(destPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                src.CopyTo(dst);
            }

            onProgress?.Invoke(i + 1, matches.Count);
        }
    }

    /// <summary>
    /// Overwrites exactly each entry's existing byte range with the new content, zero-padding any
    /// leftover space up to the original allocation. The directory table (offsets, sizes, order)
    /// never changes, since by construction every replacement fits within what was already there.
    /// </summary>
    /// <param name="onMissing">
    /// Told about any planned entry that is no longer in the archive. See below for why that is a
    /// normal thing to happen rather than an error.
    /// </param>
    private static void PatchArchiveInPlace(
        string archiveAbsolutePath, IReadOnlyList<DirectInstallMatch> matches, Action<int, int>? onProgress,
        Action<string>? onMissing = null)
    {
        // The directory INDEX is carried along, not just the entry: a replacement that is smaller
        // than what it replaces has to shrink that entry's size field, and the field's position in
        // the file is derived from the index.
        List<(int Index, ImgEntry Entry, string ModFilePath)> targets;
        using (var archive = ImgArchive.Open(archiveAbsolutePath))
        {
            // Looked up by name once rather than scanned per match: 16,000 entries against a plan of
            // a few hundred was quadratic for no reason.
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < archive.Entries.Count; i++) byName.TryAdd(archive.Entries[i].Name, i);

            targets = new List<(int, ImgEntry, string)>(matches.Count);
            foreach (var match in matches)
            {
                // An entry named in the plan that is NOT in the archive is skipped, not fatal.
                //
                // This is a real situation, not a corrupt-file case. An uninstall plans its restores
                // from the backup folder and then removes SAFT's added objects BEFORE applying them —
                // and a reinstall will have backed up those added assets as though they were
                // originals, because by then they were already in the archive. So the plan asks to
                // restore something the removal has just taken out. Skipping is not merely tolerant,
                // it is correct: putting it back would reinstate an asset the user asked to remove.
                //
                // This used to index the list with the -1 from a failed search, so the whole uninstall
                // died with "Index was out of range" and nothing was restored at all.
                if (!byName.TryGetValue(match.EntryName, out var index))
                {
                    onMissing?.Invoke(match.EntryName);
                    continue;
                }

                targets.Add((index, archive.Entries[index], match.ModFilePath));
            }
        } // read handle must close before we reopen the same path for writing

        if (targets.Count == 0) return;

        // Patching in place leaves the directory table untouched, so a cached copy stays correct —
        // but this is not the place to be clever about that. See ImgArchive.Write.
        ImgArchive.ClearCaches();

        using var writeStream = new FileStream(archiveAbsolutePath, FileMode.Open, FileAccess.Write, FileShare.Read);
        for (var i = 0; i < targets.Count; i++)
        {
            var (index, entry, modFilePath) = targets[i];
            var length = new FileInfo(modFilePath).Length;

            writeStream.Position = entry.ByteOffset;
            using (var content = File.OpenRead(modFilePath))
                content.CopyTo(writeStream);

            // Chunked, not one array the size of the gap. Restoring a small vanilla file into the
            // slot a large modded one grew to makes this remainder enormous — 27.6 MB was measured
            // uninstalling a 2.4x texture pack — and that single allocation is what took the process
            // down on 32-bit. See ZeroFill.
            var remaining = entry.ByteSize - length;
            if (remaining > 0)
                ZeroFill.Write(writeStream, remaining);

            // The size field is what the game streams by, so leaving it at the old value makes a
            // smaller replacement cost exactly what the bigger file it replaced did. That is not a
            // theoretical concern: a texture pack meant to lighten an area by a third was installed
            // correctly, byte for byte, and changed nothing in game, because this field still said
            // 28.8 MB for a 10.4 MB dictionary. It also feeds SAFT's own weighing, so every
            // measurement taken afterwards inherited the stale number too.
            var newSectors = (ushort)((length + ImgEntry.SectorSize - 1) / ImgEntry.SectorSize);
            if (newSectors != entry.SizeSectors)
            {
                writeStream.Position = ImgArchive.HeaderSize + (long)index * ImgArchive.DirEntrySize + sizeof(uint);
                writeStream.Write(BitConverter.GetBytes(newSectors));
            }

            onProgress?.Invoke(i + 1, targets.Count);
        }

        // Committed to the device before the handle closes, rather than left as dirty pages for the
        // OS to write back whenever it feels like it. The game is a separate process that may open
        // this archive seconds later, and on a phone writing to an SD card that write-back can still
        // be in flight — which looks exactly like the game hanging on its first launch after an
        // install and then being perfectly fine on the second.
        writeStream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Rebuilds one archive from its own current entries (unchanged ones streamed straight from
    /// the still-open original; matched ones from the mod files) into a temp file, then swaps it
    /// in — used only for archives where at least one replacement doesn't fit in place.
    /// </summary>
    private static void RebuildArchiveWithReplacements(
        string archiveAbsolutePath, IReadOnlyList<DirectInstallMatch> matches, Action<int, int>? onProgress,
        StorageSpeed? speed)
    {
        var replacementsByName = matches.ToDictionary(m => m.EntryName, m => m.ModFilePath, StringComparer.OrdinalIgnoreCase);
        var tempPath = archiveAbsolutePath + ".saft-tmp";

        // A previous attempt that failed after finishing the rebuild but before the final move
        // (exactly what just happened) leaves this file behind — always start from a guaranteed-
        // fresh path rather than relying on ImgArchive.Write's FileMode.Create to correctly
        // overwrite whatever's already there.
        if (File.Exists(tempPath)) File.Delete(tempPath);

        using (var archive = ImgArchive.Open(archiveAbsolutePath))
        {
            var files = archive.Entries
                .Select(entry => (
                    Name: entry.Name,
                    OpenContent: (Func<Stream>)(() =>
                        replacementsByName.TryGetValue(entry.Name, out var modPath)
                            ? File.OpenRead(modPath)
                            : archive.OpenEntry(entry))))
                .ToList();

            ImgArchive.Write(tempPath, files, onFileWritten: onProgress, onBytesWritten: speed is null ? null : speed.Sample);
        } // read handle on the original must close before we overwrite it

        // A rename (same volume, since tempPath is right next to archiveAbsolutePath), not a full
        // second read-and-write of the whole archive — File.Copy+Delete here was doing needless
        // extra I/O of the entire archive a second time for no reason, which matters a lot more on
        // a resource-constrained platform than on a real Windows machine with I/O to spare.
        FileReplace.MoveOver(tempPath, archiveAbsolutePath);
    }
}
