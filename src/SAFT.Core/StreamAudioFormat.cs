namespace SAFT.Core;

/// <summary>
/// GTA San Andreas obfuscates its streamed audio (radio stations, cutscene/ambient music) with a
/// fixed 16-byte XOR key — not real encryption, fully reversible with the same operation. Verified
/// against a real install: decrypting the beat-info section produces exactly the documented
/// "unused" pattern, the documented 4-byte footer decrypts correctly, and the payload right after
/// the header decrypts to a standard Ogg ("OggS") stream. The key's phase is tied to the absolute
/// file offset (not reset per track) — verified against a real, non-16-aligned track boundary.
/// </summary>
public static class StreamXor
{
    private static readonly byte[] Key = Convert.FromHexString("EA3AC4A19AA814F348B0D7239DE8FFF1");

    public static void Transform(Span<byte> buffer, long fileOffset)
    {
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] ^= Key[(fileOffset + i) % 16];
    }

    public static byte[] Transform(byte[] buffer, long fileOffset)
    {
        var copy = (byte[])buffer.Clone();
        Transform(copy.AsSpan(), fileOffset);
        return copy;
    }
}

/// <summary>
/// One "station" package file (audio/streams/AA, ADVERTS, CUTSCENE, ...) and its tracks. Verified
/// against a real install: StrmPaks.dat's 16-byte name records line up with TrakLkup.dat's
/// per-station track groups, whose total size matches every real station file (either exactly, or
/// short by exactly one reserved 8068-byte track header — the same "one extra reserved slot"
/// pattern seen in the SFX bank format).
/// </summary>
public sealed record StreamStation(string Name, string AbsolutePath, IReadOnlyList<(long Offset, long PayloadLength)> Tracks);

public static class StreamIndex
{
    /// <summary>1000 beat entries (8 bytes each) + 64-byte length block (8 slots) + 4-byte footer.</summary>
    public const int TrackHeaderSize = 1000 * 8 + 64 + 4;
    private const int LengthBlockOffset = 1000 * 8;
    private const int LengthSlotCount = 8;
    private const uint UnusedLengthSlotMarker = 0xCDCDCDCD;

    public static IReadOnlyList<StreamStation> Load(string gameRoot)
    {
        var configDir = Path.Combine(gameRoot, "audio", "CONFIG");
        var streamsDir = Path.Combine(gameRoot, "audio", "streams");
        var strmPaksPath = Path.Combine(configDir, "StrmPaks.dat");
        var trakLkupPath = Path.Combine(configDir, "TrakLkup.dat");

        if (!File.Exists(strmPaksPath) || !File.Exists(trakLkupPath) || !Directory.Exists(streamsDir))
            return Array.Empty<StreamStation>();

        const int strmRecordSize = 16;
        var strm = File.ReadAllBytes(strmPaksPath);
        var stationNames = new List<string>();
        for (var i = 0; i + strmRecordSize <= strm.Length; i += strmRecordSize)
        {
            var record = strm.AsSpan(i, strmRecordSize);
            var nullIndex = record.IndexOf((byte)0);
            var nameBytes = nullIndex >= 0 ? record[..nullIndex] : record;
            stationNames.Add(System.Text.Encoding.ASCII.GetString(nameBytes));
        }

        const int lkupRecordSize = 12;
        var lkup = File.ReadAllBytes(trakLkupPath);
        var tracksByStation = new List<List<(long, long)>>();
        for (var i = 0; i < stationNames.Count; i++) tracksByStation.Add(new List<(long, long)>());

        for (var i = 0; i + lkupRecordSize <= lkup.Length; i += lkupRecordSize)
        {
            var stationIndex = lkup[i];
            var offset = BitConverter.ToUInt32(lkup, i + 4);
            var length = BitConverter.ToUInt32(lkup, i + 8); // payload length only, header is separate
            if (stationIndex < tracksByStation.Count)
                tracksByStation[stationIndex].Add((offset, length));
        }

        var stations = new List<StreamStation>();
        for (var i = 0; i < stationNames.Count; i++)
        {
            if (string.IsNullOrEmpty(stationNames[i])) continue; // reserved/unused slot (e.g. index 2)
            var path = Path.Combine(streamsDir, stationNames[i]);
            if (!File.Exists(path)) continue;
            stations.Add(new StreamStation(stationNames[i], path, tracksByStation[i]));
        }
        return stations;
    }

    /// <summary>
    /// Finds which of the header's 8 length slots holds this track's real length (the rest are
    /// 0xCDCDCDCD padding) by decrypting just the length block at the given header offset.
    /// </summary>
    public static int FindActiveLengthSlot(Stream stationStream, long headerOffset)
    {
        stationStream.Position = headerOffset + LengthBlockOffset;
        var buffer = new byte[LengthSlotCount * 8];
        ReadExact(stationStream, buffer);
        StreamXor.Transform(buffer, headerOffset + LengthBlockOffset);

        for (var slot = 0; slot < LengthSlotCount; slot++)
        {
            var v = BitConverter.ToUInt32(buffer, slot * 8);
            if (v != UnusedLengthSlotMarker) return slot;
        }
        return 0; // defensive fallback; every real track header has an active slot
    }

    /// <summary>Absolute file offset of a length slot's first 4 bytes (the field that holds the payload length).</summary>
    public static long GetLengthFieldOffset(long headerOffset, int lengthSlot) =>
        headerOffset + LengthBlockOffset + lengthSlot * 8;

    public static bool LooksLikeOgg(byte[] data) =>
        data.Length >= 4 && data[0] == (byte)'O' && data[1] == (byte)'g' && data[2] == (byte)'g' && data[3] == (byte)'S';

    /// <summary>
    /// Overwrites a track's Ogg payload (re-encrypted, phased to its absolute file position) and
    /// updates the header's declared length to the new, real size — leftover space up to the
    /// original allocation is zero-padded (also XOR'd, like the rest of the file). Shared by the
    /// direct-install tab and extract/rebuild's audio pass. Throws if the new content doesn't fit.
    /// </summary>
    public static void PatchTrack(string stationAbsolutePath, long headerOffset, long originalPayloadLength, byte[] newPayload)
    {
        if (newPayload.Length > originalPayloadLength)
            throw new InvalidOperationException(
                $"New audio ({newPayload.Length} bytes) is larger than the original track's allocated space ({originalPayloadLength} bytes).");

        int lengthSlot;
        using (var readStream = File.OpenRead(stationAbsolutePath))
            lengthSlot = FindActiveLengthSlot(readStream, headerOffset);

        using var writeStream = new FileStream(stationAbsolutePath, FileMode.Open, FileAccess.Write, FileShare.Read);

        var lengthFieldOffset = GetLengthFieldOffset(headerOffset, lengthSlot);
        writeStream.Position = lengthFieldOffset;
        writeStream.Write(StreamXor.Transform(BitConverter.GetBytes((uint)newPayload.Length), lengthFieldOffset));

        var payloadOffset = headerOffset + TrackHeaderSize;
        writeStream.Position = payloadOffset;
        writeStream.Write(StreamXor.Transform(newPayload, payloadOffset));

        var remaining = originalPayloadLength - newPayload.Length;
        if (remaining > 0)
        {
            // Chunked for the same reason as the archive padding in DirectModInstaller: replacing a
            // long track with a short one leaves a remainder measured in megabytes, and one array
            // that size is a Large Object Heap request on a 32-bit heap. The XOR is position
            // dependent, so each chunk is transformed at its own offset.
            var padPosition = payloadOffset + newPayload.Length;
            var buffer = new byte[Math.Min(remaining, 81920)];

            while (remaining > 0)
            {
                var chunk = (int)Math.Min(buffer.Length, remaining);
                Array.Clear(buffer, 0, chunk); // Transform mutates in place, so reset before reuse
                StreamXor.Transform(buffer.AsSpan(0, chunk), padPosition);
                writeStream.Write(buffer, 0, chunk);

                padPosition += chunk;
                remaining -= chunk;
            }
        }
    }

    private static void ReadExact(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) throw new EndOfStreamException("Unexpected end of file while reading a stream track header.");
            read += n;
        }
    }
}
