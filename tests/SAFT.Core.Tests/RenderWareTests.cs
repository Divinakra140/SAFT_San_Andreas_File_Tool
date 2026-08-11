using SAFT.Core;

namespace SAFT.Core.Tests;

/// <summary>
/// Synthetic RenderWare streams, built to the same layout measured in the real game files, so these
/// run anywhere without needing a copy of San Andreas present.
/// </summary>
public class RenderWareTests
{
    private const uint Struct = 0x01;
    private const uint String_ = 0x02;
    private const uint Extension = 0x03;
    private const uint Texture = 0x06;
    private const uint Material = 0x07;
    private const uint MaterialList = 0x08;
    private const uint Geometry = 0x0F;
    private const uint Clump = 0x10;
    private const uint Atomic = 0x14;
    private const uint TextureNative = 0x15;
    private const uint TextureDictionary = 0x16;
    private const uint GeometryList = 0x1A;
    private const uint SkinPlugin = 0x0116;

    /// <summary>One chunk: 12-byte header (type, body size, library version) then the body.</summary>
    private static byte[] Chunk(uint type, params byte[][] body)
    {
        var payload = body.SelectMany(b => b).ToArray();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(type);
        w.Write((uint)payload.Length);
        w.Write(0x1803FFFFu); // the library version every file in this game carries
        w.Write(payload);
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] Raw(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    private static byte[] Bytes(int count) => new byte[count];
    private static byte[] U16(ushort v) => BitConverter.GetBytes(v);

    /// <summary>A NUL-terminated name padded out to a fixed field width.</summary>
    private static byte[] Fixed(string s, int width)
    {
        var buffer = new byte[width];
        var ascii = System.Text.Encoding.Latin1.GetBytes(s);
        Array.Copy(ascii, buffer, Math.Min(ascii.Length, width - 1));
        return buffer;
    }

    private static byte[] RwString(string s) =>
        Chunk(String_, Fixed(s, ((s.Length + 4) / 4) * 4));

    /// <summary>A material that asks for one named texture.</summary>
    private static byte[] MaterialWith(string textureName) =>
        Chunk(Material,
            Chunk(Struct, Bytes(28)),
            Chunk(Texture,
                Chunk(Struct, Bytes(4)),
                RwString(textureName),
                RwString(string.Empty),
                Chunk(Extension)));

    private static byte[] BuildModel(bool skinned, params string[] textureNames)
    {
        var materials = textureNames.Select(MaterialWith).ToArray();
        var geometryExtension = skinned
            ? Chunk(Extension, Chunk(SkinPlugin, Bytes(64)))
            : Chunk(Extension);

        return Chunk(Clump,
            Chunk(Struct, Bytes(12)),
            Chunk(GeometryList,
                Chunk(Struct, Bytes(4)),
                Chunk(Geometry,
                    Chunk(Struct, Bytes(40)),
                    Chunk(MaterialList, Raw(new[] { Chunk(Struct, Bytes(8)) }.Concat(materials).ToArray())),
                    geometryExtension)),
            Chunk(Atomic, Chunk(Struct, Bytes(16)), Chunk(Extension)),
            Chunk(Extension));
    }

    private static byte[] BuildTextureDictionary(params string[] names)
    {
        var natives = names.Select(n => Chunk(TextureNative,
            Chunk(Struct,
                Bytes(8),               // platform id + filter/addressing flags
                Fixed(n, 32),           // texture name
                Fixed(string.Empty, 32),// alpha mask name
                Bytes(16)),             // raster format and friends
            Chunk(Extension))).ToArray();

        return Chunk(TextureDictionary,
            Raw(new[] { Chunk(Struct, Raw(U16((ushort)names.Length), U16(0))) }
                .Concat(natives)
                .Append(Chunk(Extension))
                .ToArray()));
    }

    [Fact]
    public void Identify_tells_models_from_texture_dictionaries()
    {
        Assert.Equal(RwFileKind.Model, RenderWare.Identify(BuildModel(skinned: true, "skin")));
        Assert.Equal(RwFileKind.TextureDictionary, RenderWare.Identify(BuildTextureDictionary("skin")));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3 })]
    public void Identify_rejects_files_too_short_to_hold_a_chunk_header(byte[] content) =>
        Assert.Equal(RwFileKind.Unknown, RenderWare.Identify(content));

    [Fact]
    public void Identify_rejects_a_chunk_claiming_more_body_than_the_file_holds()
    {
        var truncated = BuildModel(skinned: true, "skin")[..40];
        Assert.Equal(RwFileKind.Unknown, RenderWare.Identify(truncated));
    }

    [Fact]
    public void ReadModel_reports_skinned_geometry()
    {
        Assert.True(RenderWare.ReadModel(BuildModel(skinned: true, "skin"))!.IsSkinned);
        Assert.False(RenderWare.ReadModel(BuildModel(skinned: false, "skin"))!.IsSkinned);
    }

    [Fact]
    public void ReadModel_lists_referenced_texture_names_without_duplicates()
    {
        var info = RenderWare.ReadModel(BuildModel(skinned: true, "body", "head", "body"))!;

        Assert.Equal(new[] { "body", "head" }, info.TextureNames);
        Assert.Equal(1, info.ClumpCount);
        Assert.Equal(1, info.AtomicCount);
    }

    [Fact]
    public void ReadModel_returns_null_for_a_texture_dictionary() =>
        Assert.Null(RenderWare.ReadModel(BuildTextureDictionary("skin")));

    /// <summary>
    /// CJ's body parts are three clumps in one file (measured: torso.dff is 3 x ~41 KB), which is how
    /// the game gives him normal, fat and muscular builds. Reading only the first would under-report
    /// both the geometry and the textures such a file needs.
    /// </summary>
    [Fact]
    public void ReadModel_reads_every_clump_in_a_multi_clump_file()
    {
        var threeClumps = Raw(
            BuildModel(skinned: true, "torso"),
            BuildModel(skinned: true, "torso_fat"),
            BuildModel(skinned: true, "torso_ripped"));

        var info = RenderWare.ReadModel(threeClumps)!;

        Assert.Equal(3, info.ClumpCount);
        Assert.Equal(3, info.AtomicCount);
        Assert.Equal(new[] { "torso", "torso_fat", "torso_ripped" }, info.TextureNames);
    }

    /// <summary>
    /// Anything read out of an IMG archive arrives sector-aligned, so it carries trailing zeros —
    /// wmyst.dff is 91,822 bytes of model inside a 92,160-byte entry. Those zeros parse as a chunk of
    /// type 0 and size 0, which has to end the walk rather than spin on it.
    /// </summary>
    [Fact]
    public void Trailing_sector_padding_is_ignored()
    {
        var model = BuildModel(skinned: true, "body");
        var padded = Raw(model, Bytes(2048 - (model.Length % 2048)));

        Assert.Equal(0, padded.Length % 2048);
        Assert.Equal(RwFileKind.Model, RenderWare.Identify(padded));

        var info = RenderWare.ReadModel(padded)!;
        Assert.Equal(1, info.ClumpCount);
        Assert.Equal(new[] { "body" }, info.TextureNames);
    }

    [Fact]
    public void ReadTextureDictionary_lists_names_and_agrees_with_the_declared_count()
    {
        var info = RenderWare.ReadTextureDictionary(
            BuildTextureDictionary("torso", "torso_fat", "torso_ripped"))!;

        Assert.Equal(3, info.DeclaredCount);
        Assert.True(info.CountAgrees);
        Assert.Equal(new[] { "torso", "torso_fat", "torso_ripped" }, info.Textures.Select(t => t.Name));
    }

    [Fact]
    public void ReadTextureDictionary_reports_disagreement_with_a_wrong_declared_count()
    {
        // A dictionary whose header lies about how many textures follow is damaged; SAFT should say
        // so rather than quietly trusting either number.
        var dictionary = BuildTextureDictionary("a", "b");
        var declaredAt = 12 + 12; // root header, then its Struct's header
        BitConverter.GetBytes((ushort)7).CopyTo(dictionary, declaredAt);

        var info = RenderWare.ReadTextureDictionary(dictionary)!;

        Assert.Equal(7, info.DeclaredCount);
        Assert.Equal(2, info.Textures.Count);
        Assert.False(info.CountAgrees);
    }

    [Fact]
    public void ReadTextureDictionary_returns_null_for_a_model() =>
        Assert.Null(RenderWare.ReadTextureDictionary(BuildModel(skinned: true, "skin")));

    /// <summary>
    /// The white-model failure: a model asking for texture names the dictionary does not supply. The
    /// game matches these case-insensitively, so a case difference alone is not a mismatch.
    /// </summary>
    [Fact]
    public void Model_and_dictionary_names_can_be_compared_case_insensitively()
    {
        var model = RenderWare.ReadModel(BuildModel(skinned: true, "WMYST"))!;
        var txd = RenderWare.ReadTextureDictionary(BuildTextureDictionary("wmyst"))!;

        var missing = model.TextureNames
            .Except(txd.Textures.Select(t => t.Name), StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Empty(missing);
    }
}
