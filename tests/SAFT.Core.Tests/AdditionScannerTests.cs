using SAFT.Core;

namespace SAFT.Core.Tests;

public class AdditionScannerTests
{
    private static readonly GameDensityBaseline Baseline = new(BusiestObjectCount: 171, BusiestBytes: 8_600_000);

    /// <summary>A stand-in game: only these names already exist, so anything else is an addition.</summary>
    private static Func<string, bool> GameContaining(params string[] names)
    {
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    private static AdditionPlan ScanMod(string modFolder, Func<string, bool> existsInGame) =>
        AdditionScanner.Scan(
            gameRoot: modFolder,             // unused here: ids and baseline are supplied directly
            modSourceFolder: modFolder,
            existsInGame: existsInGame,
            baseline: Baseline,
            usedObjectIds: new HashSet<int> { 616, 617 });

    [Fact]
    public void Separates_new_assets_from_replacements()
    {
        var mod = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(mod, "banshee.dff"), "replacement for an existing car");
        File.WriteAllText(Path.Combine(mod, "saftcastle.dff"), "a brand new object");
        File.WriteAllText(Path.Combine(mod, "saftcastle.txd"), "its texture");

        var plan = ScanMod(mod, GameContaining("banshee.dff"));

        // banshee.dff already exists, so it's an ordinary replacement and none of the addition
        // machinery applies to it.
        Assert.Equal(2, plan.NewAssets.Count);
        Assert.DoesNotContain(plan.NewAssets, a => a.FileName == "banshee.dff");
    }

    [Fact]
    public void Counts_slots_as_definitions_not_files()
    {
        var mod = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(mod, "saftcastle.dff"), "model");
        File.WriteAllText(Path.Combine(mod, "saftcastle.txd"), "texture");
        File.WriteAllText(Path.Combine(mod, "saftcastle.col"), "collision");
        File.WriteAllText(Path.Combine(mod, "castle.ide"), """
            objs
            12000, saftcastle, saftcastletxd, 300, 0
            end
            """);

        var plan = ScanMod(mod, GameContaining());

        // Three files, but one object: a .dff/.txd/.col are referenced by name and cost no slots.
        // Counting files here would tell the user they need three times the room they actually do.
        Assert.Equal(3, plan.NewAssets.Count);
        Assert.Equal(1, plan.SlotsRequired);
    }

    [Fact]
    public void Placing_one_object_many_times_still_needs_only_one_slot()
    {
        var mod = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(mod, "lamp.dff"), "model");
        File.WriteAllText(Path.Combine(mod, "lamp.ide"), """
            objs
            12000, lamp, lamptxd, 300, 0
            end
            """);
        File.WriteAllText(Path.Combine(mod, "lamp.ipl"),
            "inst\n" + string.Join("\n",
                Enumerable.Range(0, 40).Select(i => $"12000, lamp, 0, {2500 + i}, -1690, 14, 0, 0, 0, 1, -1")) + "\nend");

        var plan = ScanMod(mod, GameContaining());

        Assert.Equal(1, plan.SlotsRequired);
        Assert.Equal(40, plan.Placements.Count);
    }

    [Fact]
    public void Detects_new_assets_shipped_without_any_placement_data()
    {
        var mod = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(mod, "saftcastle.dff"), "model");
        File.WriteAllText(Path.Combine(mod, "saftcastle.txd"), "texture");

        var plan = ScanMod(mod, GameContaining());

        // Without .ide/.ipl the game has no idea these exist: they would burn slots and appear
        // nowhere, so the app has to ask rather than quietly installing them.
        Assert.True(plan.LacksPlacementData);
    }

    [Fact]
    public void A_mod_that_supplies_placement_data_is_not_flagged_as_lacking_it()
    {
        var mod = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(mod, "saftcastle.dff"), "model");
        File.WriteAllText(Path.Combine(mod, "castle.ide"), "objs\n12000, saftcastle, saftcastletxd, 300, 0\nend");
        File.WriteAllText(Path.Combine(mod, "castle.ipl"), "inst\n12000, saftcastle, 0, 2500, -1690, 14, 0, 0, 0, 1, -1\nend");

        var plan = ScanMod(mod, GameContaining());

        Assert.False(plan.LacksPlacementData);
        Assert.Single(plan.Placements);
    }

    [Fact]
    public void Placing_a_model_with_no_collision_is_a_blocking_problem()
    {
        // Verified on a real install: San Andreas crashes at world load when a placed object has no
        // collision record, whatever object id it uses and wherever on the map it sits. So this is
        // fatal, not the cosmetic "you can walk through it" an earlier version described.
        var mod = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(mod, "solid.dff"), "model");
        File.WriteAllText(Path.Combine(mod, "ghost.dff"), "model");
        File.WriteAllBytes(Path.Combine(mod, "anything.col"), ColBundleTests.MakeRecord("solid", payloadBytes: 120));
        File.WriteAllText(Path.Combine(mod, "objects.ide"), """
            objs
            12000, solid, sometxd, 300, 0
            12001, ghost, sometxd, 300, 0
            end
            """);
        File.WriteAllText(Path.Combine(mod, "objects.ipl"), """
            inst
            12000, solid, 0, 2500, -1690, 14, 0, 0, 0, 1, -1
            12001, ghost, 0, 2510, -1690, 14, 0, 0, 0, 1, -1
            end
            """);

        var plan = ScanMod(mod, GameContaining());

        Assert.Equal("ghost", Assert.Single(plan.ModelsWithoutCollision));
        Assert.True(plan.PlacesModelsWithoutCollision);
    }

    [Fact]
    public void Collision_is_matched_by_the_name_inside_the_bundle_not_the_file_name()
    {
        // A .col can be called anything and hold records for many models — packing collision for a
        // whole mod into one file is normal. Comparing file names to model names, as an earlier
        // version did, called that mod broken.
        var mod = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(mod, "tower.dff"), "model");
        File.WriteAllText(Path.Combine(mod, "keep.dff"), "model");
        File.WriteAllBytes(Path.Combine(mod, "castle_pack.col"),
            ColBundleTests.MakeRecord("tower", payloadBytes: 120).Concat(ColBundleTests.MakeRecord("keep", payloadBytes: 120)).ToArray());
        File.WriteAllText(Path.Combine(mod, "castle.ide"), """
            objs
            12000, tower, sometxd, 300, 0
            12001, keep, sometxd, 300, 0
            end
            """);
        File.WriteAllText(Path.Combine(mod, "castle.ipl"), """
            inst
            12000, tower, 0, 2500, -1690, 14, 0, 0, 0, 1, -1
            12001, keep, 0, 2510, -1690, 14, 0, 0, 0, 1, -1
            end
            """);

        var plan = ScanMod(mod, GameContaining());

        Assert.Empty(plan.ModelsWithoutCollision);
        Assert.False(plan.PlacesModelsWithoutCollision);
        Assert.Equal(2, plan.Collision.Count);
    }

    [Fact]
    public void An_empty_collision_record_is_allowed_and_reported_as_walk_through()
    {
        // Tested in game: a record with no spheres, boxes or faces loads fine and gives an object
        // you walk straight through. That's a deliberate technique for pass-through scenery, and
        // San Andreas uses it itself for plc_stinger. An earlier version refused these, which would
        // have blocked a legitimate mod — only a MISSING record crashes the game.
        var mod = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(mod, "hollow.dff"), "model");
        File.WriteAllBytes(Path.Combine(mod, "hollow.col"), ColBundleTests.MakeEmptyRecord("hollow"));
        File.WriteAllText(Path.Combine(mod, "objects.ide"), """
            objs
            12000, hollow, sometxd, 300, 0
            end
            """);
        File.WriteAllText(Path.Combine(mod, "objects.ipl"), """
            inst
            12000, hollow, 0, 2500, -1690, 14, 0, 0, 0, 1, -1
            end
            """);

        var plan = ScanMod(mod, GameContaining());

        Assert.Empty(plan.ModelsWithoutCollision);
        Assert.False(plan.PlacesModelsWithoutCollision);
        Assert.Equal("hollow", Assert.Single(plan.WalkThroughModels));
    }

    [Fact]
    public void A_model_that_is_defined_but_never_placed_needs_no_collision()
    {
        // Nothing is placed, so nothing asks the engine for bounds. Confirmed in game: definitions
        // alone load fine, and only adding the placement caused the crash.
        var mod = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(mod, "spare.dff"), "model");
        File.WriteAllText(Path.Combine(mod, "objects.ide"), """
            objs
            12000, spare, sometxd, 300, 0
            end
            """);

        var plan = ScanMod(mod, GameContaining());

        Assert.Empty(plan.ModelsWithoutCollision);
        Assert.False(plan.PlacesModelsWithoutCollision);
    }

    [Fact]
    public void Knows_when_a_mod_needs_more_slots_than_the_game_has_left()
    {
        var mod = TestScratch.NewDir();
        var lines = Enumerable.Range(0, 10).Select(i => $"{13000 + i}, obj{i}, sometxd, 300, 0");
        File.WriteAllText(Path.Combine(mod, "many.ide"), "objs\n" + string.Join("\n", lines) + "\nend");
        for (var i = 0; i < 10; i++) File.WriteAllText(Path.Combine(mod, $"obj{i}.dff"), "model");

        var plan = AdditionScanner.Scan(
            gameRoot: mod, modSourceFolder: mod, existsInGame: GameContaining(),
            baseline: Baseline,
            // A game with almost no room left: only ids 616-620 are free below the limit.
            usedObjectIds: new HashSet<int>(Enumerable.Range(621, 19_379)));

        Assert.Equal(10, plan.SlotsRequired);
        // Only headroom above the highest used ID counts; the gaps below are engine-reserved.
        Assert.True(plan.SlotsAvailable < 10);
        Assert.False(plan.FitsInAvailableSlots);
    }

    [Fact]
    public void A_malformed_ide_does_not_abort_the_whole_scan()
    {
        var mod = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(mod, "broken.ide"), "objs\nthis is not a definition at all\nend");
        File.WriteAllText(Path.Combine(mod, "saftcastle.dff"), "model");

        var plan = ScanMod(mod, GameContaining());

        // The rest of the mod is still perfectly installable; the user is better served by a report
        // than by an exception.
        Assert.Single(plan.NewAssets);
        Assert.Empty(plan.Definitions);
    }
}
