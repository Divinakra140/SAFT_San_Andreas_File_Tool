using System.Text;

namespace SAFT.Core.Tests;

public class WavPcmTests
{
    [Fact]
    public void ReadMono16Wav_round_trips_a_file_it_wrote_itself()
    {
        var path = Path.Combine(TestScratch.NewDir(), "sound.wav");
        var pcm = new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 };
        using (var f = File.Create(path))
            WavPcm.WriteMono16Wav(f, pcm, 22050);

        var (readPcm, sampleRate) = WavPcm.ReadMono16Wav(path);

        Assert.Equal(pcm, readPcm);
        Assert.Equal(22050, sampleRate);
    }

    /// <summary>Builds a minimal RIFF/WAVE file with a hand-picked (possibly invalid) declared size for its 'data' chunk.</summary>
    private static string WriteWavWithDeclaredDataSize(string path, int declaredDataChunkSize, byte[] actualPcmBytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);

        var fmtChunkSize = 16;
        var dataChunkHeaderAndFmt = 4 + fmtChunkSize + 8; // "fmt " + its payload + "data" id/size header
        var riffSize = 4 + (8 + fmtChunkSize) + (8 + actualPcmBytes.Length);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(riffSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(fmtChunkSize);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // mono
        writer.Write(22050); // sample rate
        writer.Write(22050 * 2); // byte rate
        writer.Write((short)2); // block align
        writer.Write((short)16); // bits per sample

        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(declaredDataChunkSize); // deliberately possibly wrong
        writer.Write(actualPcmBytes);

        return path;
    }

    [Fact]
    public void ReadMono16Wav_rejects_a_negative_declared_chunk_size_with_a_clear_message_instead_of_crashing()
    {
        var path = Path.Combine(TestScratch.NewDir(), "corrupt.wav");
        WriteWavWithDeclaredDataSize(path, declaredDataChunkSize: -1, actualPcmBytes: new byte[] { 1, 0, 2, 0 });

        var ex = Assert.Throws<InvalidDataException>(() => WavPcm.ReadMono16Wav(path));
        Assert.Contains("corrupt.wav", ex.Message);
    }

    [Fact]
    public void ReadMono16Wav_rejects_a_declared_chunk_size_bigger_than_the_actual_file_with_a_clear_message()
    {
        var path = Path.Combine(TestScratch.NewDir(), "corrupt.wav");
        WriteWavWithDeclaredDataSize(path, declaredDataChunkSize: 999_999, actualPcmBytes: new byte[] { 1, 0, 2, 0 });

        var ex = Assert.Throws<InvalidDataException>(() => WavPcm.ReadMono16Wav(path));
        Assert.Contains("corrupt.wav", ex.Message);
    }
}
