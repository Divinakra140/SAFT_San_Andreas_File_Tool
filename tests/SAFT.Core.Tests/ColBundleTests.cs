using System.Text;
using SAFT.Core;

namespace SAFT.Core.Tests;

public class ColBundleTests
{
    /// <summary>Builds a record in the real format: tag, size, 22-byte name, then payload.</summary>
    internal static byte[] MakeRecord(string name, string tag = "COL3", int payloadBytes = 40)
    {
        var payload = new byte[payloadBytes];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i + 1);

        var nameField = new byte[22];
        Encoding.ASCII.GetBytes(name).CopyTo(nameField, 0);

        // "size" counts everything after the size field itself: the name and the payload.
        var size = (uint)(nameField.Length + payload.Length);

        using var buffer = new MemoryStream();
        buffer.Write(Encoding.ASCII.GetBytes(tag));
        buffer.Write(BitConverter.GetBytes(size));
        buffer.Write(nameField);
        buffer.Write(payload);
        return buffer.ToArray();
    }

    /// <summary>
    /// A header-only COL2 record: name and bounds, but no spheres, boxes, faces or lines. Real
    /// San Andreas data contains these — plc_stinger is one — and they crash a placed object.
    /// </summary>
    internal static byte[] MakeEmptyRecord(string name)
    {
        var nameField = new byte[22];
        Encoding.ASCII.GetBytes(name).CopyTo(nameField, 0);

        // model id (2) + bounds (40) + counts and offsets (36), all zero.
        var body = new byte[2 + 40 + 36];

        using var buffer = new MemoryStream();
        buffer.Write(Encoding.ASCII.GetBytes("COL2"));
        buffer.Write(BitConverter.GetBytes((uint)(nameField.Length + body.Length)));
        buffer.Write(nameField);
        buffer.Write(body);
        return buffer.ToArray();
    }

    [Fact]
    public void Tells_a_record_with_a_shape_from_one_without()
    {
        // Big enough that the count fields land inside the payload, where MakeRecord's non-zero
        // filler stands in for a real sphere/box/face count.
        var real = ColBundle.Read(MakeRecord("wall_a", payloadBytes: 120)).Single();
        var hollow = ColBundle.Read(MakeEmptyRecord("plc_stinger")).Single();

        Assert.True(ColBundle.HasGeometry(real));
        Assert.False(ColBundle.HasGeometry(hollow));
    }

    private static byte[] Concat(params byte[][] parts)
    {
        using var buffer = new MemoryStream();
        foreach (var part in parts) buffer.Write(part);
        return buffer.ToArray();
    }

    [Fact]
    public void Reads_every_record_in_a_bundle()
    {
        var bundle = Concat(MakeRecord("wall_a"), MakeRecord("wall_b"), MakeRecord("wall_c"));

        var records = ColBundle.Read(bundle);

        Assert.Equal(new[] { "wall_a", "wall_b", "wall_c" }, records.Select(r => r.Name));
    }

    [Fact]
    public void Round_trips_a_bundle_byte_for_byte()
    {
        var bundle = Concat(MakeRecord("wall_a"), MakeRecord("wall_b"));

        var rewritten = ColBundle.Write(ColBundle.Read(bundle));

        // Reading and writing must be exactly lossless, or every rebuild would quietly corrupt
        // collision that was previously fine.
        Assert.Equal(bundle, rewritten);
    }

    [Fact]
    public void Stops_cleanly_at_the_sector_padding_that_follows_a_bundle_in_an_archive()
    {
        // Inside an .img the bundle is padded out to a 2048-byte boundary with zeros. Those zeros
        // are not a record and must not be read as one.
        var bundle = Concat(MakeRecord("wall_a"), new byte[500]);

        var records = ColBundle.Read(bundle);

        Assert.Equal("wall_a", Assert.Single(records).Name);
    }

    [Fact]
    public void Appends_a_new_model_while_leaving_the_existing_ones_untouched()
    {
        var bundle = Concat(MakeRecord("wall_a"), MakeRecord("wall_b"));
        var addition = ColBundle.Read(MakeRecord("saftcastle"));

        var result = ColBundle.Read(ColBundle.Append(bundle, addition));

        Assert.Equal(new[] { "wall_a", "wall_b", "saftcastle" }, result.Select(r => r.Name));
        Assert.Equal(MakeRecord("wall_a"), result[0].Bytes);
    }

    [Fact]
    public void Replaces_a_record_of_the_same_name_rather_than_duplicating_it()
    {
        var bundle = Concat(MakeRecord("wall_a"), MakeRecord("wall_b"));
        var replacement = ColBundle.Read(MakeRecord("wall_a", payloadBytes: 80));

        var result = ColBundle.Read(ColBundle.Append(bundle, replacement));

        // Two records claiming the same model would leave which collision wins up to parse order.
        Assert.Equal(2, result.Count);
        Assert.Equal(MakeRecord("wall_a", payloadBytes: 80), result[0].Bytes);
    }

    [Fact]
    public void A_truncated_bundle_yields_what_is_readable_instead_of_throwing()
    {
        var whole = Concat(MakeRecord("wall_a"), MakeRecord("wall_b"));
        var truncated = whole.AsSpan(0, whole.Length - 20).ToArray();

        var records = ColBundle.Read(truncated);

        // Better to salvage the intact records than to fail the whole install over a damaged tail.
        Assert.Equal("wall_a", Assert.Single(records).Name);
    }

    [Fact]
    public void Reads_a_real_collision_bundle_from_the_game_and_writes_it_back_unchanged()
    {
        // The strongest check available without launching the game: the parser is pointed at a real
        // Rockstar bundle rather than one this test invented.
        var gamePath = "/Volumes/Untitled/San Andreas/GTA-San-Andreas-SteamRIP.com/Grand Theft Auto San Andreas/models/gta3.img";
        if (!File.Exists(gamePath)) return;   // skipped anywhere but this development machine

        using var archive = ImgArchive.Open(gamePath);
        var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("las_2.col", StringComparison.OrdinalIgnoreCase));
        if (entry is null) return;

        using var stream = archive.OpenEntry(entry);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var data = memory.ToArray();

        var records = ColBundle.Read(data);

        Assert.Equal(82, records.Count);
        Assert.Contains(records, r => r.Name.Equals("BTOLAND8_LAS", StringComparison.OrdinalIgnoreCase));

        // Rewriting the parsed records must reproduce the original bytes exactly, ignoring only the
        // archive's trailing sector padding.
        var rewritten = ColBundle.Write(records);
        Assert.Equal(data.AsSpan(0, rewritten.Length).ToArray(), rewritten);
    }
}
