using Xunit;

namespace SAFT.Core.Tests;

/// <summary>
/// MapCells replaced holding the whole map as a list of placements. It is only a safe replacement if
/// it answers the two questions anyone ever asked that list — busiest area and heaviest area — with
/// exactly the same numbers, so that equivalence is what these assert.
/// </summary>
public class MapCellsTests
{
    private static IplInstance At(string modelName, double x, double y) =>
        new(0, modelName, 0, x, y, 0, string.Empty, 1);

    private static Dictionary<string, ModelWeight> Weights(params (string Model, string Texture, long ModelBytes, long TextureBytes)[] rows)
    {
        var map = new Dictionary<string, ModelWeight>(StringComparer.OrdinalIgnoreCase);
        foreach (var (model, texture, modelBytes, textureBytes) in rows)
            map[model] = new ModelWeight(model, texture, modelBytes, textureBytes);
        return map;
    }

    [Fact]
    public void CountsEveryPlacementButWeighsEachModelOnce()
    {
        var weights = Weights(("wall", "walltex", 1000, 500));
        var cells = new MapCells();

        // Same model placed three times in one cell: three objects, one model, one texture.
        cells.AddRange(new[] { At("wall", 10, 10), At("wall", 20, 20), At("wall", 30, 30) }, weights);

        Assert.Equal(3, cells.TotalPlacements);
        Assert.Equal(1, cells.Count);
        Assert.Equal(1500, cells.HeaviestBytes(weights));

        var densest = cells.Densest(weights);
        Assert.NotNull(densest);
        Assert.Equal(3, densest!.ObjectCount);
        Assert.Equal(1500, densest.Bytes);
    }

    [Fact]
    public void SharedTextureDictionariesAreCountedOncePerCell()
    {
        var weights = Weights(
            ("a", "shared", 100, 700),
            ("b", "shared", 200, 700));

        var cells = new MapCells();
        cells.AddRange(new[] { At("a", 10, 10), At("b", 20, 20) }, weights);

        // 100 + 200 models, plus the shared 700 texture ONCE — not twice.
        Assert.Equal(1000, cells.HeaviestBytes(weights));
    }

    [Fact]
    public void SeparatesPlacementsIntoTheirOwn200mCells()
    {
        var weights = Weights(("a", "t", 100, 0), ("b", "t", 900, 0));
        var cells = new MapCells();

        cells.AddRange(new[] { At("a", 10, 10), At("b", 500, 500) }, weights);

        Assert.Equal(2, cells.Count);
        Assert.Equal(900, cells.HeaviestBytes(weights)); // heaviest CELL, not the total
    }

    [Fact]
    public void AnUnweightedModelStillCountsAsAnObject()
    {
        // Matches the list-based behaviour exactly: the object count included placements whose model
        // had no weight, while the byte total did not.
        var weights = Weights(("known", "t", 100, 0));
        var cells = new MapCells();
        cells.AddRange(new[] { At("known", 10, 10), At("mystery", 20, 20) }, weights);

        Assert.Equal(2, cells.Densest(weights)!.ObjectCount);
        Assert.Equal(100, cells.HeaviestBytes(weights));
    }

    [Fact]
    public void AnEmptyMapHasNoBusiestArea()
    {
        var cells = new MapCells();
        Assert.Null(cells.Densest(Weights()));
        Assert.Equal(0, cells.HeaviestBytes(Weights()));
    }

    [Fact]
    public void ReweighingTheSameCellsGivesTheModifiedTotal()
    {
        // The reason cells are kept rather than a precomputed total: the same map is weighed twice,
        // once as it stands and once with a mod's replacement sizes.
        var before = Weights(("wall", "walltex", 1000, 500));
        var after = Weights(("wall", "walltex", 4000, 500));

        var cells = new MapCells();
        cells.AddRange(new[] { At("wall", 10, 10) }, before);

        Assert.Equal(1500, cells.HeaviestBytes(before));
        Assert.Equal(4500, cells.HeaviestBytes(after));
    }

    /// <summary>
    /// The equivalence that justifies the change, against a real install: fold the whole map into
    /// cells and require the same busiest/heaviest answers the list-based code produced.
    /// Opt-in via SAFT_REAL_GAME.
    /// </summary>
    [Fact]
    public void MatchesTheListBasedAnswersOnARealGame()
    {
        var gameRoot = Environment.GetEnvironmentVariable("SAFT_REAL_GAME");
        if (string.IsNullOrWhiteSpace(gameRoot)) return;
        Assert.True(Directory.Exists(gameRoot), $"SAFT_REAL_GAME set but not found: {gameRoot}");

        var definitions = PlacementDensity.ReadDefinitions(gameRoot);
        var weights = PlacementDensity.WeighGameAssets(gameRoot, definitions);
        var namesById = PlacementDensity.ModelNamesById(definitions);

        // The old way: every placement in one list.
        var placements = new List<IplInstance>();
        foreach (var path in IplFile.FindAll(gameRoot))
        {
            try { placements.AddRange(IplFile.Parse(path)); }
            catch { /* matches the snapshot's own tolerance */ }
        }
        placements.AddRange(BinaryIplFile.ReadAllFromGame(
            gameRoot, id => namesById.TryGetValue(id, out var name) ? name : string.Empty));

        // Guards against a silently-empty read passing this test by comparing nothing to nothing.
        Assert.True(placements.Count > 40_000, $"expected a full map, read only {placements.Count} placements");

        var expectedDensest = PlacementDensity.FindDensest(placements, weights);
        var expectedHeaviest = PlacementDensity.HeaviestCellBytes(placements, weights);

        // The new way: folded in one at a time, nothing kept.
        var cells = new MapCells();
        cells.AddRange(placements, weights);

        Assert.Equal(placements.Count, cells.TotalPlacements);
        Assert.Equal(expectedHeaviest, cells.HeaviestBytes(weights));
        Assert.Equal(expectedDensest!.ObjectCount, cells.Densest(weights)!.ObjectCount);
        Assert.Equal(expectedDensest.Bytes, cells.Densest(weights)!.Bytes);
    }
}
