using System.Text;

namespace SAFT.Core;

/// <summary>
/// San Andreas SFX sound data is signed 16-bit mono PCM with no header — just the samples.
/// These wrap that raw PCM as a standard RIFF/WAVE file for extraction (so it's a normal,
/// playable .wav) and unwrap a replacement .wav back down to raw PCM for re-import.
/// </summary>
public static class WavPcm
{
    public static void WriteMono16Wav(Stream destination, byte[] pcm, int sampleRate)
    {
        using var writer = new BinaryWriter(destination, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcm.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16); // fmt chunk size
        writer.Write((short)1); // PCM
        writer.Write((short)1); // mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2); // byte rate = sampleRate * blockAlign
        writer.Write((short)2); // block align (1 channel * 16 bits / 8)
        writer.Write((short)16); // bits per sample

        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcm.Length);
        writer.Write(pcm);
    }

    /// <summary>
    /// Reads a mono 16-bit PCM .wav's raw sample bytes and sample rate. Throws if the file isn't
    /// mono 16-bit PCM — that's a hard requirement of the format being replaced, not a style choice.
    /// </summary>
    public static (byte[] Pcm, int SampleRate) ReadMono16Wav(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        if (new string(reader.ReadChars(4)) != "RIFF")
            throw new InvalidDataException($"'{path}' is not a RIFF/WAVE file.");
        reader.ReadInt32(); // total size, unused
        if (new string(reader.ReadChars(4)) != "WAVE")
            throw new InvalidDataException($"'{path}' is not a RIFF/WAVE file.");

        short channels = 0, bitsPerSample = 0;
        var sampleRate = 0;
        byte[]? pcm = null;

        while (stream.Position <= stream.Length - 8)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkSize = reader.ReadInt32();
            var chunkStart = stream.Position;

            if (chunkId == "fmt ")
            {
                reader.ReadInt16(); // format tag
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32(); // byte rate, unused
                reader.ReadInt16(); // block align, unused
                bitsPerSample = reader.ReadInt16();
            }
            else if (chunkId == "data")
            {
                pcm = reader.ReadBytes(chunkSize);
            }

            stream.Position = chunkStart + chunkSize + (chunkSize % 2); // chunks are word-aligned
        }

        if (pcm is null)
            throw new InvalidDataException($"'{path}' has no 'data' chunk.");
        if (channels != 1 || bitsPerSample != 16)
            throw new InvalidDataException(
                $"'{path}' must be mono 16-bit PCM to replace a San Andreas sound effect (was {channels} channel(s), {bitsPerSample}-bit).");

        return (pcm, sampleRate);
    }
}
