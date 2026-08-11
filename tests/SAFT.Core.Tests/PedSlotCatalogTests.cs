using System.Text;
using SAFT.Core;

namespace SAFT.Core.Tests;

public class PedSlotCatalogTests
{
    /// <summary>
    /// A miniature game folder: data/peds.ide plus a models/gta3.img holding whichever entries the
    /// test wants to exist. Rows are written in the real file's shape, tabs and all.
    /// </summary>
    private static string BuildGame(string pedsIdeBody, params string[] archiveEntries)
    {
        var root = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(root, "data"));
        Directory.CreateDirectory(Path.Combine(root, "models"));

        File.WriteAllText(Path.Combine(root, "data", "peds.ide"), pedsIdeBody);

        ImgArchive.Write(
            Path.Combine(root, "models", "gta3.img"),
            archiveEntries
                .Select(name => (name, (Func<Stream>)(() => new MemoryStream(Encoding.ASCII.GetBytes(new string('x', 4096))))))
                .ToList());

        return root;
    }

    private const string TwoPeds = """
        # comment line
        peds
        0, null, generic, PLAYER1, STAT_PLAYER, player, 0, 0, null, 9,9, PED_TYPE_PLAYER, VOICE_PLY_CR, VOICE_PLY_CR
        22, BMYST, BMYST, CIVMALE, STAT_STREET_GUY, gang2, 1983, 1, null, 0,3, PED_TYPE_GEN, VOICE_GEN_BMYST, VOICE_GEN_BMYST
        87, VWFYST1, VWFYST1, CIVFEMALE, STAT_SENSIBLE_GUY, sexywoman, 0, 0, null, 9,9, PED_TYPE_GEN, VOICE_GEN_VWFYST1, VOICE_GEN_VWFYST1
        290, special01, generic, SPECIAL, STAT_SENSIBLE_GUY, man, 0, 0, null, 9,9, PED_TYPE_GEN, VOICE_GEN_MALE01, VOICE_GEN_MALE01
        end
        """;

    [Fact]
    public void Load_reads_id_names_ped_type_and_anim_group()
    {
        var root = BuildGame(TwoPeds, "bmyst.dff", "bmyst.txd", "vwfyst1.dff", "vwfyst1.txd");

        var slots = PedSlotCatalog.Load(root);

        Assert.Equal(new[] { 0, 22, 87, 290 }, slots.Select(s => s.ModelId));

        var bmyst = slots.Single(s => s.ModelId == 22);
        Assert.Equal("BMYST", bmyst.ModelName);
        Assert.Equal("CIVMALE", bmyst.PedType);
        Assert.Equal("gang2", bmyst.AnimGroup);

        Assert.Equal("sexywoman", slots.Single(s => s.ModelId == 87).AnimGroup);
    }

    [Fact]
    public void The_player_slot_and_special_character_placeholders_cannot_host_a_skin()
    {
        var root = BuildGame(TwoPeds, "bmyst.dff", "bmyst.txd", "vwfyst1.dff", "vwfyst1.txd");

        var slots = PedSlotCatalog.Load(root);

        Assert.True(slots.Single(s => s.ModelId == 0).IsPlayerSlot);
        Assert.False(slots.Single(s => s.ModelId == 0).CanHostASkin);

        Assert.True(slots.Single(s => s.ModelId == 290).IsSpecialCharacterSlot);
        Assert.False(slots.Single(s => s.ModelId == 290).CanHostASkin);

        Assert.True(slots.Single(s => s.ModelId == 22).CanHostASkin);
    }

    [Fact]
    public void A_slot_whose_model_is_absent_from_the_archive_cannot_host_a_skin()
    {
        // vwfyst1 is declared in peds.ide but its model was never put in the archive.
        var root = BuildGame(TwoPeds, "bmyst.dff", "bmyst.txd", "vwfyst1.txd");

        var slots = PedSlotCatalog.Load(root);
        var missing = slots.Single(s => s.ModelId == 87);

        Assert.False(missing.ModelInArchive);
        Assert.True(missing.TextureInArchive);
        Assert.False(missing.CanHostASkin);
    }

    /// <summary>
    /// The close-range model is the same name with an "s" in front. It must only be claimed when the
    /// archive really holds it — writing a replacement to an entry that does not exist would silently
    /// do nothing.
    /// </summary>
    [Fact]
    public void High_detail_variant_is_detected_only_when_the_archive_holds_it()
    {
        var withVariant = BuildGame(TwoPeds,
            "bmyst.dff", "bmyst.txd", "sbmyst.dff", "sbmyst.txd", "vwfyst1.dff", "vwfyst1.txd");

        var bmyst = PedSlotCatalog.Load(withVariant).Single(s => s.ModelId == 22);
        Assert.True(bmyst.HasHighDetailVariant);
        Assert.Equal(new[] { "BMYST.dff", "sBMYST.dff" }, bmyst.ModelTargets);
        Assert.Equal(new[] { "BMYST.txd", "sBMYST.txd" }, bmyst.TextureTargets);

        var without = BuildGame(TwoPeds, "bmyst.dff", "bmyst.txd", "vwfyst1.dff", "vwfyst1.txd");

        var plain = PedSlotCatalog.Load(without).Single(s => s.ModelId == 22);
        Assert.False(plain.HasHighDetailVariant);
        Assert.Equal(new[] { "BMYST.dff" }, plain.ModelTargets);
        Assert.Equal(new[] { "BMYST.txd" }, plain.TextureTargets);
    }

    [Fact]
    public void SwapCandidates_puts_slots_with_a_high_detail_variant_first()
    {
        var root = BuildGame(TwoPeds,
            "bmyst.dff", "bmyst.txd", "sbmyst.dff", "sbmyst.txd", "vwfyst1.dff", "vwfyst1.txd");

        var candidates = PedSlotCatalog.SwapCandidates(PedSlotCatalog.Load(root));

        Assert.Equal(new[] { 22, 87 }, candidates.Select(s => s.ModelId));
        Assert.DoesNotContain(candidates, s => s.IsPlayerSlot || s.IsSpecialCharacterSlot);
    }

    [Fact]
    public void AnimGroups_counts_only_slots_that_can_host_a_skin()
    {
        var root = BuildGame(TwoPeds, "bmyst.dff", "bmyst.txd", "vwfyst1.dff", "vwfyst1.txd");

        var groups = PedSlotCatalog.AnimGroups(PedSlotCatalog.Load(root));

        // "player" (CJ) and the special placeholder's "man" are both excluded.
        Assert.Equal(new[] { "gang2", "sexywoman" }, groups.Select(g => g.AnimGroup).OrderBy(g => g));
        Assert.All(groups, g => Assert.Equal(1, g.SlotCount));
    }
}
