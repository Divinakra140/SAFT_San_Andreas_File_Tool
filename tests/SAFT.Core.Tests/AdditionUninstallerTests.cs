using System.Text;
using SAFT.Core;

namespace SAFT.Core.Tests;

public class AdditionUninstallerTests
{
    private static (string Name, Func<Stream> OpenContent) File_(string name, string content) =>
        (name, () => new MemoryStream(Encoding.ASCII.GetBytes(content)));

    private static string BuildGame()
    {
        var root = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(root, "models"));
        Directory.CreateDirectory(Path.Combine(root, "data", "maps"));
        File.WriteAllText(Path.Combine(root, "gta_sa.exe"), "stub");
        ImgArchive.Write(Path.Combine(root, "models", "gta3.img"), new[] { File_("banshee.dff", "an existing car") });
        File.WriteAllText(Path.Combine(root, "data", "maps", "existing.ide"), "objs\n4806, someobject, sometxd, 150, 0\nend");
        File.WriteAllText(Path.Combine(root, "data", "gta.dat"), "IDE data\\maps\\existing.ide\n");
        return root;
    }

    private static string BuildMod(string modelName)
    {
        var mod = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(mod, modelName + ".dff"), $"model for {modelName}");
        File.WriteAllText(Path.Combine(mod, modelName + ".ide"), $"objs\n12000, {modelName}, {modelName}txd, 300, 0\nend");
        File.WriteAllText(Path.Combine(mod, modelName + ".ipl"),
            $"inst\n12000, {modelName}, 0, 2495.5, -1690.25, 14, 0, 0, 0, 1, -1\nend");
        return mod;
    }

    private static AdditionPlan PlanFor(string gameRoot, string modFolder)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ImgArchive.Open(Path.Combine(gameRoot, "models", "gta3.img")))
            foreach (var e in archive.Entries) names.Add(e.Name);
        return AdditionScanner.Scan(gameRoot, modFolder, names.Contains);
    }

    private static AdditionsManifest InstallAndRecord(string game, params (string ModName, string Model)[] mods)
    {
        var manifest = new AdditionsManifest { GameRootPath = game };
        foreach (var (modName, model) in mods)
        {
            var result = AdditionInstaller.Apply(game, PlanFor(game, BuildMod(model)), modName);
            manifest.Mods.Add(result.Recorded);
        }
        return manifest;
    }

    [Fact]
    public void Removes_an_added_object_completely()
    {
        var game = BuildGame();
        var manifest = InstallAndRecord(game, ("My Castle", "saftcastle"));

        var result = AdditionUninstaller.Remove(game, manifest, new[] { "My Castle" });

        Assert.Equal("My Castle", Assert.Single(result.RemovedMods));
        Assert.Empty(result.Skipped);

        using var archive = ImgArchive.Open(Path.Combine(game, "models", "gta3.img"));
        Assert.DoesNotContain(archive.Entries, e => e.Name == "saftcastle.dff");
        Assert.Contains(archive.Entries, e => e.Name == "banshee.dff");   // the game's own asset stays

        var saftIde = Path.Combine(game, "data", "maps", "saft", AdditionInstaller.SaftIdeFileName);
        Assert.DoesNotContain("saftcastle", File.ReadAllText(saftIde));
    }

    [Fact]
    public void Removing_one_mod_leaves_another_mods_objects_intact()
    {
        var game = BuildGame();
        var manifest = InstallAndRecord(game, ("First", "saftcastle"), ("Second", "safttower"));

        AdditionUninstaller.Remove(game, manifest, new[] { "First" });

        // The whole reason removal is surgical rather than a revert: reverting the data file would
        // silently delete the second mod's work along with the first's.
        var saftIde = File.ReadAllText(Path.Combine(game, "data", "maps", "saft", AdditionInstaller.SaftIdeFileName));
        Assert.DoesNotContain("saftcastle", saftIde);
        Assert.Contains("safttower", saftIde);

        using var archive = ImgArchive.Open(Path.Combine(game, "models", "gta3.img"));
        Assert.DoesNotContain(archive.Entries, e => e.Name == "saftcastle.dff");
        Assert.Contains(archive.Entries, e => e.Name == "safttower.dff");
    }

    [Fact]
    public void Keeps_the_gta_dat_registration_while_any_addition_remains()
    {
        var game = BuildGame();
        var manifest = InstallAndRecord(game, ("First", "saftcastle"), ("Second", "safttower"));

        AdditionUninstaller.Remove(game, manifest, new[] { "First" });

        // Removing the registration early would stop the remaining mod's objects loading at all.
        Assert.Contains(@"maps\saft\saft_additions.ide", File.ReadAllText(Path.Combine(game, "data", "gta.dat")));
    }

    [Fact]
    public void Removes_the_registration_once_the_last_addition_is_gone()
    {
        var game = BuildGame();
        var manifest = InstallAndRecord(game, ("Only", "saftcastle"));

        AdditionUninstaller.Remove(game, manifest, new[] { "Only" });

        var gtaDat = File.ReadAllText(Path.Combine(game, "data", "gta.dat"));
        Assert.DoesNotContain(@"maps\saft", gtaDat);
        Assert.Contains(@"IDE data\maps\existing.ide", gtaDat);   // the game's own listing survives
    }

    [Fact]
    public void An_asset_survives_the_archives_sector_padding_and_is_still_recognised_as_untouched()
    {
        // Regression guard for a bug found testing against a real 897 MB gta3.img: a file goes into
        // an .img padded out to a 2048-byte sector, so the bytes read back are never the bytes that
        // went in. Hashing them raw made uninstall reject every asset SAFT had just added, believing
        // the user had replaced it — stranding added files in the archive permanently.
        var game = BuildGame();

        // A deliberately awkward length: nowhere near a sector boundary, so it gets heavily padded.
        var mod = TestScratch.NewDir();
        File.WriteAllBytes(Path.Combine(mod, "saftprop.dff"), Enumerable.Range(0, 100).Select(i => (byte)(i + 1)).ToArray());
        File.WriteAllText(Path.Combine(mod, "prop.ide"), "objs\n12000, saftprop, proptxd, 300, 0\nend");

        var manifest = new AdditionsManifest { GameRootPath = game };
        manifest.Mods.Add(AdditionInstaller.Apply(game, PlanFor(game, mod), "Prop").Recorded);

        var result = AdditionUninstaller.Remove(game, manifest, new[] { "Prop" });

        Assert.Empty(result.Skipped);
        Assert.Equal(1, result.ArchiveEntriesRemoved);
        using var archive = ImgArchive.Open(Path.Combine(game, "models", "gta3.img"));
        Assert.DoesNotContain(archive.Entries, e => e.Name == "saftprop.dff");
    }

    [Fact]
    public void Refuses_to_delete_an_asset_the_user_has_replaced_since_installing()
    {
        var game = BuildGame();
        var manifest = InstallAndRecord(game, ("My Castle", "saftcastle"));

        // Someone installs a different castle over the top afterwards.
        var replacement = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(replacement, "saftcastle.dff"), "A COMPLETELY DIFFERENT MODEL");
        var plan = DirectModInstaller.Plan(game, replacement);
        DirectModInstaller.Apply(plan, backupOutputFolder: null);

        var result = AdditionUninstaller.Remove(game, manifest, new[] { "My Castle" });

        // Deleting it would destroy work SAFT never did, so it stays and is reported.
        Assert.Contains(result.Skipped, s => s.Contains("saftcastle.dff") && s.Contains("changed since"));
        using var archive = ImgArchive.Open(Path.Combine(game, "models", "gta3.img"));
        Assert.Contains(archive.Entries, e => e.Name == "saftcastle.dff");
    }

    /// <summary>
    /// The other half of the test above, and the one that was missing.
    ///
    /// Refusing to delete a changed asset is right. Striking the mod off the record anyway is not:
    /// the asset is still in the archive, and the record is the only thing that knows it is SAFT's.
    /// Remove that and it can never be found again by any version of SAFT, on any platform - the
    /// uninstall reports success and the leftovers are permanent. That is exactly what a real game
    /// was found in: seven orphaned entries and a record reading "Mods": [].
    /// </summary>
    [Fact]
    public void Keeps_a_mod_on_the_record_when_it_could_not_take_all_of_it_out()
    {
        var game = BuildGame();
        var manifest = InstallAndRecord(game, ("My Castle", "saftcastle"));

        var replacement = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(replacement, "saftcastle.dff"), "A COMPLETELY DIFFERENT MODEL");
        DirectModInstaller.Apply(DirectModInstaller.Plan(game, replacement), backupOutputFolder: null);

        var result = AdditionUninstaller.Remove(game, manifest, new[] { "My Castle" });

        // Still on the record, because it is still in the game.
        var record = Assert.Single(manifest.Mods);
        Assert.Equal("My Castle", record.Name);
        Assert.Contains(record.ArchiveEntries, e => e.EntryName == "saftcastle.dff");

        // And NOT reported as removed, because it was not.
        Assert.DoesNotContain("My Castle", result.RemovedMods);

        // What did come out is struck off, so running uninstall again does not try to remove the
        // same map lines a second time.
        Assert.Empty(record.DataLines);
        Assert.Empty(record.ObjectIds);
    }

    [Fact]
    public void Reports_rather_than_fails_when_a_line_was_already_removed_by_hand()
    {
        var game = BuildGame();
        var manifest = InstallAndRecord(game, ("My Castle", "saftcastle"));

        var saftIde = Path.Combine(game, "data", "maps", "saft", AdditionInstaller.SaftIdeFileName);
        File.WriteAllLines(saftIde, File.ReadAllLines(saftIde).Where(l => !l.Contains("saftcastle")));

        var result = AdditionUninstaller.Remove(game, manifest, new[] { "My Castle" });

        Assert.Contains("My Castle", result.RemovedMods);
        Assert.Contains(result.Skipped, s => s.Contains("already gone or had been edited"));
    }

    [Fact]
    public void Frees_the_object_ids_and_drops_the_mod_from_the_manifest()
    {
        var game = BuildGame();
        var manifest = InstallAndRecord(game, ("My Castle", "saftcastle"));
        var installedId = manifest.Mods.Single().ObjectIds.Single();

        var result = AdditionUninstaller.Remove(game, manifest, new[] { "My Castle" });

        Assert.Equal(installedId, Assert.Single(result.FreedObjectIds));
        Assert.Empty(manifest.Mods);

        // And the slot really is free again as far as a fresh scan is concerned.
        Assert.DoesNotContain(installedId, ObjectIdAllocator.ScanUsedIds(game));
    }
}
