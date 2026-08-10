using SAFT.Core;

namespace SAFT.Core.Tests;

public class ObjectIdAllocatorTests
{
    private static string WriteIde(string dir, string name, string contents)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void Parses_definition_lines_and_ignores_comments_blank_lines_and_section_markers()
    {
        var root = TestScratch.NewDir();
        WriteIde(root, "test.ide", """
            # a comment
            objs
            4806, BTOLAND8_LAS, ground5_las, 150, 0
            4807, LAroads_20gh_LAs, lasroads_las, 150, 0

            end
            """);

        var defs = IdeFile.Parse(Path.Combine(root, "test.ide"));

        Assert.Equal(2, defs.Count);
        Assert.Equal(4806, defs[0].ObjectId);
        Assert.Equal("BTOLAND8_LAS", defs[0].ModelName);
        Assert.Equal("ground5_las", defs[0].TextureName);
        Assert.Equal("objs", defs[0].Section);
        // The verbatim line is kept so an added definition can later be found by content rather than
        // by line number, which shifts as other mods are installed and removed.
        Assert.Equal("4806, BTOLAND8_LAS, ground5_las, 150, 0", defs[0].RawLine);
    }

    [Fact]
    public void Counts_ids_from_every_section_because_they_share_one_model_table()
    {
        var root = TestScratch.NewDir();
        WriteIde(root, "mixed.ide", """
            objs
            4806, a_building, sometxd, 150, 0
            end
            cars
            400, landstal, landstal, car, LANDSTAL, LANDSTK, null, 7, 7, 0, 160, 0.8
            end
            peds
            7, bfori, BFORI, STAT_NORMAL, man, 5, 7f, null, null
            end
            """);

        var used = ObjectIdAllocator.ScanUsedIds(root);

        // A ped, a vehicle and a map object all consume the same ID space — missing any of them
        // would let the allocator hand out an ID that is already taken.
        Assert.Contains(4806, used);
        Assert.Contains(400, used);
        Assert.Contains(7, used);
    }

    [Fact]
    public void Ignores_txdp_and_2dfx_sections_which_are_not_id_definitions()
    {
        var root = TestScratch.NewDir();
        WriteIde(root, "notdefs.ide", """
            txdp
            genintintcafe, genintintbarrs
            end
            2dfx
            4806, 0.0, 0.0, 0.0, 200, 200, 200, 200, 0, coronastar, shad_exp
            end
            """);

        var used = ObjectIdAllocator.ScanUsedIds(root);

        // txdp lines have no ID at all, and 2dfx lines are keyed to an object defined elsewhere.
        // Treating either as a definition would corrupt the used-ID set.
        Assert.Empty(used);
    }

    [Fact]
    public void Fills_the_lowest_free_ids_first()
    {
        var used = new SortedSet<int> { 616, 617, 619, 18630 };

        var allocated = ObjectIdAllocator.Allocate(used, 3);

        Assert.Equal(new[] { 618, 620, 621 }, allocated);
    }

    [Fact]
    public void Uses_the_gaps_between_the_games_own_ids()
    {
        // An earlier build refused these, believing a gap past the vehicle range still belonged to
        // the engine. That came from misreading a crash: the crash was a missing collision record,
        // which kills the game at world load whatever id is used. Verified afterwards on a real
        // install — a new object at 662, with collision, loads and behaves correctly — and it is
        // worth ~3,700 slots, so the test pins the behaviour down.
        var used = new SortedSet<int>(Enumerable.Range(616, 46).Concat(new[] { 18630 }));

        var allocated = ObjectIdAllocator.Allocate(used, 2);

        Assert.Equal(new[] { 662, 663 }, allocated);
    }

    [Fact]
    public void Never_allocates_into_ped_weapon_or_vehicle_id_space()
    {
        var used = new SortedSet<int>();   // nothing used at all

        var allocated = ObjectIdAllocator.Allocate(used, 5);

        // IDs below 616 belong to peds, weapons and vehicles; a building placed there breaks them.
        Assert.All(allocated, id => Assert.True(id >= ObjectIdAllocator.LowestMapObjectId));
        Assert.Equal(616, allocated[0]);
    }

    [Fact]
    public void Availability_counts_the_gaps_as_well_as_the_headroom()
    {
        var used = new SortedSet<int> { 616, 618, 700 };

        var availability = ObjectIdAllocator.Describe(used, engineLimit: 1000);

        Assert.Equal(82, availability.FreeInGaps);
        Assert.Equal(299, availability.FreeAboveHighest);
        Assert.Equal(381, availability.TotalFree);
    }

    [Fact]
    public void Refuses_to_allocate_more_slots_than_the_engine_limit_allows()
    {
        var used = new SortedSet<int>();

        // Callers are expected to check Describe() first and tell the user how many slots remain;
        // this is the backstop so an over-large mod can never half-install.
        var ex = Assert.Throws<InvalidOperationException>(
            () => ObjectIdAllocator.Allocate(used, 100, engineLimit: 700));

        Assert.Contains("Not enough free object slots", ex.Message);
    }

    [Fact]
    public void Describes_availability_split_between_gaps_and_headroom()
    {
        var used = new SortedSet<int> { 616, 618, 700 };

        var availability = ObjectIdAllocator.Describe(used, engineLimit: 1000);

        Assert.Equal(3, availability.UsedCount);
        Assert.Equal(700, availability.HighestUsedId);
        Assert.Equal(82, availability.FreeInGaps);        // 616..700 inclusive is 85 ids, 3 used
        Assert.Equal(299, availability.FreeAboveHighest); // 701..999
    }
}
