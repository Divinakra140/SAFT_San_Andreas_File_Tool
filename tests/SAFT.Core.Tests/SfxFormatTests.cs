using SAFT.Core;

namespace SAFT.Core.Tests;

public class SfxFormatTests
{
    /// <summary>
    /// Builds a synthetic package file with one bank containing the given PCM buffers, using the
    /// verified real-world layout: 4804-byte bank_header (count + 400 fixed slots) then PCM data
    /// packed back-to-back.
    /// </summary>
    private static (string PackagePath, long BankOffset, long BankLength) BuildSyntheticPackage(
        string dir, params (int SampleRate, byte[] Pcm)[] sounds)
    {
        var path = Path.Combine(dir, "TESTPKG");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);

        var bankOffset = 0L;
        writer.Write((uint)sounds.Length);

        uint runningOffset = 0;
        var offsets = new uint[sounds.Length];
        for (var i = 0; i < sounds.Length; i++)
        {
            offsets[i] = runningOffset;
            runningOffset += (uint)sounds[i].Pcm.Length;
        }

        for (var i = 0; i < sounds.Length; i++)
        {
            writer.Write(offsets[i]);
            writer.Write(-1); // loop offset: none
            writer.Write((ushort)sounds[i].SampleRate);
            writer.Write((ushort)0); // headroom
        }
        // pad the remaining unused slots up to 400
        for (var i = sounds.Length; i < SfxIndex.MaxSoundsPerBank; i++)
            writer.Write(new byte[12]);

        foreach (var (_, pcm) in sounds)
            writer.Write(pcm);

        writer.Flush();
        var bankLength = stream.Length - bankOffset;
        return (path, bankOffset, bankLength);
    }

    [Fact]
    public void SfxBank_reads_offsets_and_lengths_matching_what_was_written()
    {
        var dir = TestScratch.NewDir();
        var soundA = new byte[] { 1, 2, 3, 4, 5, 6 }; // 3 samples
        var soundB = new byte[] { 7, 8, 9, 10 };      // 2 samples
        var soundC = new byte[] { 11, 12 };           // 1 sample

        var (path, offset, length) = BuildSyntheticPackage(dir,
            (22050, soundA), (16000, soundB), (8000, soundC));

        using var stream = File.OpenRead(path);
        var bank = SfxBank.Read(stream, offset, length);

        Assert.Equal(3, bank.Sounds.Count);
        Assert.Equal(22050, bank.Sounds[0].SampleRate);
        Assert.Equal(6, bank.GetPcmLength(0));
        Assert.Equal(4, bank.GetPcmLength(1));
        Assert.Equal(2, bank.GetPcmLength(2)); // last sound: length comes from bank end, not a "next" slot

        stream.Position = bank.GetPcmOffset(1);
        var readBackB = new byte[bank.GetPcmLength(1)];
        stream.ReadExactly(readBackB);
        Assert.Equal(soundB, readBackB);
    }

    [Fact]
    public void SfxIndex_Load_returns_empty_when_no_audio_config_present()
    {
        var gameRoot = TestScratch.NewDir(); // no audio/CONFIG at all
        var packages = SfxIndex.Load(gameRoot);
        Assert.Empty(packages);
    }

    [Fact]
    public void WavPcm_round_trips_mono_16bit_pcm()
    {
        var dir = TestScratch.NewDir();
        var path = Path.Combine(dir, "test.wav");
        var pcm = new byte[] { 1, 0, 2, 0, 255, 127, 0, 128 }; // 4 arbitrary 16-bit samples

        using (var stream = File.Create(path))
            WavPcm.WriteMono16Wav(stream, pcm, 22050);

        var (readPcm, sampleRate) = WavPcm.ReadMono16Wav(path);

        Assert.Equal(pcm, readPcm);
        Assert.Equal(22050, sampleRate);
    }

    [Fact]
    public void WavPcm_rejects_stereo_or_non_16bit_files()
    {
        var dir = TestScratch.NewDir();
        var path = Path.Combine(dir, "stereo.wav");

        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + 4);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)2); // stereo, not mono
            writer.Write(22050);
            writer.Write(22050 * 4);
            writer.Write((short)4);
            writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(4);
            writer.Write(new byte[4]);
        }

        Assert.Throws<InvalidDataException>(() => WavPcm.ReadMono16Wav(path));
    }

    /// <summary>
    /// 9 of this game's 61,993 sounds have a last-sound buffer offset past where the bank lookup says
    /// the bank ends. That subtraction used to be done in uint, so it wrapped to about 4.29 billion -
    /// which made every replacement look like it fitted, and would have had PatchSound zero-fill 4 GB
    /// over the package. Clamped to 0 now, so the sound simply cannot be replaced.
    /// </summary>
    [Fact]
    public void A_last_sound_reaching_past_the_bank_end_reports_zero_length_not_four_billion()
    {
        // One sound whose buffer offset is beyond the bank's PCM region, exactly as FEET bank 4 is.
        var bankLength = SfxIndex.BankHeaderSize + 1_000;
        var header = new byte[SfxIndex.BankHeaderSize];
        BitConverter.GetBytes(1u).CopyTo(header, 0);          // one sound
        BitConverter.GetBytes(2_028u).CopyTo(header, 4);      // offset past the 1,000 bytes of data
        BitConverter.GetBytes(0).CopyTo(header, 8);
        BitConverter.GetBytes((ushort)22050).CopyTo(header, 12);

        using var stream = new MemoryStream(header.Concat(new byte[1_000]).ToArray());
        var bank = SfxBank.Read(stream, 0, bankLength);

        var length = bank.GetPcmLength(0);

        Assert.Equal(0, length);
        Assert.True(length >= 0, "a negative length must never be handed back as a huge positive one");
    }

    [Fact]
    public void Ordinary_sounds_still_measure_up_to_the_next_ones_offset()
    {
        var bankLength = SfxIndex.BankHeaderSize + 300;
        var header = new byte[SfxIndex.BankHeaderSize];
        BitConverter.GetBytes(2u).CopyTo(header, 0);
        BitConverter.GetBytes(0u).CopyTo(header, 4);          // sound 1 at 0
        BitConverter.GetBytes(0).CopyTo(header, 8);
        BitConverter.GetBytes((ushort)22050).CopyTo(header, 12);
        BitConverter.GetBytes(100u).CopyTo(header, 16);       // sound 2 at 100
        BitConverter.GetBytes(0).CopyTo(header, 20);
        BitConverter.GetBytes((ushort)22050).CopyTo(header, 24);

        using var stream = new MemoryStream(header.Concat(new byte[300]).ToArray());
        var bank = SfxBank.Read(stream, 0, bankLength);

        Assert.Equal(100, bank.GetPcmLength(0));   // up to the next sound
        Assert.Equal(200, bank.GetPcmLength(1));   // last one runs to the end of the bank
    }
}
