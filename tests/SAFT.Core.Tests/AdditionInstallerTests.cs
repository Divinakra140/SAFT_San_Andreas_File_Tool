using System.Text;
using SAFT.Core;

namespace SAFT.Core.Tests;

public class AdditionInstallerTests
{
    private static (string Name, Func<Stream> OpenContent) File_(string name, string content) =>
        (name, () => new MemoryStream(Encoding.ASCII.GetBytes(content)));

    /// <summary>A minimal but realistic game: one archive, one .ide, and a gta.dat listing it.</summary>
    private static string BuildGame()
    {
        var root = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(root, "models"));
        Directory.CreateDirectory(Path.Combine(root, "data", "maps"));
        File.WriteAllText(Path.Combine(root, "gta_sa.exe"), "stub");

        ImgArchive.Write(Path.Combine(root, "models", "gta3.img"), new[]
        {
            File_("banshee.dff", "an existing car model"),
        });

        File.WriteAllText(Path.Combine(root, "data", "maps", "existing.ide"), """
            objs
            4806, someobject, sometxd, 150, 0
            end
            """);
        File.WriteAllText(Path.Combine(root, "data", "gta.dat"), "IDE data\\maps\\existing.ide\n");
        return root;
    }

    private static string BuildMod(string gameRoot)
    {
        var mod = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(mod, "saftcastle.dff"), "a brand new model");
        File.WriteAllText(Path.Combine(mod, "saftcastle.txd"), "its texture");
        File.WriteAllText(Path.Combine(mod, "castle.ide"), "objs\n12000, saftcastle, saftcastletxd, 300, 0\nend");
        File.WriteAllText(Path.Combine(mod, "castle.ipl"),
            "inst\n12000, saftcastle, 0, 2495.5, -1690.25, 14, 0, 0, 0, 1, -1\nend");
        return mod;
    }

    private static AdditionPlan PlanFor(string gameRoot, string modFolder)
    {
        var archiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ImgArchive.Open(Path.Combine(gameRoot, "models", "gta3.img")))
            foreach (var e in archive.Entries) archiveNames.Add(e.Name);

        return AdditionScanner.Scan(gameRoot, modFolder, archiveNames.Contains);
    }

    [Fact]
    public void Adds_the_new_assets_to_the_archive_without_disturbing_what_was_there()
    {
        var game = BuildGame();
        var mod = BuildMod(game);

        AdditionInstaller.Apply(game, PlanFor(game, mod), "My Castle");

        using var archive = ImgArchive.Open(Path.Combine(game, "models", "gta3.img"));
        var names = archive.Entries.Select(e => e.Name).ToList();
        Assert.Contains("saftcastle.dff", names);
        Assert.Contains("saftcastle.txd", names);
        Assert.Contains("banshee.dff", names);   // the original survives the rebuild

        using var original = archive.OpenEntry(archive.Entries.Single(e => e.Name == "banshee.dff"));
        using var reader = new StreamReader(original);
        Assert.StartsWith("an existing car model", reader.ReadToEnd());
    }

    [Fact]
    public void Writes_its_own_map_files_and_never_touches_the_games()
    {
        var game = BuildGame();
        var mod = BuildMod(game);
        var existingIde = Path.Combine(game, "data", "maps", "existing.ide");
        var before = File.ReadAllText(existingIde);

        AdditionInstaller.Apply(game, PlanFor(game, mod), "My Castle");

        // The isolation that makes uninstall safe: Rockstar's file is byte-identical afterwards.
        Assert.Equal(before, File.ReadAllText(existingIde));

        var saftIde = Path.Combine(game, "data", "maps", "saft", AdditionInstaller.SaftIdeFileName);
        var saftIpl = Path.Combine(game, "data", "maps", "saft", AdditionInstaller.SaftIplFileName);
        Assert.True(File.Exists(saftIde));
        Assert.True(File.Exists(saftIpl));
        Assert.Contains("saftcastle", File.ReadAllText(saftIde));
        Assert.Contains("saftcastle", File.ReadAllText(saftIpl));
    }

    [Fact]
    public void Registers_its_map_files_with_the_game_so_they_actually_load()
    {
        var game = BuildGame();
        var mod = BuildMod(game);

        AdditionInstaller.Apply(game, PlanFor(game, mod), "My Castle");

        // An .ide the game was never told about is simply ignored, however correct its contents.
        var gtaDat = File.ReadAllText(Path.Combine(game, "data", "gta.dat"));
        Assert.Contains(@"IDE data\maps\saft\saft_additions.ide", gtaDat);
        Assert.Contains(@"IPL data\maps\saft\saft_additions.ipl", gtaDat);
        Assert.Contains(@"IDE data\maps\existing.ide", gtaDat);   // the original listing is untouched
    }

    [Fact]
    public void Registers_each_map_file_inside_its_own_block_not_at_the_end_of_gta_dat()
    {
        // The bug this pins down cost a long investigation. gta.dat groups its entries — every IDE,
        // then every IPL — and appending both lines to the END of the file puts the definition after
        // every placement the game has already processed. San Andreas crashes on world load.
        // Verified in game by moving nothing but these two lines: at the end of the file it died
        // instantly, inside their blocks the same object loaded and rendered.
        var game = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(game, "models"));
        Directory.CreateDirectory(Path.Combine(game, "data", "maps"));
        ImgArchive.Write(Path.Combine(game, "models", "gta3.img"), new[] { File_("banshee.dff", "car") });
        File.WriteAllText(Path.Combine(game, "data", "maps", "existing.ide"), "objs\n4806, someobject, sometxd, 150, 0\nend");

        // A gta.dat shaped like the real one: IDEs grouped, then IPLs, then trailing directives.
        File.WriteAllLines(Path.Combine(game, "data", "gta.dat"), new[]
        {
            "IMG MODELS\\CUTSCENE.IMG",
            "IDE DATA\\MAPS\\generic\\vegepart.IDE",
            "IDE data\\maps\\existing.ide",
            "IPL DATA\\MAPS\\country\\countryN.IPL",
            "IPL DATA\\MAPS\\audiozon.ipl",
            "SPLASH loadsc4",
        });

        AdditionInstaller.Apply(game, PlanFor(game, BuildMod(game)), "My Castle");

        var lines = File.ReadAllLines(Path.Combine(game, "data", "gta.dat")).ToList();
        int At(string text) => lines.FindIndex(l => l.Trim().Equals(text, StringComparison.OrdinalIgnoreCase));

        var ide = At(@"IDE data\maps\saft\saft_additions.ide");
        var ipl = At(@"IPL data\maps\saft\saft_additions.ipl");

        Assert.True(ide >= 0 && ipl >= 0, "both entries must be registered");
        Assert.True(ide < At("IPL DATA\\MAPS\\country\\countryN.IPL"), "the IDE must come before every IPL");
        Assert.True(ide > At("IDE DATA\\MAPS\\generic\\vegepart.IDE"), "the IDE joins the end of the IDE block");
        Assert.True(ipl > At("IPL DATA\\MAPS\\country\\countryN.IPL"), "the IPL joins the end of the IPL block");
        Assert.True(ipl < At("SPLASH loadsc4"), "neither line may be appended past the end of the blocks");
    }

    [Fact]
    public void Registers_at_the_end_when_the_game_lists_no_entries_of_that_kind()
    {
        // Nothing to join, so appending is the only option — must not throw or lose the entry.
        var game = BuildGame();   // its gta.dat has an IDE line but no IPL line at all

        AdditionInstaller.Apply(game, PlanFor(game, BuildMod(game)), "My Castle");

        var gtaDat = File.ReadAllText(Path.Combine(game, "data", "gta.dat"));
        Assert.Contains(@"IPL data\maps\saft\saft_additions.ipl", gtaDat);
    }

    [Fact]
    public void Installing_a_second_mod_does_not_register_the_map_files_twice()
    {
        var game = BuildGame();
        AdditionInstaller.Apply(game, PlanFor(game, BuildMod(game)), "First");

        var secondMod = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(secondMod, "safttower.dff"), "another model");
        File.WriteAllText(Path.Combine(secondMod, "tower.ide"), "objs\n12000, safttower, towertxd, 300, 0\nend");

        AdditionInstaller.Apply(game, PlanFor(game, secondMod), "Second");

        // A duplicate registration would make the game load everything twice.
        var lines = File.ReadAllLines(Path.Combine(game, "data", "gta.dat"));
        Assert.Equal(1, lines.Count(l => l.Trim().Equals(@"IDE data\maps\saft\saft_additions.ide", StringComparison.OrdinalIgnoreCase)));

        // Both mods' objects coexist in the one SAFT file.
        var saftIde = File.ReadAllText(Path.Combine(game, "data", "maps", "saft", AdditionInstaller.SaftIdeFileName));
        Assert.Contains("saftcastle", saftIde);
        Assert.Contains("safttower", saftIde);
    }

    [Fact]
    public void Gives_the_second_mod_a_different_object_id_than_the_first()
    {
        var game = BuildGame();
        var first = AdditionInstaller.Apply(game, PlanFor(game, BuildMod(game)), "First");

        var secondMod = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(secondMod, "safttower.dff"), "another model");
        // Deliberately claims the same ID the first mod shipped with.
        File.WriteAllText(Path.Combine(secondMod, "tower.ide"), "objs\n12000, safttower, towertxd, 300, 0\nend");

        var second = AdditionInstaller.Apply(game, PlanFor(game, secondMod), "Second");

        // Allocation is self-correcting: the first mod's ID is now IN the game's .ide files, so the
        // second scan sees it as taken. No cross-install bookkeeping needed.
        Assert.NotEqual(first.Recorded.ObjectIds.Single(), second.Recorded.ObjectIds.Single());
    }

    [Fact]
    public void Records_everything_needed_to_undo_the_installation()
    {
        var game = BuildGame();
        var mod = BuildMod(game);

        var result = AdditionInstaller.Apply(game, PlanFor(game, mod), "My Castle");

        var record = result.Recorded;
        Assert.Equal("My Castle", record.Name);
        Assert.Single(record.ObjectIds);
        Assert.Equal(2, record.ArchiveEntries.Count);                       // the .dff and the .txd
        Assert.All(record.ArchiveEntries, e => Assert.False(string.IsNullOrWhiteSpace(e.Sha256)));
        Assert.Contains(record.DataLines, l => l.Line.Contains("saftcastle") && l.FileRelativePath.EndsWith(".ide"));
        Assert.Contains(record.DataLines, l => l.Line.Contains("saftcastle") && l.FileRelativePath.EndsWith(".ipl"));
        Assert.Contains(record.DataLines, l => l.FileRelativePath.EndsWith("gta.dat"));
    }
}
