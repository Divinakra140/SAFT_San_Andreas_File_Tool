namespace SAFT.Core;

/// <summary>
/// One collision model inside a .col bundle. <see cref="Bytes"/> is the complete record including
/// its header, kept verbatim so a record can be moved between bundles without being re-encoded.
/// </summary>
public sealed record ColRecord(string Name, byte[] Bytes);

/// <summary>
/// Reads and writes San Andreas .col collision bundles.
///
/// A bundle is simply a run of self-describing records — four-byte tag, a size, a 22-byte name,
/// then the geometry — concatenated with no directory table. That makes adding one a matter of
/// appending, which is why collision turned out to be far less work than it first appeared.
///
/// The game matches collision to a model BY NAME, not by ID: in a stock install the record named
/// BTOLAND8_LAS carries model id 4766 while the .ide defines that same model as 4806. So a record's
/// name is the only field that has to line up with anything, and SAFT never needs to rewrite the
/// inside of a record when it reallocates an object ID.
/// </summary>
public static class ColBundle
{
    private const int HeaderSize = 8;      // four-byte tag + uint32 size
    private const int NameOffset = 8;
    private const int NameLength = 22;

    private static readonly byte[][] Tags =
    {
        "COLL"u8.ToArray(), "COL2"u8.ToArray(), "COL3"u8.ToArray(), "COL4"u8.ToArray(),
    };

    private static bool IsTag(ReadOnlySpan<byte> candidate)
    {
        // A plain loop rather than LINQ: a span can't be captured by a lambda.
        foreach (var tag in Tags)
            if (candidate.SequenceEqual(tag)) return true;
        return false;
    }

    public static IReadOnlyList<ColRecord> Read(byte[] data)
    {
        var records = new List<ColRecord>();
        var position = 0;

        while (position + HeaderSize <= data.Length)
        {
            if (!IsTag(data.AsSpan(position, 4))) break;   // trailing sector padding, or the end

            var size = BitConverter.ToUInt32(data, position + 4);
            var total = HeaderSize + (int)size;
            if (total <= HeaderSize || position + total > data.Length) break;   // truncated: stop cleanly

            var name = ReadName(data, position);
            records.Add(new ColRecord(name, data.AsSpan(position, total).ToArray()));
            position += total;
        }

        return records;
    }

    private static string ReadName(byte[] data, int recordStart)
    {
        var span = data.AsSpan(recordStart + NameOffset, NameLength);
        var end = span.IndexOf((byte)0);
        if (end < 0) end = NameLength;
        return System.Text.Encoding.ASCII.GetString(span[..end]);
    }

    public static byte[] Write(IEnumerable<ColRecord> records)
    {
        using var buffer = new MemoryStream();
        foreach (var record in records) buffer.Write(record.Bytes, 0, record.Bytes.Length);
        return buffer.ToArray();
    }

    /// <summary>
    /// Adds collision models to a bundle. A record whose name already exists REPLACES that one
    /// rather than sitting alongside it — two records claiming the same model would leave which
    /// collision wins up to parse order, which is not something to leave to chance.
    /// </summary>
    public static byte[] Append(byte[] bundle, IEnumerable<ColRecord> additions)
    {
        var existing = Read(bundle).ToList();
        var result = new List<ColRecord>(existing);

        foreach (var addition in additions)
        {
            var index = result.FindIndex(r => r.Name.Equals(addition.Name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) result[index] = addition;
            else result.Add(addition);
        }

        return Write(result);
    }

    /// <summary>
    /// Reads a standalone .col file as shipped by a mod. Collision editors export the same record
    /// format, so a mod's file is just a bundle that happens to hold one model — or several.
    /// </summary>
    public static IReadOnlyList<ColRecord> ReadFile(string path) => Read(File.ReadAllBytes(path));

    /// <summary>
    /// Whether a record actually contains a collision SHAPE, as opposed to just a header and a
    /// bounding box.
    ///
    /// A record can legitimately be empty. San Andreas ships one for plc_stinger — the police spike
    /// strip — which is only ever spawned by script and never placed on the map, so it needs bounds
    /// and nothing else. Copying that record onto a PLACED object crashes the game exactly as a
    /// missing record does, which is how this was found.
    ///
    /// The counts sit at the same offsets in COL2, COL3 and COL4. Version 1 (COLL) stores them
    /// differently and is treated as present rather than guessed at — it doesn't appear in San
    /// Andreas map collision.
    /// </summary>
    public static bool HasGeometry(ColRecord record)
    {
        if (record.Bytes.Length < HeaderSize + 4) return false;

        var tag = record.Bytes.AsSpan(0, 4);
        if (tag.SequenceEqual("COLL"u8)) return true;

        // Body layout: name[22], model id (2), bounds (40), then the counts.
        const int counts = HeaderSize + 22 + 2 + 40;
        if (record.Bytes.Length < counts + 7) return false;

        var spheres = BitConverter.ToUInt16(record.Bytes, counts);
        var boxes = BitConverter.ToUInt16(record.Bytes, counts + 2);
        var faces = BitConverter.ToUInt16(record.Bytes, counts + 4);
        var lines = record.Bytes[counts + 6];

        return spheres + boxes + faces + lines > 0;
    }
}
