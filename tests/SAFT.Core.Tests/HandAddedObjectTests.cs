using System.Text;
using SAFT.Core;

namespace SAFT.Core.Tests;

/// <summary>
/// Updating an object somebody added by hand, which is neither a stock file nor one of SAFT's.
///
/// A modder adds giantcastle to their game last week — asset, definition, placement, all their own
/// work. Today they build a modpack carrying an updated giantcastle.dff with its own .ide and .ipl,
/// and install it with SAFT. The object already exists and the pack declares it, so the two questions
/// SAFT can ask disagree: "is this file already here" says replacement, "does the mod define it"
/// says addition.
///
/// Neither answer is the right one. What settles it is who already DEFINES the object: a definition
/// in the user's own .ide makes it theirs, so the file is replaced and backed up, and SAFT writes no
/// map data — it does not edit map files it did not create. Getting this wrong is destructive in both
/// directions: their model overwritten with no backup and deleted on the next uninstall, or a second
/// castle standing next to the first on a second object ID.
/// </summary>
public class HandAddedObjectTests
{
    private static (string Name, Func<Stream> OpenContent) File_(string name, string content) =>
        (name, () => new MemoryStream(Encoding.ASCII.GetBytes(content)));

    /// <summary>A game the user added giantcastle to a week ago, by hand.</summary>
    private static string BuildGameWithAHandAddedCastle()
    {
        var root = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(root, "models"));
        Directory.CreateDirectory(Path.Combine(root, "data", "maps"));
        File.WriteAllText(Path.Combine(root, "gta_sa.exe"), "stub");
        ImgArchive.Write(Path.Combine(root, "models", "gta3.img"), new[]
        {
            File_("banshee.dff", "vanilla car model"),
            File_("giantcastle.dff", "last weeks castle"),
        });
        File.WriteAllText(Path.Combine(root, "data", "maps", "existing.ide"), "objs\n4806, someobject, sometxd, 150, 0\nend");
        File.WriteAllText(Path.Combine(root, "data", "maps", "mine.ide"), "objs\n5000, giantcastle, giantcastletxd, 300, 0\nend");
        File.WriteAllText(Path.Combine(root, "data", "maps", "mine.ipl"),
            "inst\n5000, giantcastle, 0, 100.0, 200.0, 30.0, 0, 0, 0, 1, -1\nend");
        File.WriteAllText(Path.Combine(root, "data", "gta.dat"),
            "IDE data\\maps\\existing.ide\nIDE data\\maps\\mine.ide\nIPL data\\maps\\mine.ipl\n");
        return root;
    }

    /// <summary>The updated pack: same object, new model, its own definition on a different ID.</summary>
    private static string BuildUpdatedPack()
    {
        var mod = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(mod, "giantcastle.dff"), "updated castle v2");
        File.WriteAllText(Path.Combine(mod, "giantcastle.ide"), "objs\n12000, giantcastle, giantcastletxd, 300, 0\nend");
        File.WriteAllText(Path.Combine(mod, "giantcastle.ipl"),
            "inst\n12000, giantcastle, 0, 105.0, 205.0, 30.0, 0, 0, 0, 1, -1\nend");
        return mod;
    }

    private static string ReadEntry(string game, string name)
    {
        using var img = ImgArchive.Open(Path.Combine(game, "models", "gta3.img"));
        using var entry = img.OpenEntry(img.Entries.First(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        using var reader = new StreamReader(entry);
        return reader.ReadToEnd().TrimEnd('\0');
    }

    private static IReadOnlyList<string> EntryNames(string game)
    {
        using var img = ImgArchive.Open(Path.Combine(game, "models", "gta3.img"));
        return img.Entries.Select(e => e.Name).ToList();
    }

    /// <summary>Every line mentioning a model, across every .ide or .ipl under data/.</summary>
    private static List<string> MapLinesFor(string game, string model, string extension)
    {
        return Directory.GetFiles(Path.Combine(game, "data"), "*" + extension, SearchOption.AllDirectories)
            .Where(p => !Path.GetFileName(p).StartsWith("._", StringComparison.Ordinal))
            .SelectMany(File.ReadAllLines)
            .Where(l => l.Contains(model, StringComparison.OrdinalIgnoreCase) && !l.TrimStart().StartsWith('#'))
            .ToList();
    }

    private static void Install(string game, string mod, string backups)
    {
        var plan = DirectModInstaller.Plan(game, mod);
        var existing = new HashSet<string>(EntryNames(game), StringComparer.OrdinalIgnoreCase);
        var additions = AdditionScanner.Scan(game, mod, existing.Contains);

        DirectModInstaller.Apply(plan.Without(additions.AssetFileNames), backups);

        var manifest = AdditionsManifest.Load(backups) ?? new AdditionsManifest { GameRootPath = game };
        manifest.Mods.Add(AdditionInstaller.Apply(game, additions, "Castle Update").Recorded);
        manifest.Save(backups);
    }

    private static void Uninstall(string game, string backups)
    {
        var manifest = AdditionsManifest.Load(backups);
        if (manifest is not null && manifest.Mods.Count > 0)
        {
            AdditionUninstaller.Remove(game, manifest, manifest.Mods.Select(m => m.Name).ToList());
            manifest.Save(backups);
        }

        DirectModInstaller.Apply(DirectModInstaller.Plan(game, backups), backupOutputFolder: null);
    }

    [Fact]
    public void An_object_the_user_already_defined_gets_one_definition_and_one_placement_not_two()
    {
        var game = BuildGameWithAHandAddedCastle();
        Install(game, BuildUpdatedPack(), TestScratch.NewDir());

        // Their line, and only their line. A second definition on a second ID puts a second castle
        // in the world, both pointing at the one model that actually exists.
        var definitions = MapLinesFor(game, "giantcastle", ".ide");
        var placements = MapLinesFor(game, "giantcastle", ".ipl");

        Assert.Single(definitions);
        Assert.Single(placements);
        Assert.Contains("5000", definitions[0]);
        Assert.Contains("100", placements[0]);
    }

    [Fact]
    public void The_updated_model_goes_in_and_the_one_it_replaced_is_kept()
    {
        var game = BuildGameWithAHandAddedCastle();
        var backups = TestScratch.NewDir();

        Install(game, BuildUpdatedPack(), backups);

        Assert.Equal("updated castle v2", ReadEntry(game, "giantcastle.dff"));
        Assert.Equal(1, EntryNames(game).Count(n => n.Equals("giantcastle.dff", StringComparison.OrdinalIgnoreCase)));

        // Their week-old model is not stock, but it is theirs and it was there first, so it is kept.
        // Without this the uninstall has nothing to put back.
        var backedUp = Directory.GetFiles(backups, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList();
        Assert.Contains("giantcastle.dff", backedUp);
    }

    [Fact]
    public void Uninstalling_puts_their_model_back_and_leaves_their_map_files_alone()
    {
        var game = BuildGameWithAHandAddedCastle();
        var backups = TestScratch.NewDir();
        var theirIde = File.ReadAllText(Path.Combine(game, "data", "maps", "mine.ide"));
        var theirIpl = File.ReadAllText(Path.Combine(game, "data", "maps", "mine.ipl"));

        Install(game, BuildUpdatedPack(), backups);
        Uninstall(game, backups);

        // The object survives the uninstall entirely: their model, their definition, their placement.
        // Deleting the asset here would leave their own .ide defining an object with no model behind
        // it, which is a worse state than SAFT found the game in.
        Assert.Equal("last weeks castle", ReadEntry(game, "giantcastle.dff"));
        Assert.Equal(theirIde, File.ReadAllText(Path.Combine(game, "data", "maps", "mine.ide")));
        Assert.Equal(theirIpl, File.ReadAllText(Path.Combine(game, "data", "maps", "mine.ipl")));
    }
    /// <summary>
    /// Plenty of people keep their mod folders inside the game folder. A walk of the game that
    /// wanders into one would read the pack's own .ide as the game's, decide the game already
    /// defines these objects, and install none of them - a silent no-op with a cheerful log.
    /// </summary>
    [Fact]
    public void A_mod_folder_kept_inside_the_game_folder_is_still_the_mods_own()
    {
        var game = BuildGameWithAHandAddedCastle();

        // The pack lives in the game folder, next to data/ and models/.
        var mod = Path.Combine(game, "My Mods", "Castle Pack");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "watchtower.dff"), "a brand new tower");
        File.WriteAllText(Path.Combine(mod, "watchtower.ide"), "objs\n12001, watchtower, watchtowertxd, 300, 0\nend");
        File.WriteAllText(Path.Combine(mod, "watchtower.ipl"),
            "inst\n12001, watchtower, 0, 300.0, 400.0, 20.0, 0, 0, 0, 1, -1\nend");

        Install(game, mod, TestScratch.NewDir());

        Assert.Contains("watchtower.dff", EntryNames(game));
        Assert.NotEmpty(MapLinesFor(game, "watchtower", ".ipl"));
    }
}
