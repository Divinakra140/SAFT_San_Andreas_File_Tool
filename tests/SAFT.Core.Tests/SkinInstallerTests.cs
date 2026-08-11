using System.Text;
using SAFT.Core;

namespace SAFT.Core.Tests;

public class SkinInstallerTests
{
    // Minimal well-formed RenderWare streams, same layout as the game's own files.
    private static byte[] Chunk(uint type, params byte[][] body)
    {
        var payload = body.SelectMany(b => b).ToArray();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(type); w.Write((uint)payload.Length); w.Write(0x1803FFFFu); w.Write(payload);
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] Fixed(string s, int width)
    {
        var buffer = new byte[width];
        Array.Copy(Encoding.Latin1.GetBytes(s), buffer, Math.Min(s.Length, width - 1));
        return buffer;
    }

    private static byte[] Model(bool skinned, string textureName, int padTo = 0)
    {
        var texture = Chunk(0x06, Chunk(0x01, new byte[4]), Chunk(0x02, Fixed(textureName, 16)));
        var extension = skinned ? Chunk(0x03, Chunk(0x0116, new byte[64])) : Chunk(0x03);
        var clump = Chunk(0x10,
            Chunk(0x01, new byte[12]),
            Chunk(0x1A, Chunk(0x01, new byte[4]),
                Chunk(0x0F, Chunk(0x01, new byte[40]),
                    Chunk(0x08, Chunk(0x01, new byte[8]), Chunk(0x07, Chunk(0x01, new byte[28]), texture)),
                    extension)),
            Chunk(0x14, Chunk(0x01, new byte[16]), Chunk(0x03)),
            Chunk(0x03));

        return padTo > clump.Length ? clump.Concat(new byte[padTo - clump.Length]).ToArray() : clump;
    }

    private static byte[] Dictionary(params string[] names)
    {
        var natives = names.Select(n => Chunk(0x15,
            Chunk(0x01, new byte[8], Fixed(n, 32), Fixed("", 32), new byte[16]), Chunk(0x03)));

        return Chunk(0x16, new[] { Chunk(0x01, BitConverter.GetBytes((ushort)names.Length), new byte[2]) }
            .Concat(natives).Append(Chunk(0x03)).SelectMany(b => b).ToArray());
    }

    private const string PedsIde = """
        peds
        0, null, generic, PLAYER1, STAT_PLAYER, player, 0, 0, null, 9,9, PED_TYPE_PLAYER, VOICE_PLY_CR, VOICE_PLY_CR
        101, WMYST, WMYST, CIVMALE, STAT_SENSIBLE_GUY, man, 0, 0, null, 9,9, PED_TYPE_GEN, VOICE_A, VOICE_A
        290, special01, generic, SPECIAL, STAT_SENSIBLE_GUY, man, 0, 0, null, 9,9, PED_TYPE_GEN, VOICE_B, VOICE_B
        end
        """;

    /// <summary>A game whose WMYST slot has both the ordinary and the close-range model, each sized in sectors.</summary>
    private static string BuildGame(int vanillaBytes = 8192)
    {
        var root = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(root, "data"));
        Directory.CreateDirectory(Path.Combine(root, "models"));
        File.WriteAllText(Path.Combine(root, "data", "peds.ide"), PedsIde);

        var vanilla = () => (Stream)new MemoryStream(Model(true, "WMYST", vanillaBytes));
        ImgArchive.Write(Path.Combine(root, "models", "gta3.img"), new (string, Func<Stream>)[]
        {
            ("WMYST.dff", vanilla), ("WMYST.txd", () => new MemoryStream(Dictionary("WMYST"))),
            ("sWMYST.dff", vanilla), ("sWMYST.txd", () => new MemoryStream(Dictionary("WMYST"))),
        });

        return root;
    }

    private static SkinSource WriteSkin(string dir, byte[] model, byte[] textures)
    {
        var modelPath = Path.Combine(dir, "custom.dff");
        var texturePath = Path.Combine(dir, "custom.txd");
        File.WriteAllBytes(modelPath, model);
        File.WriteAllBytes(texturePath, textures);
        return new SkinSource(modelPath, texturePath);
    }

    [Fact]
    public void Inspect_accepts_a_well_formed_skinned_model_whose_textures_line_up()
    {
        var dir = TestScratch.NewDir();
        var source = WriteSkin(dir, Model(skinned: true, "link_body", padTo: 40_000), Dictionary("link_body"));

        var inspection = SkinInstaller.Inspect(source);

        Assert.True(inspection.CanInstall);
        Assert.Empty(inspection.Issues);
        Assert.True(inspection.Model!.IsSkinned);
    }

    [Fact]
    public void Inspect_blocks_a_file_that_is_not_really_a_model()
    {
        var dir = TestScratch.NewDir();
        var source = WriteSkin(dir, Encoding.ASCII.GetBytes("this is a png, renamed"), Dictionary("body"));

        var inspection = SkinInstaller.Inspect(source);

        Assert.False(inspection.CanInstall);
        Assert.Contains(inspection.Blocking, i => i.Message.Contains("not a .dff"));
    }

    [Fact]
    public void Inspect_blocks_a_model_supplied_where_a_texture_dictionary_belongs()
    {
        var dir = TestScratch.NewDir();
        var source = WriteSkin(dir, Model(true, "body", 40_000), Model(true, "body", 40_000));

        Assert.Contains(SkinInstaller.Inspect(source).Blocking, i => i.Message.Contains("not a .txd"));
    }

    /// <summary>An unskinned model is installable — it just stands in a T-pose, which is the user's call.</summary>
    [Fact]
    public void Inspect_warns_but_does_not_block_an_unskinned_model()
    {
        var dir = TestScratch.NewDir();
        var source = WriteSkin(dir, Model(skinned: false, "body", 40_000), Dictionary("body"));

        var inspection = SkinInstaller.Inspect(source);

        Assert.True(inspection.CanInstall);
        Assert.Contains(inspection.Warnings, i => i.Message.Contains("T-pose"));
    }

    [Fact]
    public void Inspect_warns_when_the_model_asks_for_textures_the_dictionary_lacks()
    {
        var dir = TestScratch.NewDir();
        var source = WriteSkin(dir, Model(true, "link_body", 40_000), Dictionary("something_else"));

        var inspection = SkinInstaller.Inspect(source);

        Assert.True(inspection.CanInstall);
        Assert.Contains(inspection.Warnings, i => i.Message.Contains("'link_body'") && i.Message.Contains("white"));
    }

    [Fact]
    public void Inspect_matches_texture_names_case_insensitively()
    {
        var dir = TestScratch.NewDir();
        var source = WriteSkin(dir, Model(true, "LINK_BODY", 40_000), Dictionary("link_body"));

        Assert.DoesNotContain(SkinInstaller.Inspect(source).Warnings, i => i.Message.Contains("white"));
    }

    [Fact]
    public void Inspect_warns_about_a_model_far_too_small_to_be_a_body()
    {
        var dir = TestScratch.NewDir();
        var source = WriteSkin(dir, Model(true, "body"), Dictionary("body"));

        Assert.Contains(SkinInstaller.Inspect(source).Warnings, i => i.Message.Contains("skeleton or a prop"));
    }

    /// <summary>The close-range model has to be written too, or the skin reverts when the camera comes near.</summary>
    [Fact]
    public void Plan_targets_both_the_ordinary_and_the_close_range_entries()
    {
        var root = BuildGame();
        var slot = PedSlotCatalog.Load(root).Single(s => s.ModelId == 101);
        var source = WriteSkin(TestScratch.NewDir(), Model(true, "body", 4096), Dictionary("body"));

        var plan = SkinInstaller.Plan(root, slot, source);

        Assert.Equal(
            new[] { "WMYST.dff", "sWMYST.dff", "WMYST.txd", "sWMYST.txd" },
            plan.Matches.Select(m => m.EntryName));

        Assert.Equal(2, plan.Matches.Count(m => m.ModFilePath == source.ModelPath));
        Assert.Equal(2, plan.Matches.Count(m => m.ModFilePath == source.TexturePath));
    }

    [Fact]
    public void Plan_flags_a_rebuild_only_when_the_replacement_outgrows_its_slot()
    {
        var root = BuildGame(vanillaBytes: 8192);
        var slot = PedSlotCatalog.Load(root).Single(s => s.ModelId == 101);
        var small = WriteSkin(TestScratch.NewDir(), Model(true, "body", 4096), Dictionary("body"));
        var large = WriteSkin(TestScratch.NewDir(), Model(true, "body", 40_000), Dictionary("body"));

        Assert.False(SkinInstaller.Plan(root, slot, small).AnyArchiveNeedsRebuild);
        Assert.True(SkinInstaller.Plan(root, slot, large).AnyArchiveNeedsRebuild);
    }

    [Theory]
    [InlineData(0)]    // CJ
    [InlineData(290)]  // special-character placeholder
    public void Plan_refuses_slots_that_cannot_host_a_skin(int modelId)
    {
        var root = BuildGame();
        var slot = PedSlotCatalog.Load(root).Single(s => s.ModelId == modelId);
        var source = WriteSkin(TestScratch.NewDir(), Model(true, "body", 4096), Dictionary("body"));

        Assert.Throws<InvalidOperationException>(() => SkinInstaller.Plan(root, slot, source));
    }

    [Fact]
    public void Apply_replaces_every_target_entry_and_backs_up_the_originals()
    {
        var root = BuildGame();
        var slot = PedSlotCatalog.Load(root).Single(s => s.ModelId == 101);
        var custom = Model(true, "link_body", 4096);
        var source = WriteSkin(TestScratch.NewDir(), custom, Dictionary("link_body"));
        var backups = TestScratch.NewDir();

        SkinInstaller.Apply(SkinInstaller.Plan(root, slot, source), backups);

        using var archive = ImgArchive.Open(Path.Combine(root, "models", "gta3.img"));
        foreach (var name in new[] { "WMYST.dff", "sWMYST.dff" })
        {
            var entry = archive.Entries.Single(e => e.Name == name);
            using var stream = archive.OpenEntry(entry);
            var installed = new byte[custom.Length];
            stream.ReadExactly(installed);
            Assert.Equal(custom, installed);

            // and the model now names the texture the new dictionary provides
            Assert.Equal(new[] { "link_body" }, RenderWare.ReadModel(installed)!.TextureNames);
        }

        Assert.True(Directory.EnumerateFiles(backups, "*", SearchOption.AllDirectories).Any(),
            "the originals must be backed up before anything is overwritten");
    }

    [Fact]
    public void Apply_requires_a_backup_folder()
    {
        var root = BuildGame();
        var slot = PedSlotCatalog.Load(root).Single(s => s.ModelId == 101);
        var source = WriteSkin(TestScratch.NewDir(), Model(true, "body", 4096), Dictionary("body"));
        var plan = SkinInstaller.Plan(root, slot, source);

        Assert.Throws<ArgumentException>(() => SkinInstaller.Apply(plan, "   "));
    }

    /// <summary>
    /// The swap must leave the ped tables exactly as it found them — that is what keeps an existing
    /// save valid, since a save stores ped references by model ID.
    /// </summary>
    [Fact]
    public void Apply_never_touches_peds_ide()
    {
        var root = BuildGame();
        var pedsIde = Path.Combine(root, "data", "peds.ide");
        var before = File.ReadAllBytes(pedsIde);

        var slot = PedSlotCatalog.Load(root).Single(s => s.ModelId == 101);
        var source = WriteSkin(TestScratch.NewDir(), Model(true, "body", 40_000), Dictionary("body"));

        SkinInstaller.Apply(SkinInstaller.Plan(root, slot, source), TestScratch.NewDir());

        Assert.Equal(before, File.ReadAllBytes(pedsIde));
    }
}
