using System.Text;

namespace SAFT.Core;

/// <summary>
/// Adds and removes archive entries WITHOUT rebuilding the archive.
///
/// EXPERIMENTAL. The safe path is <see cref="ImgArchive.Write"/>, which builds a whole new file and
/// renames it into place, so the original is never touched and an interruption at any instant leaves
/// it byte-identical. That safety is structural — it cannot be got wrong. Everything here is
/// PROCEDURAL: it edits the user's real archive, and it is safe only because of the order the writes
/// happen in. Read the ordering rules below before changing a single line of this file.
///
/// Why bother: adding or removing one entry costs a full rebuild today, because the directory table
/// sits at the front of the file and changing its length shifts every byte behind it. On a real
/// device that is 940 MB written to change seven entries, and writing gigabytes is what exhausts an
/// SD card's write buffer and slows the whole device to a crawl. Done in place, the same job writes a
/// few megabytes.
///
/// THE TWO RULES THAT MAKE THIS SAFE
///
/// 1. The entry count in the header is the ONLY thing that decides what exists. A reader takes the
///    count, then reads that many 32-byte records. Anything past the count is invisible to it. So
///    every operation here arranges the file completely, and then commits with a single four-byte
///    write to the count. Before that write, the archive is exactly as it was; after it, the change
///    is fully present. There is no half-applied state.
///
/// 2. The directory table NEVER grows beyond the sectors it already occupies. The table is padded
///    out to a 2048-byte boundary, which usually leaves a few spare record slots at the end. Entries
///    are added into that spare room, and if there is not enough, this refuses the job and the caller
///    rebuilds the old way. Growing the table would move all the data behind it, which is the very
///    thing being avoided.
///
/// On top of those, the original header and directory table — around half a megabyte, against the
/// archive's 940 — are copied to a sidecar file before anything is touched, and deleted once the
/// commit succeeds. A sidecar left behind means an operation was interrupted, and
/// <see cref="RecoverIfInterrupted"/> puts the table back exactly as it was.
/// </summary>
public static class ImgArchiveEditor
{
    private const int HeaderSize = 8;
    private const int CountOffset = 4;
    private const int RecordSize = ImgArchive.DirEntrySize;

    /// <summary>Where the original directory table is kept while an in-place edit is in progress.</summary>
    public const string SidecarSuffix = ".saft-index";

    private static readonly byte[] SidecarMagic = "SAFTIDX1"u8.ToArray();
    private const int SidecarHeaderSize = 8 + 1 + 4;   // magic, what commits it, the committed count

    /// <summary>What the commit of an in-place edit actually is, recorded so recovery can tell a job
    /// that finished from one that did not.</summary>
    private enum CommittedBy : byte
    {
        /// <summary>The entry count. Adding and removing both end with that one write.</summary>
        Count = 0,

        /// <summary>A directory record. Replacing contents changes no count, so there is no such marker.</summary>
        Record = 1,
    }

    /// <summary>What <see cref="RecoverIfInterrupted"/> found.</summary>
    public enum Recovery
    {
        /// <summary>No interrupted edit. The overwhelmingly normal answer.</summary>
        NotNeeded,

        /// <summary>
        /// The edit had actually finished — it was killed after committing but before tidying up. The
        /// archive is correct and must be LEFT ALONE; putting the old table back here would undo a
        /// completed install.
        /// </summary>
        AlreadyFinished,

        /// <summary>Put back. The archive was readable throughout; the mod is simply not installed.</summary>
        PutBack,

        /// <summary>
        /// Put back, but the table had a record pointing outside the file — the rare case where an
        /// interruption lands mid-record. Until this ran, that entry could have shown up in the game
        /// as a missing object or a crash near it.
        /// </summary>
        PutBackAfterDamage,
    }

    /// <summary>What an in-place attempt did, or why it declined and left the archive alone.</summary>
    public enum Outcome
    {
        /// <summary>The archive was edited in place and committed.</summary>
        Done,

        /// <summary>Nothing to do — none of the named entries were present, or no files were given.</summary>
        NothingToDo,

        /// <summary>Not possible in place. The caller must rebuild; the archive is untouched.</summary>
        NotPossible,
    }

    /// <summary>
    /// How many more entries this archive can take without its directory table needing another
    /// sector — which is to say, without a rebuild.
    /// </summary>
    public static int SpareSlots(string archivePath)
    {
        using var stream = File.OpenRead(archivePath);
        var (count, tableSectors) = ReadHeader(stream);
        return SlotCapacity(tableSectors) - count;
    }

    /// <summary>
    /// Bytes in the file that no entry points at — the remains of entries removed in place. Nothing
    /// reads them and the game never sees them, but they are why an archive edited this way stops
    /// shrinking, and the number a caller uses to decide when a real rebuild is due.
    /// </summary>
    public static long DeadBytes(string archivePath)
    {
        using var stream = File.OpenRead(archivePath);
        var (count, tableSectors) = ReadHeader(stream);
        var records = ReadRecords(stream, count);

        var live = (long)tableSectors * ImgEntry.SectorSize;
        foreach (var record in records) live += record.SizeSectors * (long)ImgEntry.SectorSize;

        return Math.Max(0, stream.Length - live);
    }

    /// <summary>
    /// Rewrites an archive with its entries packed end to end, giving back every byte of dead space.
    ///
    /// Editing in place is what makes SAFT fast, and dead space is the price of it. Two things leave
    /// it behind: removing an entry drops it from the directory table but not from the file, and
    /// restoring a small original over a large modded entry shrinks the entry's size field and
    /// strands the remainder. Neither is ever reclaimed on its own, so a game that has had a heavy
    /// mod installed and then removed keeps the space forever — measured at 149,504 bytes across six
    /// holes after a single install-and-uninstall of one small mod, none of it reachable by
    /// truncation because every hole was in the middle.
    ///
    /// This is deliberately the dumb, proven method rather than a clever one: the same full rewrite
    /// that SAFT has always used, streaming each entry into a fresh file and swapping it in at the
    /// end. It costs a full pass over the archive, which is why it belongs on uninstall — a rare
    /// operation, where the user has already accepted a wait, and where they are specifically
    /// expecting to get space back.
    ///
    /// Safe to be interrupted: the original is untouched until the replacement is complete, and the
    /// swap is a rename.
    /// </summary>
    /// <returns>Bytes reclaimed. Zero means there was nothing to reclaim and nothing was written.</returns>
    public static long Compact(string archivePath, Action<string>? onStep = null, StorageSpeed? speed = null)
    {
        long dead;
        try
        {
            dead = DeadBytes(archivePath);
        }
        catch (Exception ex)
        {
            // Not being able to measure is not a reason to fail an uninstall that has otherwise
            // worked. The space stays used; nothing is damaged.
            onStep?.Invoke($"archive: could not measure unused space in {Path.GetFileName(archivePath)} ({ex.GetType().Name})");
            return 0;
        }

        if (dead <= 0) return 0;

        onStep?.Invoke(
            $"archive: {dead / 1048576.0:0.0} MB of {Path.GetFileName(archivePath)} is unused space - packing it out");

        var tempPath = archivePath + ".saft-tmp";
        if (File.Exists(tempPath)) File.Delete(tempPath);

        using (var archive = ImgArchive.Open(archivePath))
        {
            // OpenEntry hands back the entry's sector-aligned bytes, so each one lands in the new
            // file at exactly the size its directory record already claims. Packing them end to end
            // is the whole of the work — nothing is re-encoded, and no entry changes size.
            var files = archive.Entries
                .Select(entry => (entry.Name, (Func<Stream>)(() => archive.OpenEntry(entry))))
                .ToList();

            ImgArchive.Write(tempPath, files, onBytesWritten: speed is null ? null : speed.Sample);
        }

        ImgArchive.ClearCaches();
        FileReplace.MoveOver(tempPath, archivePath);

        onStep?.Invoke($"archive: reclaimed {dead / 1048576.0:0.0} MB");
        return dead;
    }

    /// <summary>
    /// Removes entries by name, leaving their data in the file as dead space.
    ///
    /// The survivors are written back into the front of the table in their existing order, and only
    /// then is the count lowered. Both halves of that are safe to be interrupted: a survivor is never
    /// overwritten before its copy has been placed, so the worst an interruption can leave is a
    /// record that appears twice — two names pointing at the same, valid data — in an archive whose
    /// count still says everything is present. That reads correctly. The commit is the count.
    /// </summary>
    public static Outcome TryRemove(string archivePath, IReadOnlyCollection<string> names, out int removed)
    {
        removed = 0;
        if (names.Count == 0) return Outcome.NothingToDo;
        if (!File.Exists(archivePath)) return Outcome.NotPossible;

        RecoverIfInterrupted(archivePath);

        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

        using (var stream = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var (count, tableSectors) = ReadHeader(stream);
            var records = ReadRecords(stream, count);

            var survivors = records.Where(r => !wanted.Contains(r.Name)).ToList();
            removed = records.Count - survivors.Count;
            if (removed == 0) return Outcome.NothingToDo;

            WriteSidecar(archivePath, stream, tableSectors, CommittedBy.Count, survivors.Count);

            // Survivors first, in order, into slots 0..n-1. Every record here is written over a slot
            // holding either itself, or a record being removed, or a survivor already copied to an
            // earlier slot - never over one still waiting to be placed.
            stream.Position = HeaderSize;
            var buffer = new byte[RecordSize];
            foreach (var survivor in survivors)
            {
                WriteRecord(buffer, survivor);
                stream.Write(buffer);
            }
            stream.Flush(flushToDisk: true);

            // Commit. Everything above this line is invisible to a reader; everything below it is done.
            WriteCount(stream, survivors.Count);
            stream.Flush(flushToDisk: true);
        }

        Finish(archivePath);
        return Outcome.Done;
    }

    /// <summary>
    /// Appends new entries, writing their data past the end of the file and their records into the
    /// directory table's spare slots.
    ///
    /// The data goes in first, where nothing points at it and no reader can see it. The records go
    /// into slots beyond the live count, which no reader looks at either. Only the final count write
    /// makes any of it real. Declines - leaving the archive untouched - if there are not enough spare
    /// slots, if a name is too long, if a name is already in the archive, or if a file is too big for
    /// one entry.
    /// </summary>
    public static Outcome TryAppend(
        string archivePath, IReadOnlyList<(string Name, Func<Stream> OpenContent)> files)
    {
        if (files.Count == 0) return Outcome.NothingToDo;
        if (!File.Exists(archivePath)) return Outcome.NotPossible;

        RecoverIfInterrupted(archivePath);

        using (var stream = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var (count, tableSectors) = ReadHeader(stream);
            if (SlotCapacity(tableSectors) - count < files.Count) return Outcome.NotPossible;

            foreach (var file in files)
                if (Encoding.ASCII.GetByteCount(file.Name) > ImgEntry.MaxNameLength) return Outcome.NotPossible;

            var existing = new HashSet<string>(
                ReadRecords(stream, count).Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
                if (existing.Contains(file.Name)) return Outcome.NotPossible;

            // Data is always sector-aligned, so the end of the file is always the start of a sector.
            // Anything else means this is not an archive SAFT wrote, and is not worth guessing about.
            if (stream.Length % ImgEntry.SectorSize != 0) return Outcome.NotPossible;

            WriteSidecar(archivePath, stream, tableSectors, CommittedBy.Count, count + files.Count);

            var added = new List<ImgEntry>(files.Count);
            stream.Position = stream.Length;

            foreach (var (name, openContent) in files)
            {
                var offsetSectors = stream.Position / ImgEntry.SectorSize;
                if (offsetSectors > uint.MaxValue) return Abandon(archivePath, stream);

                using (var content = openContent())
                {
                    var sectors = (content.Length + ImgEntry.SectorSize - 1) / ImgEntry.SectorSize;
                    if (sectors > ushort.MaxValue) return Abandon(archivePath, stream);

                    content.CopyTo(stream);
                    PadToSector(stream);
                    added.Add(new ImgEntry(name, (uint)offsetSectors, (ushort)sectors));
                }
            }

            stream.Flush(flushToDisk: true);

            // Records go into slots the count does not yet reach, so they are still invisible here.
            var buffer = new byte[RecordSize];
            stream.Position = HeaderSize + (long)count * RecordSize;
            foreach (var entry in added)
            {
                WriteRecord(buffer, entry);
                stream.Write(buffer);
            }
            stream.Flush(flushToDisk: true);

            // Commit.
            WriteCount(stream, count + added.Count);
            stream.Flush(flushToDisk: true);
        }

        Finish(archivePath);
        return Outcome.Done;
    }

    /// <summary>
    /// Replaces the CONTENTS of entries that already exist, by writing the new data past the end of
    /// the file and pointing their records at it. The old data stays where it is, as dead space.
    ///
    /// This is the one operation here that changes a record a reader is already using, so it is the
    /// one with a genuinely new failure mode: a record half written. It is 32 bytes inside a single
    /// sector, which storage does not normally tear, and the sidecar puts the whole table back if it
    /// ever does - but that is a mitigation, not the structural guarantee the rebuild path has, and
    /// it is the reason this whole file is a prototype rather than the default.
    ///
    /// What makes the rest of it safe is that data is only ever APPENDED. Every record, whether it
    /// has been updated yet or not, points at bytes that are present and correct - the old copy or
    /// the new one - so a table caught half way through still reads.
    /// </summary>
    public static Outcome TryReplace(
        string archivePath, IReadOnlyList<(string Name, Func<Stream> OpenContent)> replacements)
    {
        if (replacements.Count == 0) return Outcome.NothingToDo;
        if (!File.Exists(archivePath)) return Outcome.NotPossible;

        RecoverIfInterrupted(archivePath);

        using (var stream = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var (count, tableSectors) = ReadHeader(stream);
            var records = ReadRecords(stream, count);

            var slots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < records.Count; i++) slots.TryAdd(records[i].Name, i);

            foreach (var (name, _) in replacements)
                if (!slots.ContainsKey(name)) return Outcome.NotPossible;

            if (stream.Length % ImgEntry.SectorSize != 0) return Outcome.NotPossible;

            // Replacing commits by moving a record, not by changing the count, so there is no count
            // that means "finished" - an interrupted replace is always put back.
            WriteSidecar(archivePath, stream, tableSectors, CommittedBy.Record, count);

            var updated = new List<(int Slot, ImgEntry Entry)>(replacements.Count);
            stream.Position = stream.Length;

            foreach (var (name, openContent) in replacements)
            {
                var offsetSectors = stream.Position / ImgEntry.SectorSize;
                if (offsetSectors > uint.MaxValue) return Abandon(archivePath, stream);

                using var content = openContent();
                var sectors = (content.Length + ImgEntry.SectorSize - 1) / ImgEntry.SectorSize;
                if (sectors > ushort.MaxValue) return Abandon(archivePath, stream);

                content.CopyTo(stream);
                PadToSector(stream);
                updated.Add((slots[name], new ImgEntry(name, (uint)offsetSectors, (ushort)sectors)));
            }

            // Every byte of new content is on disk before any record is moved onto it.
            stream.Flush(flushToDisk: true);

            var buffer = new byte[RecordSize];
            foreach (var (slot, entry) in updated)
            {
                stream.Position = HeaderSize + (long)slot * RecordSize;
                WriteRecord(buffer, entry);
                stream.Write(buffer);
            }
            stream.Flush(flushToDisk: true);
        }

        Finish(archivePath);
        return Outcome.Done;
    }

    /// <summary>
    /// Puts the directory table back if an in-place edit was interrupted before it committed.
    ///
    /// A sidecar only exists between the first write and the commit. Restoring it returns the archive
    /// to exactly the state it was in before the operation started: entries that were being removed
    /// come back, and data that was appended becomes dead space nothing points at. Both are states
    /// the game reads perfectly well.
    ///
    /// Safe to call at any time, and safe to be interrupted itself - it rewrites the same bytes from
    /// the same source every time, so running it again finishes the job.
    /// </summary>
    public static Recovery RecoverIfInterrupted(string archivePath)
    {
        var sidecarPath = archivePath + SidecarSuffix;
        if (!File.Exists(sidecarPath) || !File.Exists(archivePath)) return Recovery.NotNeeded;

        var saved = File.ReadAllBytes(sidecarPath);
        if (saved.Length <= SidecarHeaderSize ||
            !saved.AsSpan(0, SidecarMagic.Length).SequenceEqual(SidecarMagic) ||
            (saved.Length - SidecarHeaderSize) % ImgEntry.SectorSize != 0)
        {
            // Not a sidecar this build wrote, or not a whole table. It cannot be trusted to restore
            // anything, so the archive is left exactly as it is - and the file goes, so no later run
            // trusts it more than this one did.
            File.Delete(sidecarPath);
            return Recovery.NotNeeded;
        }

        var commit = (CommittedBy)saved[SidecarMagic.Length];
        var committedCount = (int)BitConverter.ToUInt32(saved, SidecarMagic.Length + 1);
        var table = saved.AsSpan(SidecarHeaderSize);

        using (var stream = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            if (stream.Length < table.Length) return Recovery.NotNeeded;

            var (currentCount, _) = ReadHeader(stream);

            // The operation actually finished - it was killed between committing and tidying up.
            // Putting the old table back here would undo a completed install.
            if (commit == CommittedBy.Count && currentCount == committedCount)
            {
                stream.Dispose();
                File.Delete(sidecarPath);
                ImgArchive.ClearCaches();
                return Recovery.AlreadyFinished;
            }

            // Did the interruption land somewhere that the game could have noticed? Every live record
            // has to point at bytes that exist. One that does not is the rare mid-record case, and it
            // is the difference between "your mod did not install" and "something in your game may
            // have looked broken until now".
            var damaged = ReadRecords(stream, currentCount)
                .Any(r => r.ByteOffset < 0 || r.ByteOffset + r.ByteSize > stream.Length);

            stream.Position = 0;
            stream.Write(table);
            stream.Flush(flushToDisk: true);

            File.Delete(sidecarPath);
            ImgArchive.ClearCaches();
            return damaged ? Recovery.PutBackAfterDamage : Recovery.PutBack;
        }
    }

    /// <summary>Every archive in a folder that was left mid-edit, put back. Returns how many.</summary>
    public static (int Count, Recovery Worst) RecoverAll(
        string gameRoot, GameFiles? files = null, Action<string>? onStep = null)
    {
        var recovered = 0;
        var worst = Recovery.NotNeeded;

        foreach (var path in GameFiles.For(gameRoot, files).Paths)
        {
            if (!path.EndsWith(SidecarSuffix, StringComparison.OrdinalIgnoreCase)) continue;

            var archivePath = path[..^SidecarSuffix.Length];
            var name = Path.GetFileName(archivePath);
            onStep?.Invoke($"recover: an archive edit was interrupted, checking {name}");

            var outcome = RecoverIfInterrupted(archivePath);
            onStep?.Invoke($"recover: {name} - {outcome}");

            if (outcome is Recovery.PutBack or Recovery.PutBackAfterDamage) recovered++;
            if (outcome > worst) worst = outcome;
        }

        return (recovered, worst);
    }

    // ---------------------------------------------------------------------------------------------

    private static int SlotCapacity(int tableSectors) =>
        (tableSectors * ImgEntry.SectorSize - HeaderSize) / RecordSize;

    private static (int Count, int TableSectors) ReadHeader(Stream stream)
    {
        stream.Position = 0;
        Span<byte> header = stackalloc byte[HeaderSize];
        if (stream.Read(header) != HeaderSize) throw new InvalidDataException("Archive is too short to be a VER2 IMG.");
        if (Encoding.ASCII.GetString(header[..4]) != ImgArchive.Magic)
            throw new InvalidDataException("Not a VER2 IMG archive.");

        var count = (int)BitConverter.ToUInt32(header[4..]);
        var tableBytes = HeaderSize + (long)count * RecordSize;
        var tableSectors = (int)((tableBytes + ImgEntry.SectorSize - 1) / ImgEntry.SectorSize);
        return (count, tableSectors);
    }

    private static List<ImgEntry> ReadRecords(Stream stream, int count)
    {
        stream.Position = HeaderSize;
        var records = new List<ImgEntry>(count);
        var buffer = new byte[RecordSize];

        for (var i = 0; i < count; i++)
        {
            if (stream.Read(buffer) != RecordSize) break;
            var offset = BitConverter.ToUInt32(buffer, 0);
            var size = BitConverter.ToUInt16(buffer, 4);
            var end = Array.IndexOf(buffer, (byte)0, 8, 24);
            var name = Encoding.ASCII.GetString(buffer, 8, (end < 0 ? 32 : end) - 8);
            records.Add(new ImgEntry(name, offset, size));
        }

        return records;
    }

    private static void WriteRecord(byte[] buffer, ImgEntry entry)
    {
        Array.Clear(buffer);
        BitConverter.GetBytes(entry.OffsetSectors).CopyTo(buffer, 0);
        BitConverter.GetBytes(entry.SizeSectors).CopyTo(buffer, 4);
        BitConverter.GetBytes(entry.SizeSectors).CopyTo(buffer, 6);   // legacy field, written to match ImgArchive.Write
        Encoding.ASCII.GetBytes(entry.Name).CopyTo(buffer, 8);
    }

    private static void WriteCount(Stream stream, int count)
    {
        stream.Position = CountOffset;
        stream.Write(BitConverter.GetBytes((uint)count));
    }

    /// <summary>
    /// Copies the header and directory table aside before anything is changed, along with what a
    /// FINISHED version of this operation looks like.
    ///
    /// That last part matters more than it sounds. The sidecar is deleted after the commit, so a
    /// process killed in the gap between those two writes leaves a sidecar behind for an operation
    /// that actually succeeded - and blindly restoring it would quietly undo a completed install.
    /// Recording the committed count lets recovery tell the two apart.
    /// </summary>
    private static void WriteSidecar(
        string archivePath, Stream stream, int tableSectors, CommittedBy commit, int committedCount)
    {
        var table = new byte[tableSectors * ImgEntry.SectorSize];
        stream.Position = 0;
        stream.ReadExactly(table);

        using var sidecar = new FileStream(
            archivePath + SidecarSuffix, FileMode.Create, FileAccess.Write, FileShare.None);
        sidecar.Write(SidecarMagic);
        sidecar.WriteByte((byte)commit);
        sidecar.Write(BitConverter.GetBytes((uint)committedCount));
        sidecar.Write(table);
        sidecar.Flush(flushToDisk: true);
    }

    /// <summary>Commit succeeded: drop the safety net and the stale directory tables.</summary>
    private static void Finish(string archivePath)
    {
        var sidecarPath = archivePath + SidecarSuffix;
        if (File.Exists(sidecarPath)) File.Delete(sidecarPath);
        ImgArchive.ClearCaches();
    }

    /// <summary>
    /// Something was refused half way through appending. The count has not been touched, so the
    /// archive is still exactly what it was - but the file has grown, so the sidecar stays until the
    /// table is confirmed intact by the recovery pass.
    /// </summary>
    private static Outcome Abandon(string archivePath, FileStream stream)
    {
        stream.Flush(flushToDisk: true);
        stream.Dispose();
        RecoverIfInterrupted(archivePath);
        return Outcome.NotPossible;
    }

    private static void PadToSector(Stream stream)
    {
        var remainder = stream.Position % ImgEntry.SectorSize;
        if (remainder == 0) return;
        stream.Write(new byte[ImgEntry.SectorSize - remainder]);
    }
}
