using System.Text;
using SAFT.Core;

namespace SAFT.Core.Tests;

/// <summary>
/// The promise, tested from end to end: install and uninstall as many times as you like, in any
/// order, on either platform, and the game comes back to what it was. Backups hold vanilla files and
/// only vanilla files.
///
/// Every other test here checks one step. These check the loop, because the failure that prompted
/// them needed three rounds to show itself: one uninstall left an added asset in the archive, the
/// next install mistook that leftover for a stock file and filed a copy of the MOD in the backup
/// folder as though it were the original, and from then on every uninstall faithfully restored the
/// mod into the game. Each step behaved reasonably on its own.
/// </summary>
public class InstallUninstallCycleTests
{
    private static (string Name, Func<Stream> OpenContent) File_(string name, string content) =>
        (name, () => new MemoryStream(Encoding.ASCII.GetBytes(content)));

    private static string BuildGame()
    {
        var root = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(root, "models"));
        Directory.CreateDirectory(Path.Combine(root, "data", "maps"));
        File.WriteAllText(Path.Combine(root, "gta_sa.exe"), "stub");
        ImgArchive.Write(Path.Combine(root, "models", "gta3.img"), new[]
        {
            File_("banshee.dff", "vanilla car model"),
            File_("untouched.col", "never changes"),
        });
        File.WriteAllText(Path.Combine(root, "data", "maps", "existing.ide"), "objs\n4806, someobject, sometxd, 150, 0\nend");
        File.WriteAllText(Path.Combine(root, "data", "gta.dat"), "IDE data\\maps\\existing.ide\n");
        return root;
    }

    /// <summary>A mod that both replaces a stock file and adds one of its own — the ordinary shape.</summary>
    private static string BuildMod()
    {
        var mod = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(mod, "banshee.dff"), "modded car model");
        File.WriteAllText(Path.Combine(mod, "castle.dff"), "an added castle");
        File.WriteAllText(Path.Combine(mod, "castle.ide"), "objs\n12000, castle, castletxd, 300, 0\nend");
        File.WriteAllText(Path.Combine(mod, "castle.ipl"),
            "inst\n12000, castle, 0, 2495.5, -1690.25, 14, 0, 0, 0, 1, -1\nend");
        return mod;
    }

    private static IReadOnlyList<string> EntryNames(string game)
    {
        using var img = ImgArchive.Open(Path.Combine(game, "models", "gta3.img"));
        return img.Entries.Select(e => e.Name).ToList();
    }

    private static string ReadEntry(string game, string name)
    {
        using var img = ImgArchive.Open(Path.Combine(game, "models", "gta3.img"));
        using var entry = img.OpenEntry(img.Entries.First(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        using var reader = new StreamReader(entry);
        return reader.ReadToEnd().TrimEnd('\0');
    }

    /// <summary>The install as both shipping builds do it: replacements and their backups, then additions.</summary>
    private static void Install(string game, string mod, string backups)
    {
        var plan = DirectModInstaller.Plan(game, mod);

        var existing = new HashSet<string>(EntryNames(game), StringComparer.OrdinalIgnoreCase);
        var additions = AdditionScanner.Scan(game, mod, existing.Contains);

        // What the mod adds is never a replacement, so it never gets backed up as one.
        DirectModInstaller.Apply(plan.Without(additions.AssetFileNames), backups);

        var manifest = AdditionsManifest.Load(backups) ?? new AdditionsManifest { GameRootPath = game };
        manifest.Mods.Add(AdditionInstaller.Apply(game, additions, "Test Mod").Recorded);
        manifest.Save(backups);
    }

    /// <summary>The uninstall: added objects out first, then the originals back, then the space back.</summary>
    private static void Uninstall(string game, string backups)
    {
        var manifest = AdditionsManifest.Load(backups);
        if (manifest is not null && manifest.Mods.Count > 0)
        {
            AdditionUninstaller.Remove(game, manifest, manifest.Mods.Select(m => m.Name).ToList());
            manifest.Save(backups);
        }

        DirectModInstaller.Apply(DirectModInstaller.Plan(game, backups), backupOutputFolder: null);
        ImgArchiveEditor.Compact(Path.Combine(game, "models", "gta3.img"));
    }

    /// <summary>
    /// Three rounds, because that is what it took in the field. One round passes either way.
    /// </summary>
    [Fact]
    public void The_game_comes_back_to_vanilla_however_many_times_it_is_installed_and_uninstalled()
    {
        var game = BuildGame();
        var backups = TestScratch.NewDir();
        var vanilla = EntryNames(game).OrderBy(n => n).ToList();

        for (var round = 1; round <= 3; round++)
        {
            Install(game, BuildMod(), backups);

            Assert.Contains("castle.dff", EntryNames(game));
            Assert.Equal("modded car model", ReadEntry(game, "banshee.dff"));

            Uninstall(game, backups);

            Assert.Equal("vanilla car model", ReadEntry(game, "banshee.dff"));
            Assert.Equal(vanilla, EntryNames(game).OrderBy(n => n).ToList());
        }
    }

    /// <summary>
    /// The backup folder is the user's last line of defence, so nothing but a genuine stock file may
    /// ever land in it. An asset the mod ADDS has no stock counterpart, and a copy of one filed here
    /// is a copy of the mod wearing the label "original".
    /// </summary>
    [Fact]
    public void The_backup_folder_never_collects_a_file_the_mod_added()
    {
        var game = BuildGame();
        var backups = TestScratch.NewDir();

        for (var round = 1; round <= 3; round++)
        {
            Install(game, BuildMod(), backups);
            Uninstall(game, backups);

            var backedUp = Directory.Exists(backups)
                ? Directory.GetFiles(backups, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList()
                : new List<string?>();

            Assert.DoesNotContain("castle.dff", backedUp);
        }
    }

    /// <summary>
    /// An asset left in the archive by an earlier round is still the mod's, not the game's. Recognising
    /// that is what stops one missed removal turning into a mod file filed as a vanilla original.
    /// </summary>
    [Fact]
    public void An_asset_left_behind_by_an_earlier_round_is_not_mistaken_for_a_stock_file()
    {
        var game = BuildGame();
        var backups = TestScratch.NewDir();

        // Exactly the state the field failure started from: the asset is still in the archive, and
        // the record no longer mentions it.
        Install(game, BuildMod(), backups);
        var orphaned = AdditionsManifest.Load(backups)!;
        orphaned.Mods.Clear();
        orphaned.Save(backups);

        Install(game, BuildMod(), backups);

        // The field failure in one line: the leftover read as a stock file, so a copy of the MOD was
        // filed in the backup folder as the original. Everything after that followed from it.
        var backedUp = Directory.GetFiles(backups, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList();
        Assert.DoesNotContain("castle.dff", backedUp);

        // Claiming the leftover as an addition must not staple a SECOND copy into the archive next
        // to it. Two entries under one name is a corrupt directory table, not a tidy-up.
        Assert.Equal(1, EntryNames(game).Count(n => n.Equals("castle.dff", StringComparison.OrdinalIgnoreCase)));

        Uninstall(game, backups);

        Assert.DoesNotContain("castle.dff", EntryNames(game));
    }
    /// <summary>
    /// The same install, done the way both shipping builds actually do it: one folded rewrite.
    ///
    /// When a mod adds assets AND replaces files in the same archive, neither pass rebuilds a 940 MB
    /// file on its own — the replacements are handed to the addition installer and both happen in one
    /// rewrite. That fold reads the replacement plan, so it has to read the plan the mod's own assets
    /// have already been taken out of.
    ///
    /// Reading the unfiltered one put a leftover in as a replacement AND as an addition, and the
    /// rewrite checks replacements before removals — so it kept the old copy and appended the new one
    /// beside it. Seven leftovers came out of a real install as fourteen entries under seven names,
    /// which the tests above could not see because they never folded.
    /// </summary>
    [Fact]
    public void Folding_the_replacements_into_the_additions_rewrite_does_not_double_the_mods_own_assets()
    {
        var game = BuildGame();
        var backups = TestScratch.NewDir();

        // The state the field failure started from: the asset is in the archive, the record is not.
        Install(game, BuildMod(), backups);
        var orphaned = AdditionsManifest.Load(backups)!;
        orphaned.Mods.Clear();
        orphaned.Save(backups);

        var mod = BuildMod();
        var plan = DirectModInstaller.Plan(game, mod);
        var existing = new HashSet<string>(EntryNames(game), StringComparer.OrdinalIgnoreCase);
        var additions = AdditionScanner.Scan(game, mod, existing.Contains);
        var toApply = plan.Without(additions.AssetFileNames);

        // The fold, exactly as the builds do it - and off the FILTERED plan.
        var archive = AdditionInstaller.DefaultArchiveRelativePath;
        var folded = toApply.Matches
            .Where(m => m.ArchiveRelativePath.Equals(archive, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(m => m.EntryName, m => m.ModFilePath, StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("castle.dff", folded.Keys);

        DirectModInstaller.Apply(
            toApply, backups, deferRebuildsFor: new HashSet<string>(new[] { archive }, StringComparer.OrdinalIgnoreCase));

        var manifest = AdditionsManifest.Load(backups) ?? new AdditionsManifest { GameRootPath = game };
        manifest.Mods.Add(AdditionInstaller.Apply(game, additions, "Test Mod", archiveReplacements: folded).Recorded);
        manifest.Save(backups);

        Assert.Equal(1, EntryNames(game).Count(n => n.Equals("castle.dff", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("modded car model", ReadEntry(game, "banshee.dff"));

        Uninstall(game, backups);

        Assert.DoesNotContain("castle.dff", EntryNames(game));
        Assert.Equal("vanilla car model", ReadEntry(game, "banshee.dff"));
    }
}
