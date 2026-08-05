using SAFT.Core;

namespace SAFT.Core.Tests;

public class StreamAudioFormatTests
{
    [Fact]
    public void StreamXor_is_its_own_inverse_and_phase_depends_on_absolute_offset()
    {
        var plaintext = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18 };

        var encrypted = StreamXor.Transform(plaintext, fileOffset: 205931); // deliberately not 16-aligned
        var roundTripped = StreamXor.Transform(encrypted, fileOffset: 205931);
        Assert.Equal(plaintext, roundTripped);

        // Decrypting with the WRONG phase must not accidentally recover the same plaintext.
        var wrongPhase = StreamXor.Transform(encrypted, fileOffset: 0);
        Assert.NotEqual(plaintext, wrongPhase);
    }

    [Fact]
    public void LooksLikeOgg_checks_for_the_real_container_magic()
    {
        Assert.True(StreamIndex.LooksLikeOgg(new byte[] { (byte)'O', (byte)'g', (byte)'g', (byte)'S', 1, 2 }));
        Assert.False(StreamIndex.LooksLikeOgg(new byte[] { 1, 2, 3, 4 }));
        Assert.False(StreamIndex.LooksLikeOgg(new byte[] { (byte)'O', (byte)'g', (byte)'g' })); // too short
    }

    /// <summary>
    /// Builds a synthetic station file with one track at a deliberately non-16-aligned offset
    /// (matching the real-world case that actually distinguished the two XOR-phase hypotheses),
    /// using the verified real layout: 8068-byte header (1000 zeroed beat entries, one active
    /// length slot, "01 00 CD CD" footer) then the XOR'd Ogg payload.
    /// </summary>
    private static (string StationPath, long HeaderOffset) BuildSyntheticStation(string dir, byte[] oggPayload, int activeLengthSlot = 0)
    {
        var path = Path.Combine(dir, "TESTSTATION");
        const long headerOffset = 11; // non-16-aligned, deliberately

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        stream.Write(new byte[headerOffset]); // leading padding so the track doesn't start at 0

        var header = new byte[StreamIndex.TrackHeaderSize];
        for (var i = 0; i < 1000; i++)
        {
            BitConverter.GetBytes(-1).CopyTo(header, i * 8);     // timing = -1 (unused)
            BitConverter.GetBytes(0).CopyTo(header, i * 8 + 4);  // control = 0
        }
        for (var slot = 0; slot < 8; slot++)
        {
            var value = slot == activeLengthSlot ? (uint)oggPayload.Length : 0xCDCDCDCDu;
            BitConverter.GetBytes(value).CopyTo(header, 8000 + slot * 8);
            BitConverter.GetBytes(0xCDCDCDCDu).CopyTo(header, 8000 + slot * 8 + 4);
        }
        header[8064] = 0x01; header[8065] = 0x00; header[8066] = 0xCD; header[8067] = 0xCD;

        StreamXor.Transform(header, headerOffset);
        stream.Write(header);

        var encryptedPayload = StreamXor.Transform(oggPayload, headerOffset + StreamIndex.TrackHeaderSize);
        stream.Write(encryptedPayload);

        return (path, headerOffset);
    }

    [Fact]
    public void FindActiveLengthSlot_locates_the_real_length_among_the_padding_slots()
    {
        var dir = TestScratch.NewDir();
        var payload = new byte[] { (byte)'O', (byte)'g', (byte)'g', (byte)'S', 9, 9, 9 };
        var (path, headerOffset) = BuildSyntheticStation(dir, payload, activeLengthSlot: 3);

        using var stream = File.OpenRead(path);
        var slot = StreamIndex.FindActiveLengthSlot(stream, headerOffset);

        Assert.Equal(3, slot);
    }

    [Fact]
    public void Synthetic_track_payload_decrypts_to_a_valid_looking_Ogg_stream()
    {
        var dir = TestScratch.NewDir();
        var payload = new byte[] { (byte)'O', (byte)'g', (byte)'g', (byte)'S', 0, 2, 0, 0, 42, 42, 42 };
        var (path, headerOffset) = BuildSyntheticStation(dir, payload, activeLengthSlot: 0);

        using var stream = File.OpenRead(path);
        var payloadOffset = headerOffset + StreamIndex.TrackHeaderSize;
        stream.Position = payloadOffset;
        var raw = new byte[payload.Length];
        stream.ReadExactly(raw);

        var decrypted = StreamXor.Transform(raw, payloadOffset);

        Assert.Equal(payload, decrypted);
        Assert.True(StreamIndex.LooksLikeOgg(decrypted));
    }

    [Fact]
    public void StreamIndex_Load_returns_empty_when_no_audio_config_present()
    {
        var gameRoot = TestScratch.NewDir();
        Assert.Empty(StreamIndex.Load(gameRoot));
    }
}
