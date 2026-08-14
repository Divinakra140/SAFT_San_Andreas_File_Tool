namespace SAFT.Core;

/// <summary>A file in the mod folder with no counterpart anywhere in the game — an addition, not a replacement.</summary>
public sealed record NewAsset(string FileName, string SourcePath);

/// <summary>
/// What a mod folder is asking to ADD, as opposed to replace. Everything the install popups need
/// comes from here, so the numbers are computed once and reused rather than recounted per prompt.
/// </summary>
public sealed record AdditionPlan(
    IReadOnlyList<IdeDefinition> Definitions,
    IReadOnlyList<IplInstance> Placements,
    IReadOnlyList<NewAsset> NewAssets,
    IReadOnlyList<string> ModelsWithoutCollision,
    int SlotsAvailable,
    PlacementDensityReport Density,
    IReadOnlyList<ColRecord> Collision,
    IReadOnlyList<string> WalkThroughModels)
{
    /// <summary>
    /// Models this mod PLACES in the world with no collision record of their own.
    ///
    /// This is fatal, not cosmetic. San Andreas needs a collision model to work out an object's
    /// bounds when it builds the world, and without one it crashes on world load — before the player
    /// can reach the object, and wherever the object happens to be placed. Verified by taking a
    /// working install with the object visible in game, deleting only its collision record, and
    /// watching the game crash on loading the save; restoring the record fixed it.
    ///
    /// The record's CONTENTS are a separate matter — an empty one is fine, see
    /// <see cref="WalkThroughModels"/>. A model that is defined but never placed is harmless, which
    /// is why this looks at placements rather than definitions.
    /// </summary>
    public bool PlacesModelsWithoutCollision => ModelsWithoutCollision.Count > 0;

    /// <summary>
    /// One slot is one .ide definition — NOT one file. A .dff, .txd or .col costs nothing, being
    /// referenced by name, and placing the same object a hundred times still uses a single ID.
    /// </summary>
    public int SlotsRequired => Definitions.Count;

    /// <summary>
    /// The file names this mod is ADDING, for the replacement pass to leave alone. An asset claimed
    /// here must never be planned as a replacement or backed up as a stock original — it is the mod's
    /// own file, whatever a modded game directory happens to have sitting in it already.
    /// </summary>
    public IReadOnlySet<string> AssetFileNames =>
        NewAssets.Select(a => a.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public bool HasAdditions => NewAssets.Count > 0 || Definitions.Count > 0;

    public bool FitsInAvailableSlots => SlotsRequired <= SlotsAvailable;

    /// <summary>
    /// New assets turned up, but the mod shipped no .ide/.ide placement data to register them. SAFT
    /// could copy the files in, but the game would never show them — they would consume slots and
    /// appear nowhere, which is worse than not installing them.
    /// </summary>
    public bool LacksPlacementData => NewAssets.Count > 0 && Definitions.Count == 0;
}

/// <summary>
/// Works out which parts of a mod folder are additions rather than replacements, and everything
/// that follows from that: how many object slots are needed, whether the game has room, whether the
/// mod supplied the .ide/.ipl data needed to make its assets appear at all, and how concentrated
/// the result would be.
/// </summary>
public static class AdditionScanner
{
    /// <summary>
    /// Reads every definition in the game, for callers that are not already holding them. The ones
    /// that are (the install paths, which parse the same files to find free object IDs) pass theirs
    /// in instead — re-reading 60 .ide files per install is the duplication that made SAFT hang on
    /// an SD card still busy from the previous write.
    /// </summary>
    private static IReadOnlyList<IdeDefinition> ReadGameDefinitions(
        string gameRoot, string modSourceFolder, Action<string>? onStep)
    {
        var all = new List<IdeDefinition>();
        try
        {
            foreach (var path in IdeFile.FindAll(gameRoot))
            {
                if (IsUnder(path, modSourceFolder)) continue;
                all.AddRange(IdeFile.Parse(path));
            }
        }
        catch (Exception ex)
        {
            // Without these, everything already in the game reads as unclaimed. Say so rather than
            // silently falling back to the guess that got this wrong in the first place.
            onStep?.Invoke($"additions: could not read the game's definitions ({ex.GetType().Name}); treating existing files as unclaimed");
        }

        return all;
    }

    /// <summary>True if a file sits inside a folder. Compared as full paths, so a sibling whose name
    /// merely starts the same way is not mistaken for a child.</summary>
    private static bool IsUnder(string path, string folder)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder)) return false;

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            var how = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return full.StartsWith(root, how);
        }
        catch { return false; }
    }

    private static readonly string[] AssetExtensions = { ".dff", ".txd", ".col", ".ifp" };

    /// <summary>
    /// <paramref name="existsInGame"/> answers "does the game already have a file by this name",
    /// covering both archive entries and unarchived files — that single question is what separates
    /// a replacement from an addition.
    /// </summary>
    /// <param name="modFiles">
    /// The mod folder's listing, walked once by the caller and shared. A reinstall scans the mod
    /// folder a second time — the answer to "is this new" changes when the old copy comes out, even
    /// though the files do not — and SAFT hung on a real device inside that second walk. See GameFiles.
    /// </param>
    public static AdditionPlan Scan(
        string gameRoot,
        string modSourceFolder,
        Func<string, bool> existsInGame,
        GameDensityBaseline? baseline = null,
        IReadOnlySet<int>? usedObjectIds = null,
        Action<string>? onStep = null,
        GameFiles? modFiles = null,
        IReadOnlyList<IdeDefinition>? gameDefinitions = null)
    {
        onStep?.Invoke("additions: reading the mod folder");
        var definitions = new List<IdeDefinition>();
        var placements = new List<IplInstance>();
        var newAssets = new List<NewAsset>();
        var assetCandidates = new List<NewAsset>();

        var listing = GameFiles.For(modSourceFolder, modFiles, onStep);

        foreach (var path in listing.Paths)
        {
            var fileName = Path.GetFileName(path);
            if (FileFilters.IsIgnoredFile(fileName)) continue;

            // An .ide or .ipl the game ALREADY HAS is a replacement, not an addition manifest, and
            // its contents describe the game rather than anything being added to it. Reading one as
            // additions is wrong twice over: it reports objects the game has had all along as new,
            // and it feeds them into the density maths as extra weight the mod does not add.
            //
            // A mod replacing data/peds.ide was read as adding 276 new pedestrians. A mod replacing
            // cull.ipl had every cull zone in it counted as newly placed. Both are files the user is
            // swapping wholesale — whatever rows they contain arrive as part of the replacement and
            // are not SAFT's to install, allocate IDs for, or weigh.
            var isReplacementOfAGameFile = existsInGame(fileName);

            if (fileName.EndsWith(".ide", StringComparison.OrdinalIgnoreCase))
            {
                if (!isReplacementOfAGameFile)
                    definitions.AddRange(SafeParse(() => IdeFile.Parse(path), Array.Empty<IdeDefinition>()));
                continue;
            }

            if (fileName.EndsWith(".ipl", StringComparison.OrdinalIgnoreCase))
            {
                if (!isReplacementOfAGameFile)
                    placements.AddRange(SafeParse(() => IplFile.Parse(path), Array.Empty<IplInstance>()));
                continue;
            }

            if (!AssetExtensions.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;

            assetCandidates.Add(new NewAsset(fileName, path));
        }

        // Classified only now that every definition has been read, because "is this the game's file
        // or the mod's own?" cannot be answered from the file name alone.
        //
        // An asset the mod DEFINES in its own .ide is the mod's addition, and stays the mod's
        // addition even when the archive already holds something by that name. What puts it there is
        // an earlier round of this same mod that did not come all the way out - and the moment SAFT
        // reads that leftover as a stock file, it files a copy of the MOD in the backup folder as
        // though it were the original, records no archive entries for the mod, and every uninstall
        // after that faithfully restores the mod into the game. One missed removal became permanent.
        //
        // Going by the mod's own definitions needs no record and no memory of what happened before,
        // so a game that is already in that state heals on the next install rather than staying stuck.
        var definedByThisMod = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            if (!string.IsNullOrWhiteSpace(definition.ModelName))
            {
                definedByThisMod.Add(definition.ModelName + ".dff");
                definedByThisMod.Add(definition.ModelName + ".col");
            }

            if (!string.IsNullOrWhiteSpace(definition.TextureName))
                definedByThisMod.Add(definition.TextureName + ".txd");
        }

        // Who already DEFINES this object decides who it belongs to.
        //
        // A file sitting in the archive says nothing about whose it is. A definition does, and there
        // are only three cases. Defined in somebody else's .ide: the user's object, whether it
        // shipped with the game or they added it by hand last week - SAFT replaces the file, keeps a
        // backup, and writes no map data, because it does not edit map files it did not create.
        // Defined in saft_additions.ide, or defined nowhere at all: SAFT's, either on the record or
        // orphaned by an uninstall that did not finish, and either way this mod's to install.
        //
        // The orphan case is the one that made this necessary. Asking "does a file by this name
        // exist" cannot tell an orphan apart from the user's own castle, and getting that wrong in
        // either direction is destructive: treat the user's castle as SAFT's and their model is
        // overwritten with no backup and deleted on the next uninstall; treat an orphan as the
        // user's and a copy of the MOD gets filed as a vanilla original.
        var ownedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ownedAssetFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var saftsOwnFile = Path.Combine("maps", AdditionInstaller.SaftMapFolder, AdditionInstaller.SaftIdeFileName);
        var gameDefs = gameDefinitions ?? ReadGameDefinitions(gameRoot, modSourceFolder, onStep);
        foreach (var definition in gameDefs)
        {
            if (definition.SourcePath.Replace('\\', '/').EndsWith(saftsOwnFile.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                continue;

            // A definition inside the mod folder is the MOD's, whatever it is nested in. People keep
            // their mod folders inside the game folder, and a walk of the game that wanders into one
            // would read the pack's own .ide as the game's - concluding the game already defines
            // these objects and quietly installing none of them.
            if (IsUnder(definition.SourcePath, modSourceFolder)) continue;

            if (!string.IsNullOrWhiteSpace(definition.ModelName))
            {
                ownedModels.Add(definition.ModelName);
                ownedAssetFiles.Add(definition.ModelName + ".dff");
                ownedAssetFiles.Add(definition.ModelName + ".col");
            }

            if (!string.IsNullOrWhiteSpace(definition.TextureName))
                ownedAssetFiles.Add(definition.TextureName + ".txd");
        }

        foreach (var candidate in assetCandidates)
        {
            if (ownedAssetFiles.Contains(candidate.FileName)) continue;
            if (!existsInGame(candidate.FileName) || definedByThisMod.Contains(candidate.FileName))
                newAssets.Add(candidate);
        }

        // A model the game already defines keeps the definition and the placement it already has.
        // Adding a second of either is how one object becomes two standing next to each other, on
        // two different IDs, with the mod's copy pointing at a model the user's line also claims.
        var supersededDefinitions = definitions.RemoveAll(d => ownedModels.Contains(d.ModelName));
        var supersededPlacements = placements.RemoveAll(p => ownedModels.Contains(p.ModelName));
        if (supersededDefinitions > 0 || supersededPlacements > 0)
            onStep?.Invoke(
                $"additions: {supersededDefinitions} definition(s) and {supersededPlacements} placement(s) " +
                "left alone - the game already defines those objects, so they are replacements, not additions");

        // Collision is matched to a model BY THE RECORD NAME INSIDE THE BUNDLE, never by the .col
        // file's own name — one bundle called anything at all can carry records for many models. An
        // earlier version compared file names to model names and so believed collision was missing
        // whenever a mod packaged it sensibly.
        var collision = new List<ColRecord>();
        foreach (var asset in newAssets.Where(a => a.FileName.EndsWith(".col", StringComparison.OrdinalIgnoreCase)))
            collision.AddRange(SafeParse(() => ColBundle.ReadFile(asset.SourcePath), Array.Empty<ColRecord>()));

        // A record only has to EXIST. Its geometry may legitimately be empty: a record with no
        // spheres, boxes or faces gives a walk-through object, which is how San Andreas itself ships
        // things like plc_stinger and how a mod author deliberately makes scenery you can pass
        // through. Tested both ways in game — deleting the record crashes on world load, emptying it
        // does not. An earlier version refused empty records and would have blocked that technique.
        var suppliedCollision = new HashSet<string>(
            collision.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);

        // Only PLACED models need collision — see AdditionPlan.PlacesModelsWithoutCollision. A model
        // the mod defines but never places costs a slot and nothing else.
        var placedModels = new HashSet<string>(
            placements.Select(p => p.ModelName), StringComparer.OrdinalIgnoreCase);

        var modelsWithoutCollision = definitions
            .Select(d => d.ModelName)
            .Where(m => placedModels.Contains(m) && !suppliedCollision.Contains(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Reported, never blocked: an empty record is a deliberate choice as often as a mistake, and
        // the user is the one who knows which. Worth saying out loud so a mod author who emptied a
        // record by accident finds out before wondering why their wall isn't solid.
        var emptyCollision = new HashSet<string>(
            collision.Where(r => !ColBundle.HasGeometry(r)).Select(r => r.Name),
            StringComparer.OrdinalIgnoreCase);

        var walkThroughModels = definitions
            .Select(d => d.ModelName)
            .Where(m => placedModels.Contains(m) && emptyCollision.Contains(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();

        onStep?.Invoke($"additions: {newAssets.Count} new asset(s), {definitions.Count} definition(s), {placements.Count} placement(s); working out free object slots");
        var used = usedObjectIds ?? ObjectIdAllocator.ScanUsedIds(gameRoot);
        var slotsAvailable = ObjectIdAllocator.Describe(used).TotalFree;

        onStep?.Invoke($"additions: {slotsAvailable:N0} slot(s) free; measuring the game baseline");
        var resolvedBaseline = baseline ?? PlacementDensity.MeasureGameBaseline(gameRoot);

        onStep?.Invoke("additions: weighing the mod's own files");
        var weights = PlacementDensity.WeighModFiles(definitions, modSourceFolder, listing);
        var density = PlacementDensity.Analyse(placements, resolvedBaseline, weights);

        onStep?.Invoke("additions: scan complete");
        return new AdditionPlan(
            definitions, placements, newAssets, modelsWithoutCollision, slotsAvailable, density,
            collision, walkThroughModels);
    }

    /// <summary>
    /// A malformed .ide/.ipl in a mod pack must not abort the scan — the rest of the mod is usually
    /// perfectly installable, and the user is better served by a report than an exception.
    /// </summary>
    private static IReadOnlyList<T> SafeParse<T>(Func<IReadOnlyList<T>> parse, IReadOnlyList<T> fallback)
    {
        try { return parse(); }
        catch { return fallback; }
    }
}
