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
    private static readonly string[] AssetExtensions = { ".dff", ".txd", ".col", ".ifp" };

    /// <summary>
    /// <paramref name="existsInGame"/> answers "does the game already have a file by this name",
    /// covering both archive entries and unarchived files — that single question is what separates
    /// a replacement from an addition.
    /// </summary>
    public static AdditionPlan Scan(
        string gameRoot,
        string modSourceFolder,
        Func<string, bool> existsInGame,
        GameDensityBaseline? baseline = null,
        IReadOnlySet<int>? usedObjectIds = null)
    {
        var definitions = new List<IdeDefinition>();
        var placements = new List<IplInstance>();
        var newAssets = new List<NewAsset>();

        foreach (var path in Directory.EnumerateFiles(modSourceFolder, "*", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(path);
            if (FileFilters.IsIgnoredFile(fileName)) continue;

            if (fileName.EndsWith(".ide", StringComparison.OrdinalIgnoreCase))
            {
                definitions.AddRange(SafeParse(() => IdeFile.Parse(path), Array.Empty<IdeDefinition>()));
                continue;
            }

            if (fileName.EndsWith(".ipl", StringComparison.OrdinalIgnoreCase))
            {
                placements.AddRange(SafeParse(() => IplFile.Parse(path), Array.Empty<IplInstance>()));
                continue;
            }

            if (!AssetExtensions.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;

            // The whole classification hinges on this one question.
            if (!existsInGame(fileName)) newAssets.Add(new NewAsset(fileName, path));
        }

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

        var used = usedObjectIds ?? ObjectIdAllocator.ScanUsedIds(gameRoot);
        var slotsAvailable = ObjectIdAllocator.Describe(used).TotalFree;

        var resolvedBaseline = baseline ?? PlacementDensity.MeasureGameBaseline(gameRoot);
        var weights = PlacementDensity.WeighModFiles(definitions, modSourceFolder);
        var density = PlacementDensity.Analyse(placements, resolvedBaseline, weights);

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
