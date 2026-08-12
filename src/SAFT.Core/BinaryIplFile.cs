namespace SAFT.Core;

/// <summary>
/// Reads the BINARY .ipl files San Andreas keeps inside its archives.
///
/// Most of the map is placed by these, not by the text .ipl files under data/: on a stock install
/// they hold 36,569 of the 45,884 placements, roughly four fifths. Anything that tries to describe
/// how crowded the game is by reading only the text files is looking at a fifth of the world.
///
/// SAFT never writes these. They're read so that "how dense is this player's game already" is
/// answered from the whole map rather than from the part that happens to be in plain text.
///
/// Layout, confirmed against las_stream0.ipl in a real install: the four bytes "bnry", then six
/// uint32 counts, then a uint32 giving the byte offset of the instance array. Each instance is 40
/// bytes: three floats of position, four of rotation, then int32 model id, interior and LOD.
/// </summary>
public static class BinaryIplFile
{
    private const int HeaderSize = 32;
    private const int InstanceSize = 40;
    private const int InstanceCountOffset = 4;
    private const int InstanceArrayOffset = 28;

    public static bool LooksLikeBinaryIpl(byte[] data) =>
        data.Length >= HeaderSize && data[0] == 'b' && data[1] == 'n' && data[2] == 'r' && data[3] == 'y';

    /// <summary>
    /// <paramref name="nameForId"/> supplies the model name, which these files don't carry — they
    /// reference an object by id and rely on the .ide files for the rest. An id nothing defines gets
    /// an empty name and simply weighs nothing.
    /// </summary>
    public static IReadOnlyList<IplInstance> Read(byte[] data, Func<int, string> nameForId)
    {
        if (!LooksLikeBinaryIpl(data)) return Array.Empty<IplInstance>();

        var count = BitConverter.ToUInt32(data, InstanceCountOffset);
        var arrayStart = BitConverter.ToUInt32(data, InstanceArrayOffset);

        var instances = new List<IplInstance>();
        for (uint i = 0; i < count; i++)
        {
            var at = arrayStart + i * InstanceSize;
            if (at + InstanceSize > data.Length) break;   // truncated: take what's readable

            var x = BitConverter.ToSingle(data, (int)at);
            var y = BitConverter.ToSingle(data, (int)at + 4);
            var z = BitConverter.ToSingle(data, (int)at + 8);
            var modelId = BitConverter.ToInt32(data, (int)at + 28);
            var interior = BitConverter.ToInt32(data, (int)at + 32);

            instances.Add(new IplInstance(
                modelId, nameForId(modelId), interior, x, y, z, RawLine: string.Empty, LineNumber: 0));
        }

        return instances;
    }

    /// <summary>Every placement in every binary .ipl inside the game's archives.</summary>
    public static IReadOnlyList<IplInstance> ReadAllFromGame(string gameRoot, Func<int, string> nameForId)
    {
        var all = new List<IplInstance>();
        ReadAllFromGame(gameRoot, null, nameForId, all.Add);
        return all;
    }

    /// <summary>
    /// As above, but hands each placement to <paramref name="onInstance"/> and keeps none.
    ///
    /// The binary .ipl files hold about four fifths of the San Andreas map — roughly 40,000 of its
    /// 50,982 placements — and the list-returning version above built every one of them before the
    /// caller could reduce them. That single list was the largest allocation SAFT made, on a 32-bit
    /// heap that is never compacted, and it was built on every install. A caller that only wants
    /// per-cell totals should take them one at a time through here.
    /// </summary>
    public static void ReadAllFromGame(
        string gameRoot, GameFiles? files, Func<int, string> nameForId, Action<IplInstance> onInstance)
    {
        foreach (var found in GameScanner.FindArchives(gameRoot, files))
        {
            try
            {
                using var archive = ImgArchive.Open(found.AbsolutePath);
                foreach (var entry in archive.Entries)
                {
                    if (!entry.Name.EndsWith(".ipl", StringComparison.OrdinalIgnoreCase)) continue;

                    using var stream = archive.OpenEntry(entry);
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    foreach (var instance in Read(buffer.ToArray(), nameForId)) onInstance(instance);
                }
            }
            catch
            {
                // An unreadable archive costs some accuracy in the baseline, which is better than
                // refusing to report anything at all.
            }
        }
    }
}
