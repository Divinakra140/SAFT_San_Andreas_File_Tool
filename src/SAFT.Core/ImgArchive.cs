using System.Text;

namespace SAFT.Core;

/// <summary>One directory entry inside a GTA San Andreas VER2 IMG archive.</summary>
public sealed record ImgEntry(string Name, uint OffsetSectors, ushort SizeSectors)
{
    public const int SectorSize = 2048;
    public const int MaxNameLength = 23;

    public long ByteOffset => (long)OffsetSectors * SectorSize;
    public long ByteSize => (long)SizeSectors * SectorSize;

    public string Extension => GetBucketFolderName(Name);

    /// <summary>
    /// The extraction bucket subfolder a file with this name is placed in (lowercase extension,
    /// no dot; "misc" for extensionless names). Shared with <see cref="ModInstaller"/> so files
    /// it routes in land in exactly the folder <see cref="Extractor"/> would have put them in.
    /// </summary>
    public static string GetBucketFolderName(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..].ToLowerInvariant() : "misc";
    }
}

/// <summary>
/// Reads and writes GTA San Andreas "VER2" IMG archives.
/// Format: 4-byte magic "VER2", uint32 entry count, then that many 32-byte directory
/// entries (uint32 offset-in-sectors, uint16 size-in-sectors, uint16 unused, 24-byte
/// null-padded ASCII name), followed by sector-aligned (2048 bytes) file data in order.
/// </summary>
public sealed class ImgArchive : IDisposable
{
    public const string Magic = "VER2";
    public const int DirEntrySize = 32;
    public const int HeaderSize = 8;

    private readonly FileStream _stream;

    public IReadOnlyList<ImgEntry> Entries { get; }
    public string SourcePath { get; }

    private ImgArchive(string sourcePath, FileStream stream, IReadOnlyList<ImgEntry> entries)
    {
        SourcePath = sourcePath;
        _stream = stream;
        Entries = entries;
    }

    /// <summary>Returns true if the file at <paramref name="path"/> begins with the VER2 magic.</summary>
    public static bool IsImgArchive(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 8) return false;
            Span<byte> header = stackalloc byte[4];
            var read = fs.Read(header);
            return read == 4 && Encoding.ASCII.GetString(header) == Magic;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static ImgArchive Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

            var magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (magic != Magic)
                throw new InvalidDataException($"'{path}' is not a VER2 IMG archive (magic was '{magic}').");

            var count = reader.ReadUInt32();
            var entries = new List<ImgEntry>((int)count);
            for (var i = 0; i < count; i++)
            {
                var offset = reader.ReadUInt32();
                var streamingSize = reader.ReadUInt16();
                reader.ReadUInt16(); // legacy "size in archive" field, unused by VER2
                var nameBytes = reader.ReadBytes(24);
                var nullIndex = Array.IndexOf(nameBytes, (byte)0);
                var name = Encoding.ASCII.GetString(nameBytes, 0, nullIndex >= 0 ? nullIndex : nameBytes.Length);
                entries.Add(new ImgEntry(name, offset, streamingSize));
            }

            return new ImgArchive(path, stream, entries);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Opens a read-only stream over a single entry's raw sector-aligned bytes (including trailing padding).</summary>
    public Stream OpenEntry(ImgEntry entry)
    {
        return new SubStream(_stream, entry.ByteOffset, entry.ByteSize);
    }

    /// <summary>
    /// The exact on-disk size a VER2 archive containing <paramref name="fileLengths"/> would be:
    /// header + sector-aligned directory table + each file's content padded to a sector boundary.
    /// Used to show storage estimates before actually writing anything.
    /// </summary>
    public static long EstimateArchiveSize(int fileCount, IEnumerable<long> fileLengths)
    {
        const int sector = ImgEntry.SectorSize;
        var dirBytes = HeaderSize + fileCount * DirEntrySize;
        long total = ((dirBytes + sector - 1) / sector) * sector;
        foreach (var length in fileLengths)
            total += ((length + sector - 1) / sector) * sector;
        return total;
    }

    /// <summary>
    /// Writes a new VER2 archive to <paramref name="destinationPath"/> from a set of named byte sources,
    /// in the given order. Each source's length is padded up to the next 2048-byte sector boundary.
    /// <paramref name="onFileWritten"/>, if given, is called after each file's content is copied —
    /// the only step here slow enough (many small files) to need progress feedback.
    /// </summary>
    public static void Write(
        string destinationPath,
        IReadOnlyList<(string Name, Func<Stream> OpenContent)> files,
        Action<int, int>? onFileWritten = null)
    {
        foreach (var f in files)
        {
            if (Encoding.ASCII.GetByteCount(f.Name) > ImgEntry.MaxNameLength)
                throw new InvalidDataException(
                    $"Filename '{f.Name}' is too long for an IMG archive ({ImgEntry.MaxNameLength} ASCII characters max).");
        }

        var dirBytes = 8 + files.Count * DirEntrySize;
        var dirSectors = (dirBytes + ImgEntry.SectorSize - 1) / ImgEntry.SectorSize;

        using var outStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(outStream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes(Magic));
        writer.Write((uint)files.Count);

        var sizes = new ushort[files.Count];
        var offsets = new uint[files.Count];
        uint cursor = (uint)dirSectors;
        for (var i = 0; i < files.Count; i++)
        {
            using var content = files[i].OpenContent();
            var sectorCount = (uint)((content.Length + ImgEntry.SectorSize - 1) / ImgEntry.SectorSize);
            if (sectorCount > ushort.MaxValue)
                throw new InvalidDataException($"'{files[i].Name}' is too large for a single IMG entry.");

            offsets[i] = cursor;
            sizes[i] = (ushort)sectorCount;
            cursor += sectorCount;
        }

        for (var i = 0; i < files.Count; i++)
        {
            var nameBytes = new byte[24];
            Encoding.ASCII.GetBytes(files[i].Name).CopyTo(nameBytes, 0);

            writer.Write(offsets[i]);
            writer.Write(sizes[i]);
            writer.Write((ushort)0);
            writer.Write(nameBytes);
        }

        writer.Flush();
        PadToSector(outStream);

        for (var i = 0; i < files.Count; i++)
        {
            using var content = files[i].OpenContent();
            content.CopyTo(outStream);
            PadToSector(outStream);
            onFileWritten?.Invoke(i + 1, files.Count);
        }
    }

    private static void PadToSector(Stream stream)
    {
        var remainder = stream.Position % ImgEntry.SectorSize;
        if (remainder == 0) return;
        var padding = ImgEntry.SectorSize - remainder;
        stream.Write(new byte[padding]);
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>A read-only view over a fixed byte range of an underlying stream.</summary>
internal sealed class SubStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    public SubStream(Stream inner, long start, long length)
    {
        _inner = inner;
        _start = start;
        _length = length;
        _position = 0;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _length - _position;
        if (remaining <= 0) return 0;
        var toRead = (int)Math.Min(count, remaining);

        lock (_inner)
        {
            _inner.Position = _start + _position;
            var read = _inner.Read(buffer, offset, toRead);
            _position += read;
            return read;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
