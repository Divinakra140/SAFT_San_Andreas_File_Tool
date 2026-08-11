namespace SAFT.Core;

/// <summary>What a RenderWare stream turned out to be, read from its very first chunk.</summary>
public enum RwFileKind
{
    /// <summary>Not a RenderWare stream at all, or too short/damaged to identify.</summary>
    Unknown,

    /// <summary>A .dff — a Clump, the container for a model's geometry and frames.</summary>
    Model,

    /// <summary>A .txd — a Texture Dictionary, the container for a model's textures.</summary>
    TextureDictionary,
}

/// <summary>One texture inside a .txd. <paramref name="MaskName"/> is the separate alpha mask, usually blank.</summary>
public sealed record RwTexture(string Name, string MaskName);

/// <summary>
/// What SAFT could read out of a .dff without needing to understand the geometry itself.
///
/// <see cref="ClumpCount"/> is not always 1. CJ's body parts ship three clumps in one file — measured
/// on this game, torso.dff is 3 clumps of ~41 KB and head.dff is 3 of ~52 KB — which is how the game
/// gives him normal, fat and muscular builds. An ordinary pedestrian model is a single clump.
/// </summary>
public sealed record RwModelInfo(
    int ClumpCount,
    int AtomicCount,
    bool IsSkinned,
    IReadOnlyList<string> TextureNames);

/// <summary>
/// What SAFT could read out of a .txd. <see cref="DeclaredCount"/> is the count the dictionary's own
/// header states; when it disagrees with <see cref="Textures"/> the file is damaged or was written by
/// a tool that got it wrong, and that is worth telling the user rather than working around.
/// </summary>
public sealed record RwTextureDictionaryInfo(int DeclaredCount, IReadOnlyList<RwTexture> Textures)
{
    public bool CountAgrees => DeclaredCount == Textures.Count;
}

/// <summary>
/// A read-only reader for the RenderWare container format that GTA's .dff models and .txd texture
/// dictionaries are both built from. SAFT does not author or edit RenderWare data — it only needs to
/// answer three questions about a file a user is about to install, each of which maps to a specific
/// way skin swaps are known to fail:
///
///   "Is this actually a model / a texture dictionary?"  — catches a renamed or corrupted file before
///   it reaches the archive, where a malformed entry makes the game hang on the loading screen rather
///   than crash with anything legible.
///
///   "Is the geometry skinned?"  — an unskinned (rigid) model loads perfectly and then stands in a
///   T-pose, because there are no bone weights for the animation system to drive.
///
///   "Do the texture names line up?"  — a model asks for its textures by name. If the .txd supplies
///   different names, the model renders untextured white. Nothing in the file formats detects this;
///   comparing the two name sets does.
///
/// FORMAT. A RenderWare stream is a tree of chunks, each a 12-byte header (uint32 type, uint32 body
/// size, uint32 library version) followed by that many bytes of body. Some chunk types hold further
/// chunks; the rest hold opaque data. Walking into opaque data produces convincing nonsense, so this
/// reader descends only through the container types it knows — see <see cref="Containers"/>.
///
/// TRAILING PADDING. Files read out of an IMG archive are sector-aligned, so they carry up to 2047
/// bytes of trailing zeros — wmyst.dff is 91,822 bytes of model inside a 92,160-byte entry. A zero
/// byte run parses as a chunk of type 0 and size 0 and would otherwise loop forever, so a zero-type
/// chunk ends the walk. That is safe because type 0 is not a real RenderWare chunk type.
/// </summary>
public static class RenderWare
{
    private const uint Struct = 0x01;
    private const uint String = 0x02;
    private const uint Extension = 0x03;
    private const uint Texture = 0x06;
    private const uint Material = 0x07;
    private const uint MaterialList = 0x08;
    private const uint FrameList = 0x0E;
    private const uint Geometry = 0x0F;
    private const uint Clump = 0x10;
    private const uint Atomic = 0x14;
    private const uint TextureNative = 0x15;
    private const uint TextureDictionary = 0x16;
    private const uint GeometryList = 0x1A;
    private const uint SkinPlugin = 0x0116;

    private const int ChunkHeaderSize = 12;

    /// <summary>
    /// The chunk types whose body is a sequence of further chunks. Everything not listed here holds
    /// opaque payload — vertex data, pixels, plugin blobs — that must never be walked into.
    /// </summary>
    private static readonly HashSet<uint> Containers = new()
    {
        Clump, TextureDictionary, GeometryList, Geometry, MaterialList,
        Material, Texture, Atomic, Extension, FrameList,
    };

    /// <summary>How deep the chunk tree is allowed to go before the file is treated as malformed.</summary>
    private const int MaxDepth = 16;

    /// <summary>A model realistically sits between these bounds; outside them something is wrong.</summary>
    public const long PlausibleModelMinBytes = 10 * 1024;
    public const long PlausibleModelMaxBytes = 2 * 1024 * 1024;

    private readonly record struct Chunk(uint Type, int BodyOffset, int Size)
    {
        public int End => BodyOffset + Size;
    }

    /// <summary>
    /// The immediate child chunks within a byte range. Stops on the first header that can't be real:
    /// a zero type (sector padding), or a size that would run past the end of the range.
    /// </summary>
    private static List<Chunk> Children(ReadOnlySpan<byte> buffer, int start, int end)
    {
        var results = new List<Chunk>();
        var position = start;

        while (position + ChunkHeaderSize <= end)
        {
            var type = BitConverter.ToUInt32(buffer[position..]);
            var size = BitConverter.ToUInt32(buffer[(position + 4)..]);
            var body = position + ChunkHeaderSize;

            if (type == 0) break;                        // sector padding
            if (size > (uint)(end - body)) break;        // truncated or misparsed

            results.Add(new Chunk(type, body, (int)size));
            position = body + (int)size;
        }

        return results;
    }

    /// <summary>Every chunk of one type at or below a range, descending only through containers.</summary>
    private static void Collect(ReadOnlySpan<byte> buffer, int start, int end, uint wanted, List<Chunk> into, int depth = 0)
    {
        if (depth > MaxDepth) return;

        foreach (var chunk in Children(buffer, start, end))
        {
            if (chunk.Type == wanted) into.Add(chunk);
            if (Containers.Contains(chunk.Type))
                Collect(buffer, chunk.BodyOffset, chunk.End, wanted, into, depth + 1);
        }
    }

    private static List<Chunk> Collect(ReadOnlySpan<byte> buffer, int start, int end, uint wanted)
    {
        var into = new List<Chunk>();
        Collect(buffer, start, end, wanted, into);
        return into;
    }

    /// <summary>A NUL-terminated ASCII string out of a fixed byte range.</summary>
    private static string ReadName(ReadOnlySpan<byte> buffer, int offset, int maxLength)
    {
        if (offset < 0 || offset >= buffer.Length) return string.Empty;

        var available = Math.Min(maxLength, buffer.Length - offset);
        var slice = buffer.Slice(offset, available);
        var terminator = slice.IndexOf((byte)0);
        if (terminator >= 0) slice = slice[..terminator];

        return System.Text.Encoding.Latin1.GetString(slice);
    }

    /// <summary>
    /// What kind of RenderWare file this is, from its first chunk alone. Cheap enough to call on
    /// every file in a mod folder.
    /// </summary>
    public static RwFileKind Identify(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < ChunkHeaderSize) return RwFileKind.Unknown;

        var type = BitConverter.ToUInt32(buffer);
        var size = BitConverter.ToUInt32(buffer[4..]);

        // The declared body has to at least fit in the file, or this is not a RenderWare stream that
        // merely starts with the right four bytes.
        if (size > (uint)(buffer.Length - ChunkHeaderSize)) return RwFileKind.Unknown;

        return type switch
        {
            Clump => RwFileKind.Model,
            TextureDictionary => RwFileKind.TextureDictionary,
            _ => RwFileKind.Unknown,
        };
    }

    public static RwFileKind Identify(string path) => Identify(ReadHead(path));

    /// <summary>
    /// Enough of a file to identify it, without paying to read a 780 KB texture dictionary when all
    /// that's wanted is its first twelve bytes.
    /// </summary>
    private static byte[] ReadHead(string path)
    {
        using var stream = File.OpenRead(path);
        var head = new byte[ChunkHeaderSize];
        var read = stream.Read(head, 0, head.Length);
        return read == head.Length ? head : head[..Math.Max(read, 0)];
    }

    /// <summary>
    /// Reads a .dff. Returns null if the file isn't a Clump at all.
    ///
    /// Texture names come from each Material's Texture chunk, whose first String child is the name the
    /// model will ask the texture dictionary for. Note the game matches these case-insensitively —
    /// wmyst.dff asks for "WMYST" and wmyst.txd provides "WMYST", but that agreement in case is a
    /// convention, not a rule — so callers comparing the two sets must do so case-insensitively.
    /// </summary>
    public static RwModelInfo? ReadModel(ReadOnlySpan<byte> buffer)
    {
        if (Identify(buffer) != RwFileKind.Model) return null;

        var top = Children(buffer, 0, buffer.Length);
        var clumps = top.Where(c => c.Type == Clump).ToList();
        if (clumps.Count == 0) return null;

        var atomics = 0;
        var skinned = false;
        var textureNames = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var clump in clumps)
        {
            atomics += Collect(buffer, clump.BodyOffset, clump.End, Atomic).Count;

            if (!skinned)
                skinned = Collect(buffer, clump.BodyOffset, clump.End, SkinPlugin).Count > 0;

            foreach (var texture in Collect(buffer, clump.BodyOffset, clump.End, Texture))
            {
                // The name is the Texture chunk's first String child; the second, when present, is
                // the alpha mask name and is not what the material is keyed on.
                foreach (var child in Children(buffer, texture.BodyOffset, texture.End))
                {
                    if (child.Type != String) continue;

                    var name = ReadName(buffer, child.BodyOffset, child.Size);
                    if (name.Length > 0 && seen.Add(name)) textureNames.Add(name);
                    break;
                }
            }
        }

        return new RwModelInfo(clumps.Count, atomics, skinned, textureNames);
    }

    public static RwModelInfo? ReadModel(string path) => ReadModel(File.ReadAllBytes(path));

    /// <summary>
    /// Reads a .txd. Returns null if the file isn't a Texture Dictionary at all.
    ///
    /// Layout: the dictionary's Struct holds the texture count as a uint16, then one TextureNative
    /// chunk per texture. Each of those opens with its own Struct laid out as uint32 platform,
    /// uint32 filter/addressing flags, char[32] name, char[32] mask name.
    /// </summary>
    public static RwTextureDictionaryInfo? ReadTextureDictionary(ReadOnlySpan<byte> buffer)
    {
        if (Identify(buffer) != RwFileKind.TextureDictionary) return null;

        var top = Children(buffer, 0, buffer.Length);
        var root = top.FirstOrDefault(c => c.Type == TextureDictionary);
        if (root.Type != TextureDictionary) return null;

        const int nameOffset = 8;      // past platform id and filter flags
        const int nameLength = 32;

        var declared = 0;
        var declaredSeen = false;
        var textures = new List<RwTexture>();

        foreach (var child in Children(buffer, root.BodyOffset, root.End))
        {
            if (child.Type == Struct && !declaredSeen)
            {
                if (child.Size >= sizeof(ushort))
                    declared = BitConverter.ToUInt16(buffer[child.BodyOffset..]);
                declaredSeen = true;
                continue;
            }

            if (child.Type != TextureNative) continue;

            foreach (var inner in Children(buffer, child.BodyOffset, child.End))
            {
                if (inner.Type != Struct) continue;

                textures.Add(new RwTexture(
                    ReadName(buffer, inner.BodyOffset + nameOffset, nameLength),
                    ReadName(buffer, inner.BodyOffset + nameOffset + nameLength, nameLength)));
                break;
            }
        }

        return new RwTextureDictionaryInfo(declared, textures);
    }

    public static RwTextureDictionaryInfo? ReadTextureDictionary(string path) =>
        ReadTextureDictionary(File.ReadAllBytes(path));
}
