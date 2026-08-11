using System.Runtime.CompilerServices;

namespace SAFT.Core;

/// <summary>
/// What one model costs to stream in: its own geometry, plus the texture dictionary it uses. The
/// texture is tracked by name rather than folded into the total because a single .txd is frequently
/// shared by dozens of models — counting it per object would inflate an area's real weight
/// enormously.
/// </summary>
public sealed record ModelWeight(string ModelName, string TextureName, long ModelBytes, long TextureBytes);

/// <summary>
/// The busiest patch of world a set of placements produces, by both measures that matter.
/// <see cref="ObjectCount"/> is what pressures the engine's fixed-size pools;
/// <see cref="Bytes"/> is what pressures the streaming memory budget in stream.ini. They are
/// separate ceilings — a few very heavy objects and very many light ones fail in different ways —
/// so both are reported rather than combined into one score.
/// </summary>
public sealed record DensestArea(double CentreX, double CentreY, int ObjectCount, long Bytes);

/// <summary>The heaviest area the game itself ships, used as the yardstick a mod is measured against.</summary>
public sealed record GameDensityBaseline(int BusiestObjectCount, long BusiestBytes);

/// <summary>
/// How concentrated a mod's placements are, next to how concentrated the game's own map is.
/// The baseline is measured from the player's actual game rather than being a number SAFT made up.
/// </summary>
public sealed record PlacementDensityReport(
    int TotalPlacements,
    DensestArea? Densest,
    GameDensityBaseline Baseline)
{
    /// <summary>
    /// True when a mod packs more objects into one area than the densest area Rockstar themselves
    /// built. Not a prediction of failure — just the point past which the mod is asking the engine
    /// to do something it was never shipped doing, which is the honest thing to warn about.
    /// </summary>
    public bool ExceedsGameOwnDensity =>
        Densest is not null && Baseline.BusiestObjectCount > 0 && Densest.ObjectCount > Baseline.BusiestObjectCount;

    /// <summary>
    /// The other ceiling: more bytes in one area than the game's own heaviest. A handful of very
    /// detailed objects can breach this while the object count stays trivially low, which is exactly
    /// why the two are judged separately rather than blended into one score.
    /// </summary>
    public bool ExceedsGameOwnWeight =>
        Densest is not null && Baseline.BusiestBytes > 0 && Densest.Bytes > Baseline.BusiestBytes;

    /// <summary>
    /// Whether this mod stays inside what the player's own game demonstrably already does. A mod
    /// that does isn't guaranteed to run well — engine pool usage can't be measured from files —
    /// but it isn't asking for anything the game doesn't already handle somewhere.
    /// </summary>
    public bool WithinGameProvenRange => !ExceedsGameOwnDensity && !ExceedsGameOwnWeight;
}

/// <summary>
/// Measures how tightly clustered added objects are.
///
/// San Andreas streams: objects only occupy memory while the player is near them, so the risk from
/// adding content isn't the total count, it's how much lands in one place. The engine's fixed-size
/// pools can't be measured from the files — pool usage depends on what's loaded as the player moves,
/// which is a runtime property — but clustering CAN be measured, and it's the proxy that actually
/// correlates with the risk. So SAFT reports something true instead of guessing at a threshold.
/// </summary>
public static class PlacementDensity
{
    /// <summary>
    /// Grid size for "one area". 200 game units is roughly a couple of city blocks — small enough
    /// that a cell's contents are plausibly loaded together, large enough not to be noise.
    /// </summary>
    public const double AreaSizeUnits = 200.0;

    private static readonly Dictionary<string, long> EmptyTextureBytes = new();

    /// <summary>
    /// Size of each distinct texture dictionary, derived once per set of weights and remembered
    /// against it.
    ///
    /// The caching is not a micro-optimisation, it is the difference between the app surviving and
    /// not. <see cref="FindDensest"/> is called once per map CELL by the whole-map callers, and this
    /// table used to be rebuilt inside every one of those calls: roughly 3,000 cells x 16,000 model
    /// weights, so tens of millions of case-insensitive string hashes and a fresh multi-megabyte
    /// dictionary thrown away each time. On a desktop that was slow. In a 32-bit process on a phone
    /// it churned gigabytes through a small address space and the process was killed outright partway
    /// through — no exception, no crash log, and only sometimes, because whether it survived came
    /// down to GC timing.
    ///
    /// Keyed on the weights instance itself and held weakly, so a caller measuring "before" and
    /// "after" gets one table each and neither is kept alive past its use. The weights dictionaries
    /// this is handed are built and then only read; a caller that mutated one after measuring would
    /// see a stale table.
    /// </summary>
    private static readonly ConditionalWeakTable<object, Dictionary<string, long>> TextureBytesCache = new();

    private static Dictionary<string, long> TextureBytesFor(IReadOnlyDictionary<string, ModelWeight> weights) =>
        TextureBytesCache.GetValue(weights, static key =>
        {
            var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var weight in ((IReadOnlyDictionary<string, ModelWeight>)key).Values)
            {
                if (string.IsNullOrEmpty(weight.TextureName)) continue;
                map.TryAdd(weight.TextureName, weight.TextureBytes);
            }
            return map;
        });

    private static (long, long) CellKey(IplInstance p) =>
        ((long)Math.Floor(p.X / AreaSizeUnits), (long)Math.Floor(p.Y / AreaSizeUnits));

    /// <summary>
    /// The heaviest single cell in bytes, over the whole map, in ONE pass.
    ///
    /// This replaces "group the placements into cells, then ask FindDensest about each cell in turn".
    /// That read reasonably but cost enormously: the map is 869 cells, so it meant 869 calls, each
    /// allocating three dictionaries and a set of HashSets and running a LINQ sort over a collection
    /// of one, and it happened four times per install. Measured at 200 MB of allocation churn per
    /// analysis on a 32-bit process with a 2 GB address space and a Large Object Heap that is never
    /// compacted - which is how an app holding 30 MB live can still fail to find room, intermittently,
    /// depending only on how the previous run happened to leave the heap.
    ///
    /// Same arithmetic as <see cref="FindDensest"/>'s byte total, deliberately: distinct models per
    /// cell plus distinct texture dictionaries per cell, each counted once however many placements
    /// share them. Cells holding nothing that is in <paramref name="weights"/> total zero and so
    /// cannot win, which is why they are never added.
    /// </summary>
    public static long HeaviestCellBytes(
        IEnumerable<IplInstance> placements, IReadOnlyDictionary<string, ModelWeight>? weights)
    {
        if (weights is null) return 0;

        var modelsPerCell = new Dictionary<(long, long), HashSet<string>>();
        var texturesPerCell = new Dictionary<(long, long), HashSet<string>>();

        foreach (var p in placements)
        {
            if (!weights.TryGetValue(p.ModelName, out var weight)) continue;

            var key = CellKey(p);

            if (!modelsPerCell.TryGetValue(key, out var models))
                modelsPerCell[key] = models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            models.Add(weight.ModelName);

            if (string.IsNullOrEmpty(weight.TextureName)) continue;

            if (!texturesPerCell.TryGetValue(key, out var textures))
                texturesPerCell[key] = textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            textures.Add(weight.TextureName);
        }

        var textureBytes = TextureBytesFor(weights);
        long heaviest = 0;

        foreach (var (key, models) in modelsPerCell)
        {
            long total = 0;
            foreach (var name in models)
                if (weights.TryGetValue(name, out var w)) total += w.ModelBytes;

            if (texturesPerCell.TryGetValue(key, out var textures))
                foreach (var name in textures)
                    if (textureBytes.TryGetValue(name, out var bytes)) total += bytes;

            if (total > heaviest) heaviest = total;
        }

        return heaviest;
    }

    /// <summary>
    /// Busiest cell produced by a set of placements, or null if there are none. Pass
    /// <paramref name="weights"/> to also total the bytes an area would have to stream; without it
    /// the byte figure is simply zero and only the object count is meaningful.
    /// </summary>
    public static DensestArea? FindDensest(
        IEnumerable<IplInstance> placements, IReadOnlyDictionary<string, ModelWeight>? weights = null)
    {
        var counts = new Dictionary<(long, long), int>();
        var modelsPerCell = new Dictionary<(long, long), HashSet<string>>(); // distinct: shared assets counted once
        var texturesPerCell = new Dictionary<(long, long), HashSet<string>>();

        foreach (var p in placements)
        {
            var key = ((long)Math.Floor(p.X / AreaSizeUnits), (long)Math.Floor(p.Y / AreaSizeUnits));
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;

            if (weights is null || !weights.TryGetValue(p.ModelName, out var weight)) continue;

            if (!modelsPerCell.TryGetValue(key, out var models))
                modelsPerCell[key] = models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            models.Add(weight.ModelName);

            if (!texturesPerCell.TryGetValue(key, out var textures))
                texturesPerCell[key] = textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(weight.TextureName)) textures.Add(weight.TextureName);
        }

        if (counts.Count == 0) return null;

        var textureBytes = weights is null ? EmptyTextureBytes : TextureBytesFor(weights);

        long BytesIn((long, long) key)
        {
            long total = 0;
            if (weights is null) return total;

            if (modelsPerCell.TryGetValue(key, out var models))
                foreach (var name in models)
                    if (weights.TryGetValue(name, out var w)) total += w.ModelBytes;

            if (texturesPerCell.TryGetValue(key, out var textures))
            {
                // One entry per distinct texture dictionary, whichever model introduced it.
                foreach (var name in textures)
                    if (textureBytes.TryGetValue(name, out var bytes)) total += bytes;
            }

            return total;
        }

        var busiest = counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key).First();
        return new DensestArea(
            busiest.Key.Item1 * AreaSizeUnits + AreaSizeUnits / 2,
            busiest.Key.Item2 * AreaSizeUnits + AreaSizeUnits / 2,
            busiest.Value,
            BytesIn(busiest.Key));
    }

    /// <summary>
    /// The heaviest area in the player's own game, used as the comparison for both ceilings. Reads
    /// the plain-text .ipl files under the game folder; the binary IPLs inside gta3.img hold more of
    /// the map still, so this UNDERSTATES the real figure — which is the safe direction, since it
    /// makes SAFT warn slightly early rather than slightly late.
    /// </summary>
    public static GameDensityBaseline MeasureGameBaseline(string gameRoot)
    {
        var placements = new List<IplInstance>();
        foreach (var path in IplFile.FindAll(gameRoot))
        {
            try
            {
                placements.AddRange(IplFile.Parse(path));
            }
            catch
            {
                // One unreadable map file must not stop the comparison; a slightly low baseline is
                // better than no report at all.
            }
        }

        // The binary .ipl files inside the archives hold about four fifths of the map. Reading only
        // the text ones put the busiest area in a stock game at 171 objects when it is really 330,
        // so every mod was being compared against half a world.
        var definitions = ReadDefinitions(gameRoot);
        var namesById = ModelNamesById(definitions);
        placements.AddRange(BinaryIplFile.ReadAllFromGame(
            gameRoot, id => namesById.TryGetValue(id, out var name) ? name : string.Empty));

        var weights = WeighGameAssets(gameRoot, definitions);
        var densestByCount = FindDensest(placements, weights);

        // The area with the most objects isn't necessarily the heaviest one, so the byte ceiling is
        // measured over its own busiest cell rather than borrowed from the count.
        var heaviestBytes = HeaviestAreaBytes(placements, weights);

        return new GameDensityBaseline(densestByCount?.ObjectCount ?? 0, heaviestBytes);
    }

    /// <summary>
    /// Every object definition in the game, read once.
    ///
    /// Parsing the .ide files is the single most expensive step in measuring a game — more than
    /// reading the archives or the map — and one install used to do it five times over: twice here
    /// and three times inside <see cref="StreamingImpact"/>. That is unnoticeable on a desktop and
    /// most of the wait on an SD card, so the parsed list is passed around instead of the folder path.
    /// </summary>
    public static List<IdeDefinition> ReadDefinitions(string gameRoot)
    {
        var definitions = new List<IdeDefinition>();

        foreach (var path in IdeFile.FindAll(gameRoot))
        {
            try
            {
                definitions.AddRange(IdeFile.Parse(path));
            }
            catch
            {
                // Same reasoning as the placements above: a partial map beats no map.
            }
        }

        return definitions;
    }

    /// <summary>
    /// Object id to model name. Binary .ipl placements reference an object only by id, so this is
    /// what lets them be weighed alongside the text ones.
    /// </summary>
    public static Dictionary<int, string> ModelNamesById(IReadOnlyList<IdeDefinition> definitions)
    {
        var names = new Dictionary<int, string>();
        foreach (var definition in definitions) names[definition.ObjectId] = definition.ModelName;
        return names;
    }

    private static long HeaviestAreaBytes(
        IReadOnlyList<IplInstance> placements, IReadOnlyDictionary<string, ModelWeight> weights) =>
        HeaviestCellBytes(placements, weights);

    /// <summary>
    /// What every object already in the game costs to stream, taken from the real sizes of its
    /// .dff/.txd wherever they live — inside an archive or loose in the game folder.
    /// </summary>
    public static Dictionary<string, ModelWeight> WeighGameAssets(string gameRoot) =>
        WeighGameAssets(gameRoot, ReadDefinitions(gameRoot));

    /// <summary>
    /// As above, for a caller that has already read the definitions and shouldn't pay for them twice.
    /// </summary>
    public static Dictionary<string, ModelWeight> WeighGameAssets(
        string gameRoot, IReadOnlyList<IdeDefinition> definitions)
    {
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var found in GameScanner.FindArchives(gameRoot))
        {
            try
            {
                using var archive = ImgArchive.Open(found.AbsolutePath);
                foreach (var entry in archive.Entries)
                    sizes[entry.Name] = entry.SizeSectors * (long)ImgEntry.SectorSize;
            }
            catch
            {
                // An unreadable archive just leaves those assets unweighed.
            }
        }

        foreach (var path in Directory.EnumerateFiles(gameRoot, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            if (!name.EndsWith(".dff", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".txd", StringComparison.OrdinalIgnoreCase)) continue;
            if (!sizes.ContainsKey(name)) sizes[name] = new FileInfo(path).Length;
        }

        long SizeOf(string fileName) => sizes.TryGetValue(fileName, out var n) ? n : 0;

        var weights = new Dictionary<string, ModelWeight>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            weights[definition.ModelName] = new ModelWeight(
                definition.ModelName,
                definition.TextureName,
                SizeOf(definition.ModelName + ".dff"),
                SizeOf(definition.TextureName + ".txd"));
        }
        return weights;
    }

    public static PlacementDensityReport Analyse(
        IEnumerable<IplInstance> additions, GameDensityBaseline baseline, IReadOnlyDictionary<string, ModelWeight>? weights = null)
    {
        var list = additions.ToList();
        return new PlacementDensityReport(list.Count, FindDensest(list, weights), baseline);
    }

    /// <summary>
    /// Weighs a mod's own new assets from the files it ships — the model's .dff and the .txd its
    /// .ide line points at. Only what the mod actually supplies is measured; anything reusing an
    /// existing game texture costs nothing extra to stream, since the game was already loading it.
    /// </summary>
    public static Dictionary<string, ModelWeight> WeighModFiles(
        IEnumerable<IdeDefinition> definitions, string modSourceFolder)
    {
        var filesByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(modSourceFolder, "*", SearchOption.AllDirectories))
            filesByName[Path.GetFileName(path)] = path;

        long SizeOf(string fileName) =>
            filesByName.TryGetValue(fileName, out var path) ? new FileInfo(path).Length : 0;

        var weights = new Dictionary<string, ModelWeight>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            weights[definition.ModelName] = new ModelWeight(
                definition.ModelName,
                definition.TextureName,
                SizeOf(definition.ModelName + ".dff"),
                SizeOf(definition.TextureName + ".txd"));
        }
        return weights;
    }
}
