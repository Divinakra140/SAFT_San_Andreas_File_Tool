namespace SAFT.Core;

/// <summary>
/// One GTA San Andreas SFX "package" file (audio/sfx/GENRL, FEET, SCRIPT, ...), and the byte
/// ranges of its banks within that file. Verified byte-for-byte against a real retail install:
/// PakFiles.dat lists package names (52-byte records, name null-terminated); BankLkup.dat lists
/// every bank across every package (12-byte records: 1-byte package index + 3 bytes padding +
/// uint32 offset + uint32 length, grouped in package order); every package file's real size is
/// exactly 4804 bytes (one full bank_header) more than the last bank's (offset + length).
/// </summary>
public sealed record SfxPackage(string Name, string AbsolutePath, IReadOnlyList<(long Offset, long Length)> Banks);

/// <summary>One sound slot's metadata inside a bank header (12 bytes on disk).</summary>
public sealed record SfxSound(uint BufferOffset, int LoopOffset, ushort SampleRate, ushort Headroom);

public static class SfxIndex
{
    /// <summary>4-byte sound count + 400 12-byte SoundMeta slots, verified against every real package file's size.</summary>
    public const int BankHeaderSize = 4 + MaxSoundsPerBank * 12;
    public const int MaxSoundsPerBank = 400;

    /// <summary>
    /// Loads every SFX package and its banks for a game install. Returns an empty list (not an
    /// error) if the install has no audio/CONFIG — SFX support is additive, never a hard
    /// requirement for the rest of SAFT to work.
    /// </summary>
    public static IReadOnlyList<SfxPackage> Load(string gameRoot)
    {
        var configDir = Path.Combine(gameRoot, "audio", "CONFIG");
        var sfxDir = Path.Combine(gameRoot, "audio", "sfx");
        var pakFilesPath = Path.Combine(configDir, "PakFiles.dat");
        var bankLkupPath = Path.Combine(configDir, "BankLkup.dat");

        if (!File.Exists(pakFilesPath) || !File.Exists(bankLkupPath) || !Directory.Exists(sfxDir))
            return Array.Empty<SfxPackage>();

        const int pakRecordSize = 52;
        var pak = File.ReadAllBytes(pakFilesPath);
        var packageNames = new List<string>();
        for (var i = 0; i + pakRecordSize <= pak.Length; i += pakRecordSize)
        {
            var record = pak.AsSpan(i, pakRecordSize);
            var nullIndex = record.IndexOf((byte)0);
            var nameBytes = nullIndex >= 0 ? record[..nullIndex] : record;
            packageNames.Add(System.Text.Encoding.ASCII.GetString(nameBytes));
        }

        const int lkupRecordSize = 12;
        var lkup = File.ReadAllBytes(bankLkupPath);
        var banksByPackage = new List<List<(long, long)>>();
        for (var i = 0; i < packageNames.Count; i++) banksByPackage.Add(new List<(long, long)>());

        for (var i = 0; i + lkupRecordSize <= lkup.Length; i += lkupRecordSize)
        {
            var packageIndex = lkup[i]; // first byte of the 4-byte index field; rest is padding
            var offset = BitConverter.ToUInt32(lkup, i + 4);
            var length = BitConverter.ToUInt32(lkup, i + 8);
            if (packageIndex < banksByPackage.Count)
                banksByPackage[packageIndex].Add((offset, length));
        }

        var packages = new List<SfxPackage>();
        for (var i = 0; i < packageNames.Count; i++)
        {
            var path = Path.Combine(sfxDir, packageNames[i]);
            if (!File.Exists(path)) continue;
            packages.Add(new SfxPackage(packageNames[i], path, banksByPackage[i]));
        }
        return packages;
    }
}

/// <summary>
/// One parsed bank header: a 4-byte sound count followed by up to 400 fixed-size sound-metadata
/// slots (BufferOffset, LoopOffset, SampleRate, Headroom), then the PCM data itself. Verified
/// against a real bank: offsets increase monotonically, byte lengths are always even (16-bit
/// samples), and sample rates/durations are all in plausible ranges.
/// </summary>
public sealed class SfxBank
{
    public long HeaderOffset { get; }
    public long BankLength { get; }
    public IReadOnlyList<SfxSound> Sounds { get; }

    private SfxBank(long headerOffset, long bankLength, IReadOnlyList<SfxSound> sounds)
    {
        HeaderOffset = headerOffset;
        BankLength = bankLength;
        Sounds = sounds;
    }

    public static SfxBank Read(Stream packageStream, long headerOffset, long bankLength)
    {
        packageStream.Position = headerOffset;
        var buffer = new byte[SfxIndex.BankHeaderSize];
        ReadExact(packageStream, buffer);

        var numSounds = BitConverter.ToUInt32(buffer, 0);
        var sounds = new List<SfxSound>((int)numSounds);
        for (var i = 0; i < numSounds; i++)
        {
            var o = 4 + i * 12;
            sounds.Add(new SfxSound(
                BufferOffset: BitConverter.ToUInt32(buffer, o),
                LoopOffset: BitConverter.ToInt32(buffer, o + 4),
                SampleRate: BitConverter.ToUInt16(buffer, o + 8),
                Headroom: BitConverter.ToUInt16(buffer, o + 10)));
        }

        return new SfxBank(headerOffset, bankLength, sounds);
    }

    /// <summary>Absolute byte offset of a sound's raw PCM data within the package file.</summary>
    public long GetPcmOffset(int soundIndex) => HeaderOffset + SfxIndex.BankHeaderSize + Sounds[soundIndex].BufferOffset;

    /// <summary>Byte length of a sound's raw PCM data: up to the next sound's offset, or the end of the bank for the last one.</summary>
    public long GetPcmLength(int soundIndex)
    {
        var start = Sounds[soundIndex].BufferOffset;
        var end = soundIndex + 1 < Sounds.Count
            ? Sounds[soundIndex + 1].BufferOffset
            : (uint)(BankLength - SfxIndex.BankHeaderSize);
        return end - start;
    }

    private static void ReadExact(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) throw new EndOfStreamException("Unexpected end of file while reading an SFX bank header.");
            read += n;
        }
    }

    /// <summary>
    /// Overwrites exactly one sound's existing PCM byte range in a live package file, zero-padding
    /// any leftover space up to the original allocation — the bank header (offsets, sample rate,
    /// loop point) never changes. Shared by the direct-install tab and extract/rebuild's audio
    /// pass, since both patch sounds the same way. Throws if the new content doesn't fit.
    /// </summary>
    public static void PatchSound(string packageAbsolutePath, long bankHeaderOffset, long bankLength, int soundIndex, byte[] newPcm)
    {
        long pcmOffset;
        long originalLength;
        using (var readStream = File.OpenRead(packageAbsolutePath))
        {
            var bank = Read(readStream, bankHeaderOffset, bankLength);
            pcmOffset = bank.GetPcmOffset(soundIndex);
            originalLength = bank.GetPcmLength(soundIndex);
        } // read handle must close before we reopen the same path for writing

        if (newPcm.Length > originalLength)
            throw new InvalidOperationException(
                $"New audio ({newPcm.Length} bytes) is larger than the original sound's allocated space ({originalLength} bytes).");

        using var writeStream = new FileStream(packageAbsolutePath, FileMode.Open, FileAccess.Write, FileShare.Read);
        writeStream.Position = pcmOffset;
        writeStream.Write(newPcm);

        var remaining = originalLength - newPcm.Length;
        if (remaining > 0)
            writeStream.Write(new byte[remaining]);
    }
}
