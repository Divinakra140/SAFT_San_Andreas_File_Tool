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
        MapCells cells,
        GameDensityBaseline baseline)
    {
        Definitions = definitions;
        Weights = weights;
        Cells = cells;
        Baseline = baseline;
    }

    /// <summary>Every ID-bearing definition line across the game's .ide files.</summary>
    public IReadOnlyList<IdeDefinition> Definitions { get; }

    /// <summary>What each model costs to stream, as the game currently stands.</summary>
    public Dictionary<string, ModelWeight> Weights { get; }

    /// <summary>
    /// The map as per-cell totals, folded in from the text .ipl files and the binary ones in the
    /// archives. The placements themselves are not kept — see <see cref="MapCells"/> for why holding
    /// all 50,982 of them was the largest single allocation SAFT made.
    /// </summary>
    public MapCells Cells { get; }

    /// <summary>The busiest and heaviest area the game itself ships — the yardstick a mod is judged against.</summary>
    public GameDensityBaseline Baseline { get; }

    /// <summary>
    /// Reads the game once. <paramref name="onStep"/> receives a breadcrumb before each phase; this
    /// is several seconds of the heaviest work SAFT does, and when a process is killed part way
    /// through it, that trail is the only way to know where.
    /// </summary>
    public static GameSnapshot Read(string gameRoot, Action<string>? onStep = null, GameFiles? listing = null)
    {
        // The game folder is listed ONCE here and handed to everything below. Between them, the
        // definition search, the asset weighing, the archive search and the .ipl search used to walk
        // this folder recursively five separate times for a single map read - and two installs have
        // now stopped dead inside one of those walks. See GameFiles.
        var files = GameFiles.For(gameRoot, listing, onStep);

        onStep?.Invoke("map: reading definitions");
        var definitions = PlacementDensity.ReadDefinitions(gameRoot, files);

        onStep?.Invoke($"map: {definitions.Count:N0} definition(s); weighing game assets");
        var weights = PlacementDensity.WeighGameAssets(gameRoot, definitions, onStep, files);

        // Per file, not per loop. Two runs have now stopped dead somewhere inside this loop, and a
        // single breadcrumb around the whole thing cannot say whether the search stalled, or one
        // particular file did, or it was simply grinding. Restored after a refactor dropped it.
        onStep?.Invoke($"map: {weights.Count:N0} weighted model(s); searching for text .ipl files");
        var iplPaths = IplFile.FindAll(gameRoot, files);

        // Folded into per-cell totals one file at a time and then dropped. The previous version built
        // a single list of all 50,982 placements first and reduced it afterwards, which meant the
        // largest object SAFT ever allocated existed only to be collapsed into 869 cells.
        onStep?.Invoke($"map: found {iplPaths.Count} text .ipl file(s); parsing them");
        var cells = new MapCells();
        for (var i = 0; i < iplPaths.Count; i++)
        {
            onStep?.Invoke($"map: ipl {i + 1}/{iplPaths.Count} {Path.GetFileName(iplPaths[i])}");
            try { cells.AddRange(IplFile.Parse(iplPaths[i]), weights); }
            catch { /* one unreadable map file must not stop the rest */ }
        }

        // The binary .ipl files inside the archives carry about four fifths of the map. Leaving them
        // out put "the heaviest area" at 8.6 MB while the baseline, which does read them, called the
        // same game 32.0 MB — two different answers to one question in a single popup.
        onStep?.Invoke($"map: {cells.TotalPlacements:N0} text placement(s); reading binary .ipl from archives");
        var namesById = PlacementDensity.ModelNamesById(definitions);
        BinaryIplFile.ReadAllFromGame(
            gameRoot, files,
            id => namesById.TryGetValue(id, out var name) ? name : string.Empty,
            instance => cells.Add(instance, weights));

        onStep?.Invoke($"map: {cells.TotalPlacements:N0} placement(s) total in {cells.Count:N0} cell(s); finding the busiest area");
        var densest = cells.Densest(weights);

        onStep?.Invoke("map: finding the heaviest area");
        var heaviest = cells.HeaviestBytes(weights);

        onStep?.Invoke($"map: read complete — busiest {densest?.ObjectCount ?? 0:N0} objects, heaviest {heaviest / 1048576.0:N1} MB");
        return new GameSnapshot(
            definitions, weights, cells,
            new GameDensityBaseline(densest?.ObjectCount ?? 0, heaviest));
    }
}
