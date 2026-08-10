using SAFT.Core;

namespace SAFT.Core.Tests;

public class StreamingImpactTests
{
    /// <summary>
    /// A miniature game: one placed building and one spawned car, each with its own texture, plus
    /// the .ipl that puts the building somewhere.
    /// </summary>
    private static string BuildTinyGame()
    {
        var root = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(root, "models"));
        Directory.CreateDirectory(Path.Combine(root, "data", "maps"));
        File.WriteAllText(Path.Combine(root, "gta_sa.exe"), "stub");

        // Loose assets, so their sizes are read straight off disk.
        File.WriteAllBytes(Path.Combine(root, "models", "house.dff"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(root, "models", "housetxd.txd"), new byte[2000]);
        File.WriteAllBytes(Path.Combine(root, "models", "banshee.dff"), new byte[500]);
        File.WriteAllBytes(Path.Combine(root, "models", "vehicle.txd"), new byte[4000]);

        File.WriteAllText(Path.Combine(root, "data", "maps", "test.ide"), """
            objs
            5000, house, housetxd, 300, 0
            end
            cars
            400, banshee, vehicle, car, BANSHEE, BANSHEE, null, 7, 7, 0, 160, 0.8
            end
            """);
        File.WriteAllText(Path.Combine(root, "data", "maps", "test.ipl"), """
            inst
            5000, house, 0, 2500, -1690, 14, 0, 0, 0, 1, -1
            end
            """);
        return root;
    }

    [Fact]
    public void Reports_no_change_when_a_mod_replaces_nothing()
    {
        var game = BuildTinyGame();

        var impact = StreamingImpact.Measure(game, new Dictionary<string, long>());

        Assert.False(impact.IncreasesLoad);
        Assert.Equal(impact.HeaviestAreaBefore, impact.HeaviestAreaAfter);
        Assert.Equal(1.0, impact.AreaMultiplier, 3);
    }

    [Fact]
    public void Catches_a_heavier_replacement_of_a_placed_building()
    {
        var game = BuildTinyGame();

        // house.dff 1000 -> 9000. Its area held 3000 bytes (model + texture), now 11000.
        var impact = StreamingImpact.Measure(game, new Dictionary<string, long> { ["house.dff"] = 9000 });

        Assert.True(impact.IncreasesLoad);
        Assert.Equal(3000, impact.HeaviestAreaBefore);
        Assert.Equal(11000, impact.HeaviestAreaAfter);
        Assert.Equal(1, impact.ReplacedPlacedModels);
        Assert.Equal(0, impact.ReplacedDynamicModels);
    }

    [Fact]
    public void Catches_a_heavier_vehicle_texture_even_though_nothing_is_placed_anywhere()
    {
        var game = BuildTinyGame();

        // This is the 90s car pack case: vehicles are spawned by traffic, never placed in an .ipl,
        // so a per-area analysis sees nothing at all. Measuring them by total weight is the only
        // way to notice — and it's why an over-heavy car pack degrades rendering EVERYWHERE rather
        // than in one district.
        var impact = StreamingImpact.Measure(game, new Dictionary<string, long> { ["vehicle.txd"] = 40_000 });

        Assert.Equal(impact.HeaviestAreaBefore, impact.HeaviestAreaAfter);  // no placed object changed
        Assert.True(impact.IncreasesLoad);                                  // but the load went up regardless
        Assert.Equal(4500, impact.DynamicWeightBefore);                     // banshee.dff 500 + vehicle.txd 4000
        Assert.Equal(40_500, impact.DynamicWeightAfter);
        Assert.Equal(1, impact.ReplacedDynamicModels);
        Assert.Equal(9.0, impact.DynamicMultiplier, 1);
    }

    [Fact]
    public void A_shared_texture_replacement_is_counted_once_not_once_per_model()
    {
        var root = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(root, "models"));
        Directory.CreateDirectory(Path.Combine(root, "data", "maps"));
        File.WriteAllText(Path.Combine(root, "gta_sa.exe"), "stub");
        File.WriteAllBytes(Path.Combine(root, "models", "car_a.dff"), new byte[100]);
        File.WriteAllBytes(Path.Combine(root, "models", "car_b.dff"), new byte[100]);
        File.WriteAllBytes(Path.Combine(root, "models", "vehicle.txd"), new byte[1000]);
        File.WriteAllText(Path.Combine(root, "data", "maps", "cars.ide"), """
            cars
            400, car_a, vehicle, car, A, A, null, 7, 7, 0, 160, 0.8
            401, car_b, vehicle, car, B, B, null, 7, 7, 0, 160, 0.8
            end
            """);

        var impact = StreamingImpact.Measure(root, new Dictionary<string, long> { ["vehicle.txd"] = 5000 });

        // 200 bytes of models plus the shared texture ONCE: 5200, not 10200.
        Assert.Equal(1200, impact.DynamicWeightBefore);
        Assert.Equal(5200, impact.DynamicWeightAfter);
    }

    [Fact]
    public void A_smaller_replacement_does_not_count_as_increasing_the_load()
    {
        var game = BuildTinyGame();

        var impact = StreamingImpact.Measure(game, new Dictionary<string, long> { ["housetxd.txd"] = 100 });

        Assert.False(impact.IncreasesLoad);
        Assert.True(impact.HeaviestAreaAfter < impact.HeaviestAreaBefore);
    }
}
