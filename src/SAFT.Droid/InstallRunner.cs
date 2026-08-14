using SAFT.Core;

namespace SAFT.Droid;

/// <summary>
/// Everything the checks worked out, carried from the analysis to the dialog to the install.
///
/// The split exists so the user can be told what is about to happen while there is still time to
/// say no. The Windows build does the same thing with a run of popups; here it is one analysis, one
/// dialog, one install.
/// </summary>
internal sealed record InstallAnalysis(
    string GameFolder,
    string ModFolder,
    string BackupFolder,
    string ModName,
    GameFiles GameListing,
    GameFiles ModListing,
    DirectInstallPlan Plan,
    AdditionPlan? Additions,
    HashSet<string>? ExistingNames,
    AdditionsManifest? PriorManifest,
    bool IsReinstall,
    StreamingVerdict Verdict,
    string? Refusal)
{
    /// <summary>True when SAFT will not do this at all, and the dialog offers no way forward.</summary>
    public bool Refused => Refusal is not null;

    /// <summary>What the dialog shows: what it would replace, what it would add, and what that costs.</summary>
    public string Describe()
    {
        var lines = new List<string>();

        var replacing = Plan.Matches.Count + Plan.UnarchivedMatches.Count
                      + Plan.AudioMatchesThatFit.Count + Plan.StreamMatchesThatFit.Count;
        lines.Add($"REPLACES {replacing} file(s) the game already has.");

        if (Additions is null)
        {
            lines.Add("ADDS nothing — this mod only replaces files.");
        }
        else
        {
            lines.Add($"ADDS {Additions.NewAssets.Count} new file(s) as {Additions.SlotsRequired} new " +
                      $"object(s), placed {Additions.Placements.Count} time(s) in the world.");
            lines.Add($"Uses {Additions.SlotsRequired} of {Additions.SlotsAvailable} free object slots.");
        }

        if (IsReinstall)
            lines.Add($"\n'{ModName}' is already installed. The old copy comes out and this one goes in, " +
                      "in a single rewrite.");

        if (Plan.Ambiguous.Count > 0)
            lines.Add($"\n{Plan.Ambiguous.Count} file(s) matched more than one place in the game and will " +
                      "be left alone rather than guessed at.");

        if (Plan.AudioMatchesTooLarge.Count + Plan.StreamMatchesTooLarge.Count > 0)
            lines.Add($"\n{Plan.AudioMatchesTooLarge.Count + Plan.StreamMatchesTooLarge.Count} audio file(s) " +
                      "are too big for the space they must fit and cannot be replaced.");

        if (!string.IsNullOrWhiteSpace(Verdict.Message))
            lines.Add("\n" + Verdict.Message.Trim());

        if (Refusal is not null) lines.Add("\nSAFT WILL NOT INSTALL THIS:\n" + Refusal);

        return string.Join("\n", lines);
    }
}

/// <summary>
/// Installing a mod, in the same order the Windows build does it.
///
/// This is a port of a sequence rather than a fresh design, and deliberately so — the order of these
/// steps is not arbitrary, and most of it was arrived at by watching something go wrong. Collision
/// records are pruned before they are merged, or a mod ends up with no collision and the game
/// crashes on world load. A reinstall's removal hands its archive work to the addition rewrite
/// instead of doing its own, or a 900 MB file gets written three times for a job that changes a
/// handful of entries. The record of what was added is saved AFTER the rewrite it describes, never
/// before, because a process that dies in between must not leave entries in an archive with nothing
/// recording them — SAFT only installs what it can uninstall.
///
/// Nothing here is Android-specific. It is the engine, called in the order the engine expects.
/// </summary>
internal static class InstallRunner
{
    /// <summary>Reasons SAFT will not install, which are all better found before anything is written.</summary>
    public static string? WhyNotInstallable(AdditionPlan plan) =>
        plan.PlacesModelsWithoutCollision
            ? $"{plan.ModelsWithoutCollision.Count} model(s) are placed with no collision record. " +
              "San Andreas crashes on world load without one."
        : !plan.FitsInAvailableSlots
            ? $"This mod needs {plan.SlotsRequired} object slots and only {plan.SlotsAvailable} are free."
        : plan.LacksPlacementData
            ? "This mod brings new files but no .ide data to register them, so the game would never " +
              "show them. They would take up object slots and appear nowhere."
            : null;

    /// <summary>
    /// Works out everything about this install WITHOUT touching the game. Reads only.
    ///
    /// This is the stretch of work that hung and got the process killed under Winlator — the map read
    /// inside it most of all — and it is also where the numbers come from that a person needs to
    /// decide whether a mod is a good idea: how much it adds to what the game must stream, how it
    /// compares to the busiest place the game already ships with, whether it fits at all.
    /// </summary>
    public static InstallAnalysis Analyse(
        string gameFolder, string modFolder, string backupFolder, string modName, Action<string> say)
    {
        say("listing the game and the mod");
        var gameListing = GameFiles.Walk(gameFolder, say);
        var modListing = GameFiles.Walk(modFolder, say);

        // Before the analysis goes any further, let alone before anything is written: backing the
        // originals up into the mod folder itself would file them alongside the mod's own files,
        // where the next scan reads them back as things to install.
        DirectModInstaller.EnsureBackupFolderIsSeparate(modFolder, backupFolder);

        say("\nworking out which files replace what");
        var plan = DirectModInstaller.Plan(gameFolder, modFolder, say, gameListing, modListing);

        AdditionPlan? additions = null;
        HashSet<string>? existingNames = null;
        StreamingVerdict verdict;

        if (!ModContent.AffectsStreaming(modFolder, modListing))
        {
            say("mod has no models, textures or map data; skipping the map read and weight checks");
            verdict = StreamingAdvice.ComposeWithoutStreamingContent();
        }
        else
        {
            existingNames = GameFileNames(gameFolder, gameListing);
            say($"game already holds {existingNames.Count:N0} known file name(s)");

            // Read ONCE and handed to both the addition scan and the weight measurement. They each
            // used to read the whole game independently for the same answer.
            var gameMap = GameSnapshot.Read(gameFolder, say, gameListing);
            var usedIds = ObjectIdAllocator.UsedIdsFrom(gameMap.Definitions);

            additions = AdditionScanner.Scan(
                gameFolder, modFolder, existingNames.Contains, gameMap.Baseline, usedIds, say, modListing,
                gameDefinitions: gameMap.Definitions);

            say("measuring what this adds to what your game has to load");
            var impact = StreamingImpact.Measure(gameFolder, ReplacementSizes(plan), say, gameMap);

            // The baseline goes in whether or not this mod adds anything: it describes the game being
            // installed into, which matters just as much for a pure replacement.
            verdict = StreamingAdvice.Compose(
                additions.HasAdditions ? additions.Density : null, impact, additions.Density.Baseline);

            if (!additions.HasAdditions) additions = null;
        }

        var priorManifest = AdditionsManifest.Load(backupFolder);
        var isReinstall = priorManifest?.Mods
            .Any(m => m.Name.Equals(modName, StringComparison.OrdinalIgnoreCase)) == true;

        return new InstallAnalysis(
            gameFolder, modFolder, backupFolder, modName, gameListing, modListing, plan, additions,
            existingNames, priorManifest, isReinstall, verdict,
            additions is null ? null : WhyNotInstallable(additions));
    }

    /// <summary>How big each replacement is, which is what the streaming measurement weighs.</summary>
    private static Dictionary<string, long> ReplacementSizes(DirectInstallPlan plan)
    {
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var match in plan.Matches)
        {
            try { sizes[match.FileName] = new FileInfo(match.ModFilePath).Length; } catch { }
        }

        foreach (var match in plan.UnarchivedMatches)
        {
            try { sizes[match.FileName] = new FileInfo(match.ModFilePath).Length; } catch { }
        }

        return sizes;
    }

    /// <summary>Everything from here writes. Nothing above it does.</summary>
    /// <param name="warn">See the note on Jobs.Uninstall - the SD card warning goes on screen.</param>
    public static void Apply(InstallAnalysis a, Action<string> say, Action<string, string>? warn = null)
    {
        if (a.Refused) throw new InvalidOperationException(a.Refusal);

        // In-place editing writes a sidecar before it touches the archive, so an install killed
        // half-way can be put back. Nothing puts it back on its own, though — recovery has to be
        // asked for, and the moment to ask is before writing to the same archive again.
        var recovered = ImgArchiveEditor.RecoverAll(a.GameFolder, a.GameListing, say);
        if (recovered.Count > 0)
            say($"recovered {recovered.Count} archive(s) from an interrupted edit before starting");

        // The archive additions are written into. Replacements bound for this same archive are folded
        // into that one rewrite rather than each rebuilding it themselves.
        // Worked out BEFORE the fold, not just before the write.
        //
        // The mod's own additions are not replacements, and the fold below reads the replacement
        // plan to decide what to carry into the additions rewrite. Filtering only at the Apply
        // call left the fold reading the UNFILTERED plan, so an asset the mod defines was handed
        // over as a replacement and installed as an addition both - and in the rewrite loop a
        // replacement is checked before a removal, so the old copy was kept and the new one
        // appended beside it. Seven leftovers came out as fourteen entries under seven names.
        var toApply = a.Additions is null ? a.Plan : a.Plan.Without(a.Additions.AssetFileNames);
        if (toApply.Matches.Count != a.Plan.Matches.Count)
            say($"{a.Plan.Matches.Count - toApply.Matches.Count} file(s) the mod defines itself left to the addition installer");

        var deferredArchive = AdditionInstaller.DefaultArchiveRelativePath;

        var deferredReplacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var deferredDrops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deferredPrunes = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        HashSet<string>? deferRebuildsFor = null;

        var additions = a.Additions;

        if (additions is not null)
        {
            foreach (var match in toApply.Matches.Where(m =>
                         m.ArchiveRelativePath.Equals(deferredArchive, StringComparison.OrdinalIgnoreCase)))
                deferredReplacements[match.EntryName] = match.ModFilePath;

            if (deferredReplacements.Count > 0)
            {
                deferRebuildsFor = new HashSet<string>(new[] { deferredArchive }, StringComparer.OrdinalIgnoreCase);
                say($"folding {deferredReplacements.Count} replacement(s) into the additions rewrite");
            }
        }

        // One watcher for the whole install. It times the byte count the write loops already keep, so
        // a crawl can be reported as the storage catching its breath rather than SAFT having stopped.
        var speed = new StorageSpeed();

        say("\nwriting replacements");
        // The mod's own additions are not replacements, so the replacement pass gives them up.
        //
        // The two passes ask different questions of the same folder: "does the game already have
        // a file by this name" (which a modded game answers yes to, for the mod's own leftovers)
        // versus "does the mod define this in its .ide" (which only the mod can answer). The
        // second is the truthful one. Without this, an asset left behind by an earlier round gets
        // backed up as though the copy in the game were stock - which is how a backup folder came
        // to hold the modpack's own files under the name of the originals.
        var result = DirectModInstaller.Apply(
            toApply, a.BackupFolder,
            new Reporter<DirectInstallProgress>(p => say($"   {p.Stage} {p.FilesDone}/{p.FilesTotal}")),
            say, deferRebuildsFor, speed);

        say($"replaced {result.Archives.Sum(s => s.FilesReplaced)} file(s) across {result.Archives.Count} archive(s)");

        if (additions is null)
        {
            say($"\n{speed.Describe()}");
            say("\nINSTALL COMPLETE.");
            if (speed.Warning() is { } tiredEarly) warn?.Invoke("Give your SD card a rest", tiredEarly);
            return;
        }

        var additionProgress = new Reporter<AdditionProgress>(p => say($"   {p.Stage} {p.FilesDone}/{p.FilesTotal}"));

        if (a.IsReinstall && a.PriorManifest is not null)
        {
            say("\nremoving the previously installed copy (archive rewrite deferred)");
            var removal = AdditionUninstaller.Remove(
                a.GameFolder, a.PriorManifest, new[] { a.ModName }, additionProgress,
                new HashSet<string>(new[] { deferredArchive }, StringComparer.OrdinalIgnoreCase),
                say, speed);

            if (removal.DeferredEntryRemovals.TryGetValue(deferredArchive, out var dropped))
                deferredDrops.UnionWith(dropped);
            foreach (var bundle in removal.DeferredCollisionPrunes)
                deferredPrunes[bundle.Key] = bundle.Value;

            foreach (var note in removal.Skipped) say($"   left alone - {note}");
            say($"old copy's map data removed; {deferredDrops.Count} entry/entries and " +
                $"{deferredPrunes.Values.Sum(r => r.Count)} collision record(s) handed to the rewrite");

            // The old copy's assets and ids are gone, so what counts as "new" has changed. Its
            // ENTRIES are still physically in the archive - they come out with the rewrite below - so
            // they are struck off the list of what the game has, or this rescan would call the mod's
            // own assets replacements of themselves.
            say("rechecking what this mod adds");
            var names = a.ExistingNames is not null
                ? new HashSet<string>(a.ExistingNames, StringComparer.OrdinalIgnoreCase)
                : GameFileNames(a.GameFolder, a.GameListing);
            names.ExceptWith(deferredDrops);

            additions = AdditionScanner.Scan(
                a.GameFolder, a.ModFolder, names.Contains,
                baseline: null, usedObjectIds: null, onStep: say, modFiles: a.ModListing);

            // A name can be on both lists: coming out with the old copy, and going back in from the
            // mod folder. Exactly one route must put it back, or the archive ends up with the entry
            // twice - or, worse, not at all.
            var readdedByAdditions = new HashSet<string>(
                additions.NewAssets.Select(x => x.FileName), StringComparer.OrdinalIgnoreCase);
            foreach (var name in deferredDrops.ToList())
            {
                if (readdedByAdditions.Contains(name)) deferredReplacements.Remove(name);
                else if (deferredReplacements.ContainsKey(name)) deferredDrops.Remove(name);
            }
        }

        say($"\nrewriting the archive once - new assets plus {deferredReplacements.Count} folded " +
            $"replacement(s) and {deferredDrops.Count} folded removal(s)");
        var added = AdditionInstaller.Apply(
            a.GameFolder, additions, a.ModName, additionProgress, deferredReplacements,
            deferredDrops, deferredPrunes, speed, say);

        say($"added {added.Recorded.ArchiveEntries.Count} archive entry/entries, {added.Problems.Count} problem(s)");
        foreach (var problem in added.Problems) say($"   {problem}");

        // Saved only now the rewrite has actually happened. See the note at the top of this file.
        var manifest = AdditionsManifest.Load(a.BackupFolder)
                       ?? new AdditionsManifest { GameRootPath = a.GameFolder };
        manifest.Mods.RemoveAll(m => m.Name.Equals(a.ModName, StringComparison.OrdinalIgnoreCase));
        manifest.Mods.Add(added.Recorded);
        manifest.Save(a.BackupFolder);
        say("recorded what was added, so it can be uninstalled again");

        say($"\n{speed.Describe()}");
        say("\nINSTALL COMPLETE.");

        if (speed.Warning() is { } tired) warn?.Invoke("Give your SD card a rest", tired);
    }

    /// <summary>
    /// Every name the game already knows, archive entries included — the single question that
    /// separates an addition from a replacement.
    /// </summary>
    public static HashSet<string> GameFileNames(string gameRoot, GameFiles listing)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var found in GameScanner.FindArchives(gameRoot, listing))
        {
            try
            {
                // Names only, so the archive is never held open for it.
                foreach (var entry in ImgArchive.ReadDirectory(found.AbsolutePath)) names.Add(entry.Name);
            }
            catch
            {
                // An unreadable archive just means those names look new, which errs towards asking.
            }
        }

        foreach (var group in UnarchivedIndex.Build(gameRoot, listing)) names.Add(group.Key);
        return names;
    }

    /// <summary>
    /// The read-only half of an install, timed. Writes nothing, and stays available on its own as
    /// the fastest way to tell whether something about a particular game or mod is pathological.
    /// </summary>
    public static void Check(string gameFolder, string modFolder, Action<string> say)
    {
        var total = System.Diagnostics.Stopwatch.StartNew();
        var analysis = Analyse(gameFolder, modFolder, gameFolder, "check", say);

        say("\n" + analysis.Describe());
        say($"\nCHECK COMPLETE — {total.ElapsedMilliseconds:N0} ms. Nothing was written.");
    }

    private sealed class Reporter<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }
}
