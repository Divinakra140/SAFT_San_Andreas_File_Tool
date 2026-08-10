using SAFT.Core;

namespace SAFT.Core.Tests;

public class PlacementDensityTests
{
    private static IplInstance At(double x, double y) =>
        new(12000, "saftcastle", 0, x, y, 14.0, "raw", 1);

    [Fact]
    public void Finds_the_area_holding_the_most_objects()
    {
        var placements = new[]
        {
            At(2500, -1690), At(2510, -1695), At(2520, -1680),   // three in one 200-unit cell
            At(-2000, 500),                                       // one far away
        };

        var densest = PlacementDensity.FindDensest(placements);

        Assert.NotNull(densest);
        Assert.Equal(3, densest!.ObjectCount);
    }

    [Fact]
    public void Spreading_the_same_objects_out_lowers_the_density()
    {
        // The point of the whole feature: 20 objects scattered across the map are far safer than
        // 20 in one block, because the engine only loads what's near the player.
        var clustered = Enumerable.Range(0, 20).Select(i => At(2500 + i, -1690)).ToList();
        var scattered = Enumerable.Range(0, 20).Select(i => At(i * 1000, i * 1000)).ToList();

        Assert.Equal(20, PlacementDensity.FindDensest(clustered)!.ObjectCount);
        Assert.Equal(1, PlacementDensity.FindDensest(scattered)!.ObjectCount);
    }

    [Fact]
    public void Flags_a_mod_only_when_it_out_packs_the_games_own_densest_area()
    {
        var placements = Enumerable.Range(0, 50).Select(i => At(2500 + i, -1690)).ToList();

        // Compared against a game whose own busiest area holds more than this: nothing to warn about,
        // the mod is asking for something the engine demonstrably already does.
        Assert.False(PlacementDensity.Analyse(placements, new GameDensityBaseline(171, 0)).ExceedsGameOwnDensity);

        // Compared against a sparser game, the same mod is now the densest thing in the world.
        Assert.True(PlacementDensity.Analyse(placements, new GameDensityBaseline(40, 0)).ExceedsGameOwnDensity);
    }

    [Fact]
    public void Totals_the_bytes_an_area_would_have_to_stream()
    {
        var weights = new Dictionary<string, ModelWeight>(StringComparer.OrdinalIgnoreCase)
        {
            ["castle"] = new("castle", "castletxd", ModelBytes: 1000, TextureBytes: 4000),
            ["shed"] = new("shed", "shedtxd", ModelBytes: 200, TextureBytes: 300),
        };
        var placements = new[]
        {
            new IplInstance(1, "castle", 0, 2500, -1690, 14, "raw", 1),
            new IplInstance(2, "shed", 0, 2510, -1695, 14, "raw", 2),
        };

        var densest = PlacementDensity.FindDensest(placements, weights);

        Assert.Equal(2, densest!.ObjectCount);
        Assert.Equal(5500, densest.Bytes);   // 1000+4000 + 200+300
    }

    [Fact]
    public void Counts_a_shared_texture_once_no_matter_how_many_models_use_it()
    {
        // A single .txd is routinely shared by dozens of models. Charging every placement for it
        // would report an area as many times heavier than it really is, which would make the
        // warning meaningless.
        var weights = new Dictionary<string, ModelWeight>(StringComparer.OrdinalIgnoreCase)
        {
            ["wall_a"] = new("wall_a", "shared", ModelBytes: 100, TextureBytes: 9000),
            ["wall_b"] = new("wall_b", "shared", ModelBytes: 100, TextureBytes: 9000),
            ["wall_c"] = new("wall_c", "shared", ModelBytes: 100, TextureBytes: 9000),
        };
        var placements = new[]
        {
            new IplInstance(1, "wall_a", 0, 2500, -1690, 14, "raw", 1),
            new IplInstance(2, "wall_b", 0, 2505, -1690, 14, "raw", 2),
            new IplInstance(3, "wall_c", 0, 2510, -1690, 14, "raw", 3),
        };

        var densest = PlacementDensity.FindDensest(placements, weights);

        // 300 bytes of geometry plus the shared texture ONCE, not three times.
        Assert.Equal(9300, densest!.Bytes);
    }

    [Fact]
    public void The_same_object_placed_repeatedly_costs_its_bytes_only_once()
    {
        // Placing one model 50 times pressures the pools 50 times over, but streams a single copy
        // of the asset — the two ceilings really are independent.
        var weights = new Dictionary<string, ModelWeight>(StringComparer.OrdinalIgnoreCase)
        {
            ["lamp"] = new("lamp", "lamptxd", ModelBytes: 500, TextureBytes: 1500),
        };
        var placements = Enumerable.Range(0, 50)
            .Select(i => new IplInstance(1, "lamp", 0, 2500 + i, -1690, 14, "raw", i))
            .ToList();

        var densest = PlacementDensity.FindDensest(placements, weights);

        Assert.Equal(50, densest!.ObjectCount);
        Assert.Equal(2000, densest.Bytes);
    }

    [Fact]
    public void Reports_nothing_to_warn_about_when_a_mod_adds_no_placements()
    {
        var report = PlacementDensity.Analyse(Array.Empty<IplInstance>(), new GameDensityBaseline(171, 0));

        Assert.Equal(0, report.TotalPlacements);
        Assert.Null(report.Densest);
        Assert.False(report.ExceedsGameOwnDensity);
    }

    [Fact]
    public void Flags_a_few_very_heavy_objects_even_though_the_object_count_is_tiny()
    {
        // The case that motivates weighing at all: three objects is nothing for the pools, but a
        // PS5-grade texture set can blow past the streaming budget on its own. Judging on object
        // count alone would call this mod perfectly safe.
        var weights = new Dictionary<string, ModelWeight>(StringComparer.OrdinalIgnoreCase)
        {
            ["hugecastle"] = new("hugecastle", "hugecastletxd", ModelBytes: 8_000_000, TextureBytes: 40_000_000),
        };
        var placements = new[] { new IplInstance(1, "hugecastle", 0, 2500, -1690, 14, "raw", 1) };

        var report = PlacementDensity.Analyse(
            placements, new GameDensityBaseline(BusiestObjectCount: 171, BusiestBytes: 5_000_000), weights);

        Assert.False(report.ExceedsGameOwnDensity);   // one object: trivially under
        Assert.True(report.ExceedsGameOwnWeight);     // but far heavier than anything the game ships
        Assert.False(report.WithinGameProvenRange);
    }

    [Fact]
    public void Calls_a_modest_mod_within_the_range_the_game_already_handles()
    {
        var weights = new Dictionary<string, ModelWeight>(StringComparer.OrdinalIgnoreCase)
        {
            ["castle"] = new("castle", "castletxd", ModelBytes: 50_000, TextureBytes: 200_000),
        };
        var placements = Enumerable.Range(0, 4)
            .Select(i => new IplInstance(1, "castle", 0, 2500 + i, -1690, 14, "raw", i))
            .ToList();

        var report = PlacementDensity.Analyse(
            placements, new GameDensityBaseline(BusiestObjectCount: 171, BusiestBytes: 5_000_000), weights);

        Assert.True(report.WithinGameProvenRange);
    }

    [Fact]
    public void Never_flags_when_the_games_own_density_could_not_be_measured()
    {
        // A game folder that yields no baseline (unreadable or unusual layout) must not produce a
        // scary warning off a comparison that was never actually made.
        var placements = Enumerable.Range(0, 500).Select(i => At(2500 + i * 0.1, -1690)).ToList();

        Assert.False(PlacementDensity.Analyse(placements, new GameDensityBaseline(0, 0)).ExceedsGameOwnDensity);
    }
}
