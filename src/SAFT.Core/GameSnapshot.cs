namespace SAFT.Core;

/// <summary>
/// Everything SAFT needs to know about the game's own map, read exactly once.
///
/// Before this existed, an install read the whole map TWICE. <see cref="AdditionScanner.Scan"/> called
/// <see cref="PlacementDensity.MeasureGameBaseline"/>, which parsed all 60 .ide files, all 54 text
/// .ipl files and every binary .ipl inside the archives; then <see cref="StreamingImpact.Measure"/>
/// turned around and did the identical work again. Neither knew the other existed. Measured at 81 MB
/// and 84 MB of allocation for the same answer.
///
/// That mattered far more than it sounds. SAFT is a 32-bit process, so it has a 2 GB address space
/// (4 GB now the exe is large-address-aware), and the Large Object Heap it allocates these arrays on
/// is never compacted. The failure was never "out of memory" - live heap peaks around 40 MB - it was
/// the allocator unable to find a CONTIGUOUS block among the holes left by the previous pass. Which
/// is why it was intermittent, why it only ever hit the scanning phase and never the file-writing
/// phase, and why it never once reproduced on a 64-bit machine.
///
/// Reading once is the fix for half of it. The other half is that the reading itself is expensive,
/// because "scan the game" here does not mean skim some bytes - it means building an object graph of
/// the entire San Andreas map: 14,839 definitions, 14,823 weighted models and 50,982 placements.
/// </summary>
public sealed class GameSnapshot
{
    private GameSnapshot(
        IReadOnlyList<IdeDefinition> definitions,
        Dictionary<string, ModelWeight> weights,
        IReadOnlyList<IplInstance> placements,
        GameDensityBaseline baseline)
    {
        Definitions = definitions;
        Weights = weights;
        Placements = placements;
        Baseline = baseline;
    }

    /// <summary>Every ID-bearing definition line across the game's .ide files.</summary>
    public IReadOnlyList<IdeDefinition> Definitions { get; }

    /// <summary>What each model costs to stream, as the game currently stands.</summary>
    public Dictionary<string, ModelWeight> Weights { get; }

    /// <summary>Every placement on the map, from the text .ipl files and the binary ones in the archives.</summary>
    public IReadOnlyList<IplInstance> Placements { get; }

    /// <summary>The busiest and heaviest area the game itself ships — the yardstick a mod is judged against.</summary>
    public GameDensityBaseline Baseline { get; }

    /// <summary>
    /// Reads the game once. <paramref name="onStep"/> receives a breadcrumb before each phase; this
    /// is several seconds of the heaviest work SAFT does, and when a process is killed part way
    /// through it, that trail is the only way to know where.
    /// </summary>
    public static GameSnapshot Read(string gameRoot, Action<string>? onStep = null)
    {
        onStep?.Invoke("map: reading definitions");
        var definitions = PlacementDensity.ReadDefinitions(gameRoot);

        onStep?.Invoke($"map: {definitions.Count:N0} definition(s); weighing game assets");
        var weights = PlacementDensity.WeighGameAssets(gameRoot, definitions);

        // Per file, not per loop. Two runs have now stopped dead somewhere inside this loop, and a
        // single breadcrumb around the whole thing cannot say whether the search stalled, or one
        // particular file did, or it was simply grinding. Restored after a refactor dropped it.
        onStep?.Invoke($"map: {weights.Count:N0} weighted model(s); searching for text .ipl files");
        var iplPaths = IplFile.FindAll(gameRoot);

        onStep?.Invoke($"map: found {iplPaths.Count} text .ipl file(s); parsing them");
        var placements = new List<IplInstance>();
        for (var i = 0; i < iplPaths.Count; i++)
        {
            onStep?.Invoke($"map: ipl {i + 1}/{iplPaths.Count} {Path.GetFileName(iplPaths[i])}");
            try { placements.AddRange(IplFile.Parse(iplPaths[i])); }
            catch { /* one unreadable map file must not stop the rest */ }
        }

        // The binary .ipl files inside the archives carry about four fifths of the map. Leaving them
        // out put "the heaviest area" at 8.6 MB while the baseline, which does read them, called the
        // same game 32.0 MB — two different answers to one question in a single popup.
        onStep?.Invoke($"map: {placements.Count:N0} text placement(s); reading binary .ipl from archives");
        var namesById = PlacementDensity.ModelNamesById(definitions);
        placements.AddRange(BinaryIplFile.ReadAllFromGame(
            gameRoot, id => namesById.TryGetValue(id, out var name) ? name : string.Empty));

        onStep?.Invoke($"map: {placements.Count:N0} placement(s) total; finding the busiest area");
        var densest = PlacementDensity.FindDensest(placements, weights);

        onStep?.Invoke("map: finding the heaviest area");
        var heaviest = PlacementDensity.HeaviestCellBytes(placements, weights);

        onStep?.Invoke($"map: read complete — busiest {densest?.ObjectCount ?? 0:N0} objects, heaviest {heaviest / 1048576.0:N1} MB");
        return new GameSnapshot(
            definitions, weights, placements,
            new GameDensityBaseline(densest?.ObjectCount ?? 0, heaviest));
    }
}
