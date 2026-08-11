namespace SAFT.Core;

/// <summary>
/// One pedestrian slot the game knows about — a row of data/peds.ide joined against what
/// models/gta3.img actually contains for it.
///
/// A "slot" is the unit a skin swap operates on. Replacing what a slot's model and texture files
/// contain changes what that character looks like everywhere the game uses them, permanently, with
/// no script and no runtime involved.
/// </summary>
public sealed record PedSlot(
    int ModelId,
    string ModelName,
    string TextureName,
    string PedType,
    string AnimGroup,
    bool ModelInArchive,
    bool TextureInArchive,
    string? HighDetailModelName,
    string? HighDetailTextureName,
    long ModelBytes,
    long TextureBytes)
{
    /// <summary>CJ. Model ID 0 is assembled from parts in player.img, not loaded from one file.</summary>
    public bool IsPlayerSlot => ModelId == 0;

    /// <summary>
    /// One of the SPECIAL01..SPECIAL10 placeholders. These hold no model of their own — the script
    /// loads a real character into them by name at runtime — so there is nothing here to re-skin.
    /// </summary>
    public bool IsSpecialCharacterSlot =>
        ModelName.StartsWith("special", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the game ships a separate high-detail model for this ped, used at close range and in
    /// cutscenes. Only 32 of this game's 276 ped rows have one, but a skin that misses it visibly
    /// pops back to the vanilla character when the camera comes close.
    /// </summary>
    public bool HasHighDetailVariant => HighDetailModelName is not null;

    /// <summary>
    /// Whether a plain file swap can re-skin this slot: the game has to actually load its appearance
    /// from a model and a texture that exist in the archive.
    /// </summary>
    public bool CanHostASkin => ModelInArchive && TextureInArchive && !IsPlayerSlot && !IsSpecialCharacterSlot;

    /// <summary>Every archive entry a replacement model must be written to for the swap to hold at all camera ranges.</summary>
    public IReadOnlyList<string> ModelTargets =>
        HighDetailModelName is null
            ? new[] { ModelName + ".dff" }
            : new[] { ModelName + ".dff", HighDetailModelName + ".dff" };

    /// <summary>Every archive entry a replacement texture dictionary must be written to.</summary>
    public IReadOnlyList<string> TextureTargets =>
        HighDetailTextureName is null
            ? new[] { TextureName + ".txd" }
            : new[] { TextureName + ".txd", HighDetailTextureName + ".txd" };
}

/// <summary>
/// Builds the list of pedestrian slots from a game install, by reading data/peds.ide and checking
/// each row against models/gta3.img.
///
/// Read entirely from the install in front of it rather than from a table baked into SAFT, so a game
/// whose peds.ide a previous mod has already changed is described as it actually is. Measured on a
/// stock v3 install: 276 rows spanning model IDs 0-299, of which 265 have both a model and a texture
/// in the archive, 32 have a high-detail variant, and 11 have neither (CJ plus the ten
/// special-character placeholders).
///
/// Nothing here writes to peds.ide. A skin swap changes what the model and texture files contain, not
/// which IDs exist or what they are called — so no ID is ever renumbered, and an existing save, which
/// stores ped references by ID, stays valid.
/// </summary>
public static class PedSlotCatalog
{
    private const string PedsSection = "peds";

    /// <summary>Column positions in a peds.ide row, past the three that <see cref="IdeFile"/> already reads.</summary>
    private const int PedTypeColumn = 3;
    private const int AnimGroupColumn = 5;

    public static string PedsIdePath(string gameRoot) => Path.Combine(gameRoot, "data", "peds.ide");

    public static string PedArchivePath(string gameRoot) => Path.Combine(gameRoot, "models", "gta3.img");

    /// <summary>
    /// Every pedestrian slot in the install, in model-ID order. Throws if peds.ide or gta3.img is
    /// missing, since without both there is no catalog to speak of.
    /// </summary>
    public static IReadOnlyList<PedSlot> Load(string gameRoot)
    {
        var definitions = IdeFile.Parse(PedsIdePath(gameRoot))
            .Where(d => d.Section.Equals(PedsSection, StringComparison.OrdinalIgnoreCase));

        using var archive = ImgArchive.Open(PedArchivePath(gameRoot));
        var entries = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
            entries[entry.Name] = entry.SizeSectors * (long)ImgEntry.SectorSize;

        return definitions.Select(d => Build(d, entries))
            .OrderBy(s => s.ModelId)
            .ToList();
    }

    private static PedSlot Build(IdeDefinition definition, IReadOnlyDictionary<string, long> entries)
    {
        var fields = definition.RawLine.Split(',');

        // The high-detail model the game swaps in at close range is the same name with an "s" in
        // front — wmyst.dff / swmyst.dff. It only counts when the archive really holds it; most peds
        // have no such entry, and inventing one would send a replacement to a name nothing reads.
        var highDetailModel = "s" + definition.ModelName;
        var highDetailTexture = "s" + definition.TextureName;

        return new PedSlot(
            ModelId: definition.ObjectId,
            ModelName: definition.ModelName,
            TextureName: definition.TextureName,
            PedType: Column(fields, PedTypeColumn),
            AnimGroup: Column(fields, AnimGroupColumn),
            ModelInArchive: entries.ContainsKey(definition.ModelName + ".dff"),
            TextureInArchive: entries.ContainsKey(definition.TextureName + ".txd"),
            HighDetailModelName: entries.ContainsKey(highDetailModel + ".dff") ? highDetailModel : null,
            HighDetailTextureName: entries.ContainsKey(highDetailTexture + ".txd") ? highDetailTexture : null,
            ModelBytes: entries.GetValueOrDefault(definition.ModelName + ".dff"),
            TextureBytes: entries.GetValueOrDefault(definition.TextureName + ".txd"));
    }

    private static string Column(string[] fields, int index) =>
        index < fields.Length ? fields[index].Trim() : string.Empty;

    /// <summary>
    /// The slots worth offering as swap targets, ordered so the most useful come first: those with a
    /// high-detail variant last-longest at close range, and a bigger vanilla model means more room
    /// before the archive has to be rebuilt to fit the replacement.
    /// </summary>
    public static IReadOnlyList<PedSlot> SwapCandidates(IEnumerable<PedSlot> slots) =>
        slots.Where(s => s.CanHostASkin)
            .OrderByDescending(s => s.HasHighDetailVariant)
            .ThenByDescending(s => s.ModelBytes)
            .ToList();

    /// <summary>
    /// The animation groups present across the catalog, each with how many slots use it. A skin
    /// inherits the walk and idle animations of whatever slot it is installed into — putting a male
    /// model into a "sexywoman" slot gives it that slot's hip-swaying walk — so the group is part of
    /// choosing a slot, not an afterthought.
    /// </summary>
    public static IReadOnlyList<(string AnimGroup, int SlotCount)> AnimGroups(IEnumerable<PedSlot> slots) =>
        slots.Where(s => s.CanHostASkin && s.AnimGroup.Length > 0)
            .GroupBy(s => s.AnimGroup, StringComparer.OrdinalIgnoreCase)
            .Select(g => (AnimGroup: g.Key, SlotCount: g.Count()))
            .OrderByDescending(g => g.SlotCount)
            .ThenBy(g => g.AnimGroup, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
