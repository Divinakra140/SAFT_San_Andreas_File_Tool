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

    /// <summary>
    /// Identity of a file as far as its parsed contents are concerned. Two reads of the same path
    /// with the same length and the same last-write time cannot disagree, and any write SAFT makes -
    /// an in-place patch, a rebuild, a restore from backup - moves at least one of them.
    /// </summary>
    private readonly record struct FileIdentity(string Path, long Length, DateTime LastWriteUtc);

    private static FileIdentity? IdentityOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            return new FileIdentity(path, info.Length, info.LastWriteTimeUtc);
        }
        catch { return null; }
    }

    // One analysis pass opens the same handful of archives over and over: the plan indexes them, the
    // asset weighing reads them again, the binary .ipl reader opens them again, and the archive
    // search magic-checks every one of them each time it runs. On a real device that came to four
    // opens and three tree walks over models\gta3.img - a 940 MB file - inside a five second window,
    // for a game folder nothing had touched.
    //
    // That is the working set the process gets killed over. Both of these are pure functions of a
    // file's identity, so they are answered once and reused until the file itself changes. Keyed on
    // length and last-write time, so any write invalidates the entry rather than serving a stale
    // directory table - which would be far worse than the cost it saves.
    private static readonly Dictionary<FileIdentity, bool> MagicCache = new();
    private static readonly Dictionary<FileIdentity, IReadOnlyList<ImgEntry>> DirectoryCache = new();
    private static readonly object CacheLock = new();

    /// <summary>Returns true if the file at <paramref name="path"/> begins with the VER2 magic.</summary>
    public static bool IsImgArchive(string path)
    {
        var identity = IdentityOf(path);
        if (identity is { } id)
        {
            lock (CacheLock)
                if (MagicCache.TryGetValue(id, out var cached)) return cached;
        }

        bool result;
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 8) return false;
            Span<byte> header = stackalloc byte[4];
            var read = fs.Read(header);
            result = read == 4 && Encoding.ASCII.GetString(header) == Magic;
        }
        catch (IOException)
        {
            return false;
        }

        if (identity is { } toCache)
            lock (CacheLock) MagicCache[toCache] = result;

        return result;
    }

    /// <summary>
    /// The entry table alone, WITHOUT holding the archive open.
    ///
    /// Most readers of an archive never touch an entry's contents — they want the names, or the
    /// sizes. Weighing the game's assets is one, indexing what a mod could replace is another,
    /// listing what names the game already has is a third. Between them they opened all eight
    /// archives three times over during the checks, models\gta3.img among them, and a real device
    /// died on the log line "weigh: archive 6/8 models\gta3.img - opening" — four seconds into a
    /// fresh session, with the file open being the last thing it did.
    ///
    /// A cached directory table needs no file at all, so a warm call here touches nothing; a cold one
    /// opens, parses and closes immediately rather than keeping a 940 MB file open across the loop
    /// that follows. Same table, same cache, same invalidation - just without the handle.
    /// </summary>
    public static IReadOnlyList<ImgEntry> ReadDirectory(string path)
    {
        var identity = IdentityOf(path);
        if (identity is { } id)
        {
            lock (CacheLock)
                if (DirectoryCache.TryGetValue(id, out var cached)) return cached;
        }

        using var archive = Open(path);
        return archive.Entries;
    }

    public static ImgArchive Open(string path)
    {
        var identity = IdentityOf(path);
        if (identity is { } id)
        {
            IReadOnlyList<ImgEntry>? cached;
            lock (CacheLock) DirectoryCache.TryGetValue(id, out cached);

            if (cached is not null)
            {
                // The stream is still opened - entry contents are read through it on demand - but the
                // directory table behind it is not re-parsed.
                var reuseStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return new ImgArchive(path, reuseStream, cached);
            }
        }

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

            // Re-read the identity AFTER parsing: if the file changed while it was being read, the
            // identity taken beforehand would key this table against the wrong version of the file.
            var settled = IdentityOf(path);
            if (settled is { } key && key == identity)
                lock (CacheLock) DirectoryCache[key] = entries;

            return new ImgArchive(path, stream, entries);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Drops every cached magic check and directory table. Called by anything that writes an
    /// archive, and this IS load-bearing for correctness rather than merely tidy: file length and
    /// last-write time are not a reliable enough fingerprint on their own, because FAT32 records
    /// modification times to a two second granularity and a rebuild can land on the same length.
    /// </summary>
    public static void ClearCaches()
    {
        lock (CacheLock)
        {
            MagicCache.Clear();
            DirectoryCache.Clear();
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
    /// <paramref name="onFileWritten"/>, if given, is called periodically (every 25th file, plus
    /// always on the last one) as files are copied — the only step here slow enough (many small
    /// files) to need progress feedback, but throttled rather than called for literally every
    /// file, since posting tens of thousands of cross-thread UI updates in a tight loop can
    /// overwhelm the receiving thread faster than it can keep up.
    /// </summary>
    /// <summary>
    /// <paramref name="onFileMeasured"/> reports the sizing pass that runs before any bytes are
    /// written. It looks like it should be instant, but it opens every file in the archive — 16,000+
    /// for gta3.img — and on Windows each open goes through the virus scanner one at a time, which
    /// took minutes on a real install with the UI sitting on the previous stage's finished count.
    /// Silence there is indistinguishable from a hang, so it gets its own progress like every other
    /// stage that takes real time.
    /// </summary>
    /// <param name="onBytesWritten">
    /// The running total of bytes on disk, reported at the same throttled points as
    /// <paramref name="onFileWritten"/>. Used to notice when the storage underneath has slowed down —
    /// see <see cref="StorageSpeed"/>. It is a number this loop already has (the output stream's own
    /// position), so measuring it costs nothing and changes nothing about what gets written.
    /// </param>
    public static void Write(
        string destinationPath,
        IReadOnlyList<(string Name, Func<Stream> OpenContent)> files,
        Action<int, int>? onFileWritten = null,
        Action<int, int>? onFileMeasured = null,
        Action<long>? onBytesWritten = null)
    {
        // Any archive write invalidates every cached directory table. Length and last-write time
        // alone are not enough to lean on here: an SD card formatted FAT32 records modification
        // times to a two second granularity, so a rebuild that happened to land on the same file
        // length could be indistinguishable from the version already cached. Serving a stale
        // directory table would mean reading entries at the wrong offsets, which is exactly the kind
        // of harm SAFT must never do to somebody's install. Rebuilds are rare and scans are not, so
        // dropping everything on a write costs nothing worth measuring.
        ClearCaches();

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

            var measured = i + 1;
            if (onFileMeasured is not null && (measured == files.Count || measured % 25 == 0))
                onFileMeasured(measured, files.Count);
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

            // Reporting on every single file — tens of thousands of times for a big archive —
            // posts that many cross-thread UI updates in a tight loop. If the UI thread can't
            // drain that queue as fast as this one produces it (plausible under a slower/
            // constrained runtime), the backlog keeps growing until something gives out well
            // into the write, not at any fixed point — exactly the failure pattern this was
            // built to avoid. Throttled to a still-smooth-looking rate; always fires on the last
            // file so callers relying on the final callback (e.g. to reach 100%) still get it.
            var done = i + 1;
            if (done == files.Count || done % 25 == 0)
            {
                onFileWritten?.Invoke(done, files.Count);
                onBytesWritten?.Invoke(outStream.Position);
            }
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
