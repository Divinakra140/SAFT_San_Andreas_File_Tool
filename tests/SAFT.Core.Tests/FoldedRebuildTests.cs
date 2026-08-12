using System.Text;
using SAFT.Core;

namespace SAFT.Core.Tests;

/// <summary>
/// A mod that both adds assets and replaces an oversized file used to rewrite the same archive twice
/// — models/gta3.img, 940 MB, written out twice in one install. The replacements are now folded into
/// the additions rewrite so it happens once. These pin down that folding them in does not lose them.
/// </summary>
public class FoldedRebuildTests
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
            File_("banshee.dff", "the original banshee"),
            File_("infernus.dff", "the original infernus"),
        });

        File.WriteAllText(Path.Combine(root, "data", "maps", "existing.ide"), """
            objs
            4806, someobject, sometxd, 150, 0
            end
            """);
        File.WriteAllText(Path.Combine(root, "data", "gta.dat"), "IDE data\\maps\\existing.ide\n");
        return root;
    }

    private static string BuildMod()
    {
        var mod = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(mod, "saftcastle.dff"), "a brand new model");
        File.WriteAllText(Path.Combine(mod, "saftcastle.txd"), "its texture");
        File.WriteAllText(Path.Combine(mod, "castle.ide"), "objs\n12000, saftcastle, saftcastletxd, 300, 0\nend");
        File.WriteAllText(Path.Combine(mod, "castle.ipl"),
            "inst\n12000, saftcastle, 0, 2495.5, -1690.25, 14, 0, 0, 0, 1, -1\nend");
        return mod;
    }

    /// <summary>
    /// What the game holds, as the installer sees it — minus <paramref name="pendingRemoval"/>, the
    /// entries whose removal has been handed over and so is still to happen. MainForm does the same
    /// subtraction before its reinstall rescan: those entries are physically still in the archive, and
    /// counting them would make the mod's own assets look like replacements of themselves.
    /// </summary>
    private static AdditionPlan PlanFor(string gameRoot, string modFolder, IEnumerable<string>? pendingRemoval = null)
    {
        var archiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ImgArchive.Open(Path.Combine(gameRoot, "models", "gta3.img")))
            foreach (var e in archive.Entries) archiveNames.Add(e.Name);

        if (pendingRemoval is not null) archiveNames.ExceptWith(pendingRemoval);

        return AdditionScanner.Scan(gameRoot, modFolder, archiveNames.Contains);
    }

    private static string ReadEntry(string archivePath, string entryName)
    {
        using var archive = ImgArchive.Open(archivePath);
        var entry = archive.Entries.Single(e => e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase));
        using var stream = archive.OpenEntry(entry);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return reader.ReadToEnd().TrimEnd('\0');
    }

    [Fact]
    public void AFoldedReplacementLandsInTheArchiveAlongsideTheNewAssets()
    {
        var game = BuildGame();
        var mod = BuildMod();
        var archive = Path.Combine(game, "models", "gta3.img");

        // Stand-in for the file the direct installer would otherwise have rebuilt the archive for.
        var replacementSource = Path.Combine(mod, "banshee.dff");
        File.WriteAllText(replacementSource, "a much larger replacement banshee");

        AdditionInstaller.Apply(game, PlanFor(game, mod), "My Castle", null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["banshee.dff"] = replacementSource,
            });

        // The folded replacement went in...
        Assert.Equal("a much larger replacement banshee", ReadEntry(archive, "banshee.dff"));
        // ...the new asset went in...
        Assert.Equal("a brand new model", ReadEntry(archive, "saftcastle.dff"));
        // ...and the entry nobody touched is untouched.
        Assert.Equal("the original infernus", ReadEntry(archive, "infernus.dff"));
    }

    [Fact]
    public void FoldedReplacementsStillHappenWhenTheModAddsNoAssetsOfItsOwn()
    {
        // The early-out for "nothing to add" must not silently drop handed-over replacements.
        var game = BuildGame();
        var emptyMod = TestScratch.NewDir();
        var archive = Path.Combine(game, "models", "gta3.img");

        var replacementSource = Path.Combine(emptyMod, "banshee.dff");
        File.WriteAllText(replacementSource, "replaced with no additions present");

        AdditionInstaller.Apply(game, PlanFor(game, emptyMod), "Replacements Only", null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["banshee.dff"] = replacementSource,
            });

        Assert.Equal("replaced with no additions present", ReadEntry(archive, "banshee.dff"));
        Assert.Equal("the original infernus", ReadEntry(archive, "infernus.dff"));
    }

    [Fact]
    public void DeferringAnArchiveLeavesItUntouchedButStillReportsIt()
    {
        // What the direct installer does with a deferred archive: no write of its own, but the
        // archive is still reported as handled so the summary is not silently short.
        var game = BuildGame();
        var mod = TestScratch.NewDir();
        var archive = Path.Combine(game, "models", "gta3.img");

        var bigger = Path.Combine(mod, "banshee.dff");
        File.WriteAllText(bigger, new string('x', 5000)); // far larger than the original entry

        var plan = DirectModInstaller.Plan(game, mod);
        Assert.NotEmpty(plan.Matches);

        var result = DirectModInstaller.Apply(
            plan, backupOutputFolder: null, progress: null, onStep: null,
            deferRebuildsFor: new HashSet<string>(
                new[] { AdditionInstaller.DefaultArchiveRelativePath }, StringComparer.OrdinalIgnoreCase));

        Assert.Contains(result.Archives, a =>
            a.ArchiveRelativePath.Equals(AdditionInstaller.DefaultArchiveRelativePath, StringComparison.OrdinalIgnoreCase));

        // Deferred means not written here — the original content is still in place.
        Assert.Equal("the original banshee", ReadEntry(archive, "banshee.dff"));
    }

    // ---------------------------------------------------------------------------------------------
    // The reinstall fold: the same trick from the other side. Removing the previously installed copy
    // rebuilt the archive in full, and then adding the new copy rebuilt it again — 940 MB written
    // twice to swap a handful of entries. The removal now hands over what it would have taken out.
    // ---------------------------------------------------------------------------------------------

    /// <summary>A game whose archive also carries the shared collision bundle, with real records in it.</summary>
    private static string BuildGameWithCollision()
    {
        var root = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(root, "models"));
        Directory.CreateDirectory(Path.Combine(root, "data", "maps"));
        File.WriteAllText(Path.Combine(root, "gta_sa.exe"), "stub");

        var bundle = ColBundleTests.MakeRecord("plc_stinger", payloadBytes: 120)
            .Concat(ColBundleTests.MakeRecord("beachball", payloadBytes: 120)).ToArray();

        ImgArchive.Write(Path.Combine(root, "models", "gta3.img"), new[]
        {
            File_("banshee.dff", "the original banshee"),
            File_("infernus.dff", "the original infernus"),
            (AdditionInstaller.SharedCollisionBundle, (Func<Stream>)(() => new MemoryStream(bundle, writable: false))),
        });

        File.WriteAllText(Path.Combine(root, "data", "gta.dat"), "IDE data\\maps\\existing.ide\n");
        return root;
    }

    /// <summary>A mod folder holding one model, its definition and placement, and optionally its collision.</summary>
    private static string BuildModNamed(string model, string modelContent, byte[]? collision = null, string? extraAsset = null)
    {
        var mod = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(mod, model + ".dff"), modelContent);
        if (extraAsset is not null) File.WriteAllText(Path.Combine(mod, extraAsset + ".dff"), $"content of {extraAsset}");
        if (collision is not null) File.WriteAllBytes(Path.Combine(mod, model + ".col"), collision);

        File.WriteAllText(Path.Combine(mod, model + ".ide"), $"objs\n12000, {model}, {model}txd, 300, 0\nend");
        File.WriteAllText(Path.Combine(mod, model + ".ipl"),
            $"inst\n12000, {model}, 0, 2495.5, -1690.25, 14, 0, 0, 0, 1, -1\nend");
        return mod;
    }

    private static IReadOnlyList<ColRecord> BundleIn(string gameRoot)
    {
        using var archive = ImgArchive.Open(Path.Combine(gameRoot, "models", "gta3.img"));
        var entry = archive.Entries.Single(e =>
            e.Name.Equals(AdditionInstaller.SharedCollisionBundle, StringComparison.OrdinalIgnoreCase));
        using var stream = archive.OpenEntry(entry);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return ColBundle.Read(buffer.ToArray());
    }

    private static IReadOnlySet<string> DeferredArchive =>
        new HashSet<string>(new[] { AdditionInstaller.DefaultArchiveRelativePath }, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void DeferringTheRemovalsRewriteLeavesTheArchiveAloneButStillDoesEverythingElse()
    {
        var game = BuildGameWithCollision();
        var manifest = new AdditionsManifest { GameRootPath = game };
        manifest.Mods.Add(AdditionInstaller.Apply(
            game, PlanFor(game, BuildModNamed("saftcastle", "v1 castle", extraAsset: "saftgate")), "My Castle").Recorded);

        var removal = AdditionUninstaller.Remove(
            game, manifest, new[] { "My Castle" }, null, DeferredArchive);

        // Nothing was rewritten: both of the old copy's entries are still there, untouched.
        var archive = Path.Combine(game, "models", "gta3.img");
        Assert.Equal("v1 castle", ReadEntry(archive, "saftcastle.dff"));
        Assert.Equal("content of saftgate", ReadEntry(archive, "saftgate.dff"));
        Assert.Equal(0, removal.ArchiveEntriesRemoved);

        // ...but it was all worked out and handed back.
        var handed = removal.DeferredEntryRemovals[AdditionInstaller.DefaultArchiveRelativePath];
        Assert.Equal(
            new[] { "saftcastle.dff", "saftgate.dff" },
            handed.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray());

        // And everything that is NOT the archive rewrite still happened, or the deferral would be
        // doing half a removal: the map lines are gone and the mod is out of the manifest.
        Assert.DoesNotContain(
            "saftcastle",
            File.ReadAllText(Path.Combine(game, "data", "maps", "saft", AdditionInstaller.SaftIdeFileName)));
        Assert.Empty(manifest.Mods);
        Assert.Equal("My Castle", Assert.Single(removal.RemovedMods));
    }

    [Fact]
    public void AReinstallDropsTheOldCopyAndAddsTheNewOneInASingleRewrite()
    {
        var game = BuildGameWithCollision();
        var archive = Path.Combine(game, "models", "gta3.img");

        var manifest = new AdditionsManifest { GameRootPath = game };
        manifest.Mods.Add(AdditionInstaller.Apply(
            game, PlanFor(game, BuildModNamed("saftcastle", "v1 castle", extraAsset: "saftgate")), "My Castle").Recorded);

        // The new version of the mod has dropped saftgate.dff and changed the castle.
        var v2 = BuildModNamed("saftcastle", "v2 castle");

        var removal = AdditionUninstaller.Remove(game, manifest, new[] { "My Castle" }, null, DeferredArchive);
        var drops = removal.DeferredEntryRemovals[AdditionInstaller.DefaultArchiveRelativePath];

        var added = AdditionInstaller.Apply(
            game, PlanFor(game, v2, drops), "My Castle", null, null, drops, removal.DeferredCollisionPrunes);

        // The entry the new version no longer ships is gone...
        using (var img = ImgArchive.Open(archive))
            Assert.DoesNotContain(img.Entries, e => e.Name.Equals("saftgate.dff", StringComparison.OrdinalIgnoreCase));

        // ...the one it still ships is present exactly once, with the NEW content...
        using (var img = ImgArchive.Open(archive))
            Assert.Single(img.Entries.Where(e => e.Name.Equals("saftcastle.dff", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("v2 castle", ReadEntry(archive, "saftcastle.dff"));

        // ...the game's own entries are byte-identical...
        Assert.Equal("the original banshee", ReadEntry(archive, "banshee.dff"));
        Assert.Equal("the original infernus", ReadEntry(archive, "infernus.dff"));

        // ...and the new copy is recorded, so it is still uninstallable afterwards.
        Assert.Contains(added.Recorded.ArchiveEntries, e =>
            e.EntryName.Equals("saftcastle.dff", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AReinstallPrunesTheOldCollisionBeforeMergingTheNewSoTheModKeepsIts()
    {
        // The one real hazard in the fold. The old copy's records and the new copy's records have the
        // SAME NAMES, and merging skips a name the bundle already has. Merge first and the new record
        // is skipped as a duplicate, then the prune takes the old one out — leaving a placed object
        // with no collision at all, which crashes San Andreas on world load every time.
        var game = BuildGameWithCollision();

        var manifest = new AdditionsManifest { GameRootPath = game };
        manifest.Mods.Add(AdditionInstaller.Apply(
            game,
            PlanFor(game, BuildModNamed("saftcastle", "v1 castle", ColBundleTests.MakeRecord("saftcastle", payloadBytes: 120))),
            "My Castle").Recorded);

        // Another mod's collision shares the bundle and must survive all of this.
        manifest.Mods.Add(AdditionInstaller.Apply(
            game,
            PlanFor(game, BuildModNamed("safttower", "a tower", ColBundleTests.MakeRecord("safttower", payloadBytes: 120))),
            "Someone Elses Tower").Recorded);

        var newCollision = ColBundleTests.MakeRecord("saftcastle", payloadBytes: 200);   // reshaped in v2
        var v2 = BuildModNamed("saftcastle", "v2 castle", newCollision);

        var removal = AdditionUninstaller.Remove(game, manifest, new[] { "My Castle" }, null, DeferredArchive);
        var drops = removal.DeferredEntryRemovals[AdditionInstaller.DefaultArchiveRelativePath];
        Assert.Equal(
            new[] { "saftcastle" },
            removal.DeferredCollisionPrunes[AdditionInstaller.SharedCollisionBundle].ToArray());

        AdditionInstaller.Apply(
            game, PlanFor(game, v2, drops), "My Castle", null, null, drops, removal.DeferredCollisionPrunes);

        var bundle = BundleIn(game);

        // Rockstar's records, byte for byte.
        Assert.Equal(ColBundleTests.MakeRecord("plc_stinger", payloadBytes: 120),
            bundle.Single(r => r.Name == "plc_stinger").Bytes);
        Assert.Equal(ColBundleTests.MakeRecord("beachball", payloadBytes: 120),
            bundle.Single(r => r.Name == "beachball").Bytes);

        // The other mod's record, untouched.
        Assert.Equal(ColBundleTests.MakeRecord("safttower", payloadBytes: 120),
            bundle.Single(r => r.Name == "safttower").Bytes);

        // And this mod's record present exactly once, in its NEW shape — not the v1 one, and not gone.
        var mine = Assert.Single(bundle.Where(r => r.Name == "saftcastle"));
        Assert.Equal(newCollision, mine.Bytes);
    }

    [Fact]
    public void AHandedOverRemovalHappensEvenWhenTheNewCopyAddsNothingToTheArchive()
    {
        // The early-out that skips the rewrite when there is nothing to add must account for the
        // handed-over removals too, or the old copy is silently left installed — the same trap the
        // folded replacements fell into.
        var game = BuildGameWithCollision();
        var archive = Path.Combine(game, "models", "gta3.img");

        var manifest = new AdditionsManifest { GameRootPath = game };
        manifest.Mods.Add(AdditionInstaller.Apply(
            game, PlanFor(game, BuildModNamed("saftcastle", "v1 castle")), "My Castle").Recorded);

        // v2 is map data only: it places an object the game already has, and ships no assets at all.
        var v2 = TestScratch.NewDir();
        File.WriteAllText(Path.Combine(v2, "castle.ipl"),
            "inst\n4806, someobject, 0, 2495.5, -1690.25, 14, 0, 0, 0, 1, -1\nend");

        var removal = AdditionUninstaller.Remove(game, manifest, new[] { "My Castle" }, null, DeferredArchive);
        var drops = removal.DeferredEntryRemovals[AdditionInstaller.DefaultArchiveRelativePath];

        AdditionInstaller.Apply(
            game, PlanFor(game, v2, drops), "My Castle", null, null, drops, removal.DeferredCollisionPrunes);

        using var img = ImgArchive.Open(archive);
        Assert.DoesNotContain(img.Entries, e => e.Name.Equals("saftcastle.dff", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(img.Entries, e => e.Name.Equals("banshee.dff", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AHandoverThatTurnsOutToChangeNothingDoesNotRewriteTheArchiveAtAll()
    {
        // 940 MB written to produce a byte-identical archive is the most expensive kind of nothing.
        // If the entries handed over for removal have already gone — another tool took them out, or
        // the user did — there is no rewrite to do. The uninstaller has always had this guard on its
        // own rebuild; the folded path needs it too.
        var game = BuildGameWithCollision();
        var archive = Path.Combine(game, "models", "gta3.img");
        var before = File.ReadAllBytes(archive);

        AdditionInstaller.Apply(
            game, PlanFor(game, TestScratch.NewDir()), "Ghost", null, null,
            new HashSet<string>(new[] { "an_entry_that_is_not_there.dff" }, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [AdditionInstaller.SharedCollisionBundle] =
                    new HashSet<string>(new[] { "a_record_that_is_not_there" }, StringComparer.OrdinalIgnoreCase),
            });

        Assert.Equal(before, File.ReadAllBytes(archive));
        Assert.False(File.Exists(archive + ".saft-tmp"));
    }

    [Fact]
    public void AnEntryBeingReplacedInTheSameRewriteIsNotAlsoDropped()
    {
        // Belt and braces for the one case where the two handovers overlap: a name that is both
        // coming out with the old copy and going back in as a replacement. Keeping the file beats
        // losing it — a dropped entry with no route back in is a mod that half-installs.
        var game = BuildGameWithCollision();
        var archive = Path.Combine(game, "models", "gta3.img");

        var manifest = new AdditionsManifest { GameRootPath = game };
        manifest.Mods.Add(AdditionInstaller.Apply(
            game, PlanFor(game, BuildModNamed("saftcastle", "v1 castle")), "My Castle").Recorded);

        var replacement = Path.Combine(TestScratch.NewDir(), "saftcastle.dff");
        File.WriteAllText(replacement, "handed over as a replacement");

        var removal = AdditionUninstaller.Remove(game, manifest, new[] { "My Castle" }, null, DeferredArchive);
        var drops = removal.DeferredEntryRemovals[AdditionInstaller.DefaultArchiveRelativePath];

        AdditionInstaller.Apply(
            game, PlanFor(game, TestScratch.NewDir(), drops), "My Castle", null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["saftcastle.dff"] = replacement },
            drops, removal.DeferredCollisionPrunes);

        Assert.Equal("handed over as a replacement", ReadEntry(archive, "saftcastle.dff"));
    }
}
