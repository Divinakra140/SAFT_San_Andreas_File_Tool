namespace SAFT.Core;

/// <summary>
/// What a mod does to the game's streaming load — for REPLACEMENTS as much as additions.
///
/// Replacements never add objects, so they can't pressure the engine's entity pools. What they can
/// do, and routinely do, is make the same objects far heavier: swapping every vanilla car texture
/// for an HD one multiplies what the streamer has to hold. Past the budget in stream.ini the engine
/// starts evicting things, which is why an over-heavy car pack makes distant world geometry stop
/// rendering rather than making cars look wrong.
/// </summary>
public sealed record StreamingImpactReport(
    long HeaviestAreaBefore,
    long HeaviestAreaAfter,
    long DynamicWeightBefore,
    long DynamicWeightAfter,
    int ReplacedPlacedModels,
    int ReplacedDynamicModels)
{
    public bool IncreasesLoad => HeaviestAreaAfter > HeaviestAreaBefore || DynamicWeightAfter > DynamicWeightBefore;

    /// <summary>How much heavier the busiest patch of map becomes, e.g. 2.4 means "2.4x the vanilla load".</summary>
    public double AreaMultiplier => HeaviestAreaBefore <= 0 ? 1 : (double)HeaviestAreaAfter / HeaviestAreaBefore;

    /// <summary>
    /// The same for vehicles, peds and weapons. These aren't placed anywhere — traffic and gameplay
    /// decide when they load — so their risk is total weight rather than concentration, and it
    /// applies everywhere in the game at once.
    /// </summary>
    public double DynamicMultiplier => DynamicWeightBefore <= 0 ? 1 : (double)DynamicWeightAfter / DynamicWeightBefore;
}

public static class StreamingImpact
{
    /// <summary>Sections whose objects are placed on the map, so their cost is felt per area.</summary>
    private static readonly HashSet<string> PlacedSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "objs", "tobj", "anim",
    };

    /// <summary>
    /// Sections whose objects are spawned by the game rather than placed — traffic, pedestrians,
    /// weapons. Location tells you nothing about when they load, so their cost is measured in total.
    /// </summary>
    private static readonly HashSet<string> DynamicSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "cars", "peds", "weap",
    };

    /// <summary>
    /// Compares the game as it is against the game as this mod would leave it.
    /// <paramref name="replacementSizes"/> maps a file name the mod replaces ("banshee.txd") to the
    /// size of the mod's version.
    ///
    /// <paramref name="onStep"/> is a diagnostic breadcrumb sink, called before each phase, for the
    /// same reason <see cref="DirectModInstaller.Plan"/> has one: this is several seconds of the
    /// heaviest work SAFT does behind a single call, and a process killed part way through it left
    /// no way to tell which phase it died in.
    /// </summary>
    public static StreamingImpactReport Measure(
        string gameRoot, IReadOnlyDictionary<string, long> replacementSizes, Action<string>? onStep = null,
        GameSnapshot? snapshot = null)
    {
        // Handed the map when the caller already read it, which is the whole point of GameSnapshot:
        // this method and AdditionScanner used to read the entire game independently, for the same
        // answer, at roughly 85 MB of allocation each. Falls back to reading it itself so the method
        // still stands alone.
        snapshot ??= GameSnapshot.Read(gameRoot, onStep);

        var definitions = snapshot.Definitions;
        var before = snapshot.Weights;
        var cells = snapshot.Cells;
        var after = new Dictionary<string, ModelWeight>(before, StringComparer.OrdinalIgnoreCase);

        var replacedPlaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var replacedDynamic = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in definitions)
        {
            if (!before.TryGetValue(definition.ModelName, out var vanilla)) continue;

            // A .dff replacement affects one model; a .txd replacement affects EVERY model that
            // shares that texture dictionary, which is how a single file can make a whole vehicle
            // class heavier at once.
            var newModelBytes = replacementSizes.TryGetValue(definition.ModelName + ".dff", out var dff)
                ? dff : vanilla.ModelBytes;
            var newTextureBytes = replacementSizes.TryGetValue(definition.TextureName + ".txd", out var txd)
                ? txd : vanilla.TextureBytes;

            if (newModelBytes == vanilla.ModelBytes && newTextureBytes == vanilla.TextureBytes) continue;

            after[definition.ModelName] = vanilla with { ModelBytes = newModelBytes, TextureBytes = newTextureBytes };

            if (DynamicSections.Contains(definition.Section)) replacedDynamic.Add(definition.ModelName);
            else if (PlacedSections.Contains(definition.Section)) replacedPlaced.Add(definition.ModelName);
        }

        // Already known: the snapshot measured the game as it stands. Recomputing it would be a
        // third full pass over 50,982 placements for a number we are holding.
        var heaviestBefore = snapshot.Baseline.BusiestBytes;

        onStep?.Invoke($"measure: heaviest area now {heaviestBefore / 1048576.0:N1} MB; weighing it with the mod applied");
        var heaviestAfter = cells.HeaviestBytes(after);

        onStep?.Invoke($"measure: heaviest area after {heaviestAfter / 1048576.0:N1} MB; totalling dynamic weight");
        return new StreamingImpactReport(
            heaviestBefore,
            heaviestAfter,
            TotalDynamicWeight(definitions, before),
            TotalDynamicWeight(definitions, after),
            replacedPlaced.Count,
            replacedDynamic.Count);
    }

    /// <summary>
    /// Total weight of everything the game spawns rather than places. Each model and each texture
    /// dictionary counts once, however many definitions share them.
    /// </summary>
    private static long TotalDynamicWeight(
        IReadOnlyList<IdeDefinition> definitions, IReadOnlyDictionary<string, ModelWeight> weights)
    {
        var countedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var countedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;

        foreach (var definition in definitions)
        {
            if (!DynamicSections.Contains(definition.Section)) continue;
            if (!weights.TryGetValue(definition.ModelName, out var weight)) continue;

            if (countedModels.Add(weight.ModelName)) total += weight.ModelBytes;
            if (!string.IsNullOrEmpty(weight.TextureName) && countedTextures.Add(weight.TextureName))
                total += weight.TextureBytes;
        }
        return total;
    }
}
