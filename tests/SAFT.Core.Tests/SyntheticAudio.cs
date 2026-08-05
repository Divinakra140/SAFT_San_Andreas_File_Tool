namespace SAFT.Core.Tests;

/// <summary>
/// Builds minimal, real-layout-matching SFX packages and streamed-audio stations for tests —
/// shared by <see cref="DirectModInstallerTests"/> and the extract/rebuild audio tests, since both
/// need the exact same on-disk format (PakFiles.dat/BankLkup.dat/bank_header, or
/// StrmPaks.dat/TrakLkup.dat/track_header) to exercise real parsing code, not a shortcut.
/// </summary>
internal static class SyntheticAudio
{
    /// <summary>Writes one SFX package with a single bank containing the given sounds.</summary>
    public static void AddSfxPackage(string gameRoot, string packageName, params (int SampleRate, byte[] Pcm)[] sounds)
    {
        var configDir = Path.Combine(gameRoot, "audio", "CONFIG");
        var sfxDir = Path.Combine(gameRoot, "audio", "sfx");
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(sfxDir);

        // PakFiles.dat: one 52-byte record with the package name.
        var pakRecord = new byte[52];
        System.Text.Encoding.ASCII.GetBytes(packageName).CopyTo(pakRecord, 0);
        File.WriteAllBytes(Path.Combine(configDir, "PakFiles.dat"), pakRecord);

        // The package file itself: one bank_header (4804 bytes) then PCM data back-to-back.
        var packagePath = Path.Combine(sfxDir, packageName);
        using (var stream = new FileStream(packagePath, FileMode.Create, FileAccess.Write))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((uint)sounds.Length);
            uint runningOffset = 0;
            var offsets = new uint[sounds.Length];
            for (var i = 0; i < sounds.Length; i++) { offsets[i] = runningOffset; runningOffset += (uint)sounds[i].Pcm.Length; }
            for (var i = 0; i < sounds.Length; i++)
            {
                writer.Write(offsets[i]);
                writer.Write(-1);
                writer.Write((ushort)sounds[i].SampleRate);
                writer.Write((ushort)0);
            }
            for (var i = sounds.Length; i < SfxIndex.MaxSoundsPerBank; i++) writer.Write(new byte[12]);
            foreach (var (_, pcm) in sounds) writer.Write(pcm);
        }

        var bankLength = new FileInfo(packagePath).Length;

        // BankLkup.dat: one 12-byte record for this single bank (package index 0, offset 0, this length).
        using var lkupStream = new FileStream(Path.Combine(configDir, "BankLkup.dat"), FileMode.Create, FileAccess.Write);
        using var lkupWriter = new BinaryWriter(lkupStream);
        lkupWriter.Write((byte)0);
        lkupWriter.Write(new byte[3]);
        lkupWriter.Write(0u);
        lkupWriter.Write((uint)bankLength);
    }

    public static byte[] BuildOggLikePayload(int contentByte, int length)
    {
        var payload = new byte[length];
        payload[0] = (byte)'O'; payload[1] = (byte)'g'; payload[2] = (byte)'g'; payload[3] = (byte)'S';
        for (var i = 4; i < length; i++) payload[i] = (byte)contentByte;
        return payload;
    }

    /// <summary>Writes one stream station with a single track holding the given Ogg-like payload.</summary>
    public static void AddStreamStation(string gameRoot, string stationName, byte[] oggPayload)
    {
        var configDir = Path.Combine(gameRoot, "audio", "CONFIG");
        var streamsDir = Path.Combine(gameRoot, "audio", "streams");
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(streamsDir);

        var strmRecord = new byte[16];
        System.Text.Encoding.ASCII.GetBytes(stationName).CopyTo(strmRecord, 0);
        File.WriteAllBytes(Path.Combine(configDir, "StrmPaks.dat"), strmRecord);

        const long headerOffset = 0;
        var stationPath = Path.Combine(streamsDir, stationName);
        var header = new byte[StreamIndex.TrackHeaderSize];
        for (var i = 0; i < 1000; i++)
        {
            BitConverter.GetBytes(-1).CopyTo(header, i * 8);
            BitConverter.GetBytes(0).CopyTo(header, i * 8 + 4);
        }
        BitConverter.GetBytes((uint)oggPayload.Length).CopyTo(header, 8000); // active length slot 0
        for (var slot = 1; slot < 8; slot++)
            BitConverter.GetBytes(0xCDCDCDCDu).CopyTo(header, 8000 + slot * 8);
        header[8064] = 0x01; header[8065] = 0x00; header[8066] = 0xCD; header[8067] = 0xCD;

        using (var stream = new FileStream(stationPath, FileMode.Create, FileAccess.Write))
        {
            stream.Write(StreamXor.Transform(header, headerOffset));
            stream.Write(StreamXor.Transform(oggPayload, headerOffset + StreamIndex.TrackHeaderSize));
        }

        using var lkupStream = new FileStream(Path.Combine(configDir, "TrakLkup.dat"), FileMode.Create, FileAccess.Write);
        using var lkupWriter = new BinaryWriter(lkupStream);
        lkupWriter.Write((byte)0);
        lkupWriter.Write(new byte[3]);
        lkupWriter.Write((uint)headerOffset);
        lkupWriter.Write((uint)oggPayload.Length);
    }
}
