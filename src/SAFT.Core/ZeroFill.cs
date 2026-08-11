namespace SAFT.Core;

/// <summary>
/// Writes a run of zero bytes to a stream without allocating a buffer the size of the run.
///
/// The obvious `stream.Write(new byte[remaining])` is fine when the remainder is a few hundred bytes
/// of sector padding, which is what every one of these call sites was written for. It stops being
/// fine when the remainder is the gap a large file left behind.
///
/// Uninstalling a heavy texture pack is exactly that case. Restoring the 1.2 MB vanilla
/// lahillsroadscoast.txd into the 28.8 MB slot its modded replacement had grown to asks for a single
/// 27.6 MB array — and, in the same uninstall, 13.5 MB and 7.4 MB right behind it. SAFT is a 32-bit
/// process with a 2 GB address space, and allocations that size go on the Large Object Heap, where a
/// fragmented heap can refuse a request long before the address space is genuinely exhausted. The
/// failure is not a tidy exception either: it took the process down with nothing in the log.
///
/// The buffer here is shared and never written to, only read from, so concurrent writers are safe.
/// </summary>
internal static class ZeroFill
{
    /// <summary>
    /// Matches the copy buffer size used elsewhere in SAFT. Big enough that the write syscall count
    /// stays irrelevant, small enough to live on the ordinary heap forever.
    /// </summary>
    private const int ChunkSize = 81920;

    private static readonly byte[] Zeros = new byte[ChunkSize];

    /// <summary>Writes exactly <paramref name="count"/> zero bytes at the stream's current position.</summary>
    public static void Write(Stream stream, long count)
    {
        while (count > 0)
        {
            var chunk = (int)Math.Min(ChunkSize, count);
            stream.Write(Zeros, 0, chunk);
            count -= chunk;
        }
    }

    /// <summary>
    /// A run of zero bytes as an array, for the callers that need to hand bytes to something rather
    /// than write them straight out. Still bounded — asking for more than one chunk is a mistake this
    /// type exists to prevent, so it says so rather than quietly allocating.
    /// </summary>
    public static byte[] Buffer(int count)
    {
        if (count > ChunkSize)
            throw new ArgumentOutOfRangeException(
                nameof(count), count, $"Use {nameof(Write)} for runs longer than {ChunkSize} bytes rather than allocating them.");

        return new byte[count];
    }
}
