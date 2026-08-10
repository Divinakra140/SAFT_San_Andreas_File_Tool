using SAFT.Core;

namespace SAFT.Core.Tests;

public class AdditionSnippetsTests
{
    private static IReadOnlyList<IdeDefinition> Definitions(params string[] lines) =>
        IdeFile.ParseLines(new[] { "objs" }.Concat(lines).Concat(new[] { "end" }));

    private static IReadOnlyList<IplInstance> Placements(params string[] lines) =>
        IplFile.ParseLines(new[] { "inst" }.Concat(lines).Concat(new[] { "end" }));

    [Fact]
    public void Rewrites_the_definition_and_every_placement_that_refers_to_it()
    {
        var definitions = Definitions("12000, saftcastle, saftcastletxd, 300, 0");
        var placements = Placements(
            "12000, saftcastle, 0, 2500, -1690, 14, 0, 0, 0, 1, -1",
            "12000, saftcastle, 0, 2600, -1700, 14, 0, 0, 0, 1, -1");

        var result = AdditionSnippets.Rewrite(definitions, placements, new[] { 12046 });

        // The whole point: a definition moved to a new slot is useless unless its placements move
        // with it, or the game renders nothing where the mod said something should be.
        Assert.Equal("12046, saftcastle, saftcastletxd, 300, 0", Assert.Single(result.IdeLines));
        Assert.Equal(2, result.IplLines.Count);
        Assert.All(result.IplLines, line => Assert.StartsWith("12046, saftcastle", line));
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Two_mods_claiming_the_same_id_are_given_different_slots()
    {
        // Mod authors pick IDs blind, so "obviously free" collisions are routine.
        var definitions = Definitions(
            "12000, castle_a, txd_a, 300, 0",
            "12000, castle_b, txd_b, 300, 0");

        var result = AdditionSnippets.Rewrite(definitions, Array.Empty<IplInstance>(), new[] { 12046, 12047 });

        Assert.Equal("12046, castle_a, txd_a, 300, 0", result.IdeLines[0]);
        Assert.Equal("12047, castle_b, txd_b, 300, 0", result.IdeLines[1]);
    }

    [Fact]
    public void Preserves_draw_distance_and_flags_exactly_as_the_author_wrote_them()
    {
        var definitions = Definitions("12000, bigtower, towertxd, 1200, 2097152");

        var result = AdditionSnippets.Rewrite(definitions, Array.Empty<IplInstance>(), new[] { 15065 });

        // Draw distance and flags are the mod author's tuning; only the ID is SAFT's business.
        Assert.Equal("15065, bigtower, towertxd, 1200, 2097152", Assert.Single(result.IdeLines));
    }

    [Fact]
    public void Reports_a_placement_whose_object_the_mod_never_defines()
    {
        var definitions = Definitions("12000, saftcastle, saftcastletxd, 300, 0");
        var placements = Placements(
            "12000, saftcastle, 0, 2500, -1690, 14, 0, 0, 0, 1, -1",
            "9999, mystery_object, 0, 2600, -1700, 14, 0, 0, 0, 1, -1");

        var result = AdditionSnippets.Rewrite(definitions, placements, new[] { 12046 });

        // Writing this out would put a reference to an undefined object into the game files, so it's
        // skipped and reported instead.
        Assert.Single(result.IplLines);
        var problem = Assert.Single(result.Problems);
        Assert.Contains("mystery_object", problem);
        Assert.Contains("no .ide file in this mod defines it", problem);
    }

    [Fact]
    public void Placement_coordinates_survive_the_rewrite_unchanged()
    {
        var definitions = Definitions("12000, saftcastle, saftcastletxd, 300, 0");
        var placements = Placements("12000, saftcastle, 0, 2495.5, -1690.25, 14.75, 0, 0, 0, 1, -1");

        var result = AdditionSnippets.Rewrite(definitions, placements, new[] { 12046 });

        var rewritten = Assert.Single(IplFile.ParseLines(new[] { "inst", result.IplLines[0], "end" }));
        Assert.Equal(2495.5, rewritten.X, 3);
        Assert.Equal(-1690.25, rewritten.Y, 3);
        Assert.Equal(14.75, rewritten.Z, 3);
    }

    [Fact]
    public void Refuses_to_rewrite_with_fewer_ids_than_definitions()
    {
        var definitions = Definitions(
            "12000, a, atxd, 300, 0",
            "12001, b, btxd, 300, 0");

        Assert.Throws<ArgumentException>(
            () => AdditionSnippets.Rewrite(definitions, Array.Empty<IplInstance>(), new[] { 12046 }));
    }
}
