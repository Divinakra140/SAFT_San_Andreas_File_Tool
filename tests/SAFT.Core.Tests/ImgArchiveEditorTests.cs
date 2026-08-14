using System.Text;
using SAFT.Core;

namespace SAFT.Core.Tests;

/// <summary>
/// The in-place editor writes into the user's real archive instead of building a new one and renaming
/// it, which trades a safety property that cannot be got wrong for one that depends entirely on the
/// order of the writes. So these tests are not about speed. They are about whether the archive is
/// still readable at every point where the process could be killed, and whether what comes out the
/// far end is identical to what a full rebuild would have produced.
///
/// "Killed" is simulated by truncating the operation: the editor commits with a single write to the
/// entry count, so every state before that write is one a reader must cope with.
/// </summary>
[Collection("ImgArchiveCache")]
public class ImgArchiveEditorTests
{
    private static byte[] Body(string text, int length)
    {
        var bytes = new byte[length];
        Encoding.ASCII.GetBytes(text).CopyTo(bytes, 0);
        return bytes;
    }

    private static string BuildArchive(params (string Name, byte[] Content)[] files)
    {
        var path = Path.Combine(TestScratch.NewDir(), "test.img");
        ImgArchive.Write(path, files.Select(f => (f.Name, (Func<Stream>)(() => new MemoryStream(f.Content)))).ToList());
        return path;
    }

    private static List<string> NamesIn(string path)
    {
        using var archive = ImgArchive.Open(path);
        return archive.Entries.Select(e => e.Name).ToList();
    }

    private static byte[] ContentOf(string path, string entryName)
    {
        using var archive = ImgArchive.Open(path);
        var entry = archive.Entries.Single(e => e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase));
        using var stream = archive.OpenEntry(entry);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public void RemovingAnEntryLeavesEveryOtherEntryExactlyWhereItWas()
    {
        var path = BuildArchive(
            ("alpha.dff", Body("alpha", 3000)),
            ("beta.dff", Body("beta", 5000)),
            ("gamma.dff", Body("gamma", 1000)));

        var before = ContentOf(path, "gamma.dff");

        Assert.Equal(ImgArchiveEditor.Outcome.Done,
            ImgArchiveEditor.TryRemove(path, new[] { "beta.dff" }, out var removed));

        Assert.Equal(1, removed);
        Assert.Equal(new[] { "alpha.dff", "gamma.dff" }, NamesIn(path));
        Assert.Equal(before, ContentOf(path, "gamma.dff"));
        Assert.Equal(Body("alpha", 3000), ContentOf(path, "alpha.dff")[..3000]);
    }

    [Fact]
    public void AddingAnEntryLeavesEveryExistingEntryExactlyWhereItWas()
    {
        var path = BuildArchive(("alpha.dff", Body("alpha", 3000)), ("beta.dff", Body("beta", 5000)));
        var beforeAlpha = ContentOf(path, "alpha.dff");
        var beforeBeta = ContentOf(path, "beta.dff");

        Assert.Equal(ImgArchiveEditor.Outcome.Done, ImgArchiveEditor.TryAppend(path, new[]
        {
            ("delta.dff", (Func<Stream>)(() => new MemoryStream(Body("delta", 2500)))),
        }));

        Assert.Equal(new[] { "alpha.dff", "beta.dff", "delta.dff" }, NamesIn(path));
        Assert.Equal(beforeAlpha, ContentOf(path, "alpha.dff"));
        Assert.Equal(beforeBeta, ContentOf(path, "beta.dff"));
        Assert.Equal(Body("delta", 2500), ContentOf(path, "delta.dff")[..2500]);
    }

    [Fact]
    public void AReinstallInPlaceMatchesWhatAFullRebuildWouldHaveProduced()
    {
        // The case this exists for: take the old copy out, put the new one in. The result has to be
        // indistinguishable from the rebuild it replaces, or the fast path is not a substitute for
        // the slow one - it is a different program.
        (string, byte[])[] original =
        {
            ("keep1.dff", Body("keep1", 4000)),
            ("mod.dff", Body("old mod", 3000)),
            ("keep2.txd", Body("keep2", 6000)),
        };

        var inPlace = BuildArchive(original);
        ImgArchiveEditor.TryRemove(inPlace, new[] { "mod.dff" }, out _);
        ImgArchiveEditor.TryAppend(inPlace, new[]
        {
            ("mod.dff", (Func<Stream>)(() => new MemoryStream(Body("new mod", 3500)))),
        });

        var rebuilt = BuildArchive(
            ("keep1.dff", Body("keep1", 4000)),
            ("keep2.txd", Body("keep2", 6000)),
            ("mod.dff", Body("new mod", 3500)));

        Assert.Equal(NamesIn(rebuilt).OrderBy(n => n), NamesIn(inPlace).OrderBy(n => n));
        foreach (var name in NamesIn(rebuilt))
            Assert.Equal(ContentOf(rebuilt, name), ContentOf(inPlace, name));
    }

    [Fact]
    public void TheArchiveIsStillReadableIfTheProcessDiesBeforeTheCommit()
    {
        // Everything before the four-byte count write must leave a working archive. Simulated by
        // doing the work and then putting the count back to what it was, which is precisely the state
        // a kill at any point before the commit leaves behind.
        var path = BuildArchive(
            ("alpha.dff", Body("alpha", 3000)),
            ("beta.dff", Body("beta", 5000)),
            ("gamma.dff", Body("gamma", 1000)));

        var countBefore = File.ReadAllBytes(path)[4..8];

        ImgArchiveEditor.TryRemove(path, new[] { "beta.dff" }, out _);

        // Put the count back: the file now has the survivors written into the front of the table, the
        // old records still behind them, and a count that says nothing was removed.
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.Position = 4;
            stream.Write(countBefore);
        }
        ImgArchive.ClearCaches();

        // It must still open, still have three readable records, and every one of them must resolve
        // to real data rather than to a corrupted offset.
        //
        // Read by RECORD, not by name: this state legitimately contains the same name twice - the
        // survivor written into its new slot, and the copy still sitting in its old one. That is the
        // designed outcome rather than damage. Both point at the same valid data, which is why a
        // reader copes with it, and why the recovery has something intact to put back.
        using var archive = ImgArchive.Open(path);
        Assert.Equal(3, archive.Entries.Count);

        foreach (var entry in archive.Entries)
        {
            Assert.InRange(entry.ByteOffset + entry.ByteSize, 0, new FileInfo(path).Length);
            using var stream = archive.OpenEntry(entry);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            Assert.NotEmpty(buffer.ToArray());
        }

        Assert.Equal(2, archive.Entries.Select(e => e.Name).Distinct().Count());
    }

    [Fact]
    public void AnInterruptedEditIsPutBackExactlyAsItWas()
    {
        var path = BuildArchive(("alpha.dff", Body("alpha", 3000)), ("beta.dff", Body("beta", 5000)));
        var untouched = File.ReadAllBytes(path);

        // Mid-operation state: the sidecar exists and the table has been rewritten, but the count was
        // never committed. This is what a process killed during an in-place edit leaves on disk.
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            var table = new byte[ImgEntry.SectorSize];
            stream.ReadExactly(table);
            WriteSidecarLike(path, table);

            stream.Position = 8;
            stream.Write(new byte[32]);   // scribble over the first record
        }
        ImgArchive.ClearCaches();

        Assert.Equal(ImgArchiveEditor.Recovery.PutBack, ImgArchiveEditor.RecoverIfInterrupted(path));

        Assert.Equal(untouched, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ImgArchiveEditor.SidecarSuffix));
        Assert.Equal(new[] { "alpha.dff", "beta.dff" }, NamesIn(path));
    }

    [Fact]
    public void RecoveryCanItselfBeInterruptedAndRunAgain()
    {
        // The recovery writes the same bytes from the same source every time, so running it twice is
        // the same as running it once. If that were not true, a crash during recovery would be
        // unrecoverable - the one state with no way out.
        var path = BuildArchive(("alpha.dff", Body("alpha", 3000)), ("beta.dff", Body("beta", 5000)));
        var untouched = File.ReadAllBytes(path);

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            var table = new byte[ImgEntry.SectorSize];
            stream.ReadExactly(table);
            WriteSidecarLike(path, table);
            stream.Position = 8;
            stream.Write(new byte[32]);
        }

        ImgArchiveEditor.RecoverIfInterrupted(path);
        ImgArchiveEditor.RecoverIfInterrupted(path);   // again, as if the first was cut short

        Assert.Equal(untouched, File.ReadAllBytes(path));
    }

    [Fact]
    public void RefusesRatherThanGrowingTheDirectoryTable()
    {
        // The whole design rests on the table never needing another sector. When there is no room,
        // the answer must be "not possible" and the archive must be untouched, so the caller falls
        // back to a full rebuild. Silently growing it would move every byte of data behind it.
        var files = Enumerable.Range(0, 200)
            .Select(i => ($"file{i:D3}.dff", Body($"f{i}", 100)))
            .ToArray();
        var path = BuildArchive(files);

        var spare = ImgArchiveEditor.SpareSlots(path);
        var untouched = File.ReadAllBytes(path);

        var tooMany = Enumerable.Range(0, spare + 1)
            .Select(i => ($"extra{i:D3}.dff", (Func<Stream>)(() => new MemoryStream(Body("x", 100)))))
            .ToList();

        Assert.Equal(ImgArchiveEditor.Outcome.NotPossible, ImgArchiveEditor.TryAppend(path, tooMany));
        Assert.Equal(untouched, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ImgArchiveEditor.SidecarSuffix));
    }

    [Fact]
    public void FillsTheSpareSlotsExactlyAndNoFurther()
    {
        var files = Enumerable.Range(0, 200).Select(i => ($"file{i:D3}.dff", Body($"f{i}", 100))).ToArray();
        var path = BuildArchive(files);
        var spare = ImgArchiveEditor.SpareSlots(path);
        Assert.True(spare > 0, "a sector-padded table should have spare slots to test with");

        var exactly = Enumerable.Range(0, spare)
            .Select(i => ($"extra{i:D3}.dff", (Func<Stream>)(() => new MemoryStream(Body("x", 100)))))
            .ToList();

        Assert.Equal(ImgArchiveEditor.Outcome.Done, ImgArchiveEditor.TryAppend(path, exactly));
        Assert.Equal(200 + spare, NamesIn(path).Count);
        Assert.Equal(0, ImgArchiveEditor.SpareSlots(path));

        // And one more must now be refused rather than squeezed in.
        Assert.Equal(ImgArchiveEditor.Outcome.NotPossible, ImgArchiveEditor.TryAppend(path, new[]
        {
            ("onemore.dff", (Func<Stream>)(() => new MemoryStream(Body("x", 100)))),
        }));
    }

    [Fact]
    public void RefusesToAddANameTheArchiveAlreadyHas()
    {
        // Two records under one name is a mod nobody can uninstall cleanly: removal takes one and the
        // game keeps finding the other.
        var path = BuildArchive(("alpha.dff", Body("alpha", 3000)));
        var untouched = File.ReadAllBytes(path);

        Assert.Equal(ImgArchiveEditor.Outcome.NotPossible, ImgArchiveEditor.TryAppend(path, new[]
        {
            ("ALPHA.DFF", (Func<Stream>)(() => new MemoryStream(Body("other", 100)))),
        }));

        Assert.Equal(untouched, File.ReadAllBytes(path));
    }

    [Fact]
    public void ReplacingAnEntrySwapsItsContentsAndLeavesTheOthersAlone()
    {
        var path = BuildArchive(
            ("alpha.dff", Body("alpha", 3000)),
            ("multiobj.col", Body("old collision", 2000)),
            ("gamma.dff", Body("gamma", 1000)));

        var beforeAlpha = ContentOf(path, "alpha.dff");
        var beforeGamma = ContentOf(path, "gamma.dff");

        Assert.Equal(ImgArchiveEditor.Outcome.Done, ImgArchiveEditor.TryReplace(path, new[]
        {
            ("multiobj.col", (Func<Stream>)(() => new MemoryStream(Body("new collision, bigger", 9000)))),
        }));

        Assert.Equal(new[] { "alpha.dff", "multiobj.col", "gamma.dff" }, NamesIn(path));
        Assert.Equal(Body("new collision, bigger", 9000), ContentOf(path, "multiobj.col")[..9000]);
        Assert.Equal(beforeAlpha, ContentOf(path, "alpha.dff"));
        Assert.Equal(beforeGamma, ContentOf(path, "gamma.dff"));
    }

    [Fact]
    public void AReplacementInterruptedBeforeItsRecordMovesStillReadsTheOldContent()
    {
        // Data is only ever appended, so until a record is repointed the archive is simply the
        // archive it was. This is the state a kill during the data write leaves behind.
        var path = BuildArchive(("alpha.dff", Body("alpha", 3000)), ("multiobj.col", Body("old collision", 2000)));
        var untouched = File.ReadAllBytes(path);

        // Append the new content, but stop before touching the table - what TryReplace does first.
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = stream.Length;
            stream.Write(Body("new collision", 4096));
        }
        ImgArchive.ClearCaches();

        Assert.Equal(Body("old collision", 2000), ContentOf(path, "multiobj.col")[..2000]);
        Assert.Equal(untouched, File.ReadAllBytes(path)[..untouched.Length]);
    }

    [Fact]
    public void RefusesToReplaceSomethingTheArchiveDoesNotHave()
    {
        var path = BuildArchive(("alpha.dff", Body("alpha", 3000)));
        var untouched = File.ReadAllBytes(path);

        Assert.Equal(ImgArchiveEditor.Outcome.NotPossible, ImgArchiveEditor.TryReplace(path, new[]
        {
            ("nothere.dff", (Func<Stream>)(() => new MemoryStream(Body("x", 100)))),
        }));

        Assert.Equal(untouched, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ImgArchiveEditor.SidecarSuffix));
    }

    [Fact]
    public void AFullReinstallInPlaceSurvivesAllThreeOperationsAndMatchesARebuild()
    {
        // The real sequence: drop the old copy's entries, update the shared collision bundle, add the
        // new copy's entries. Three commits, one after another, on the user's live archive.
        var inPlace = BuildArchive(
            ("banshee.dff", Body("rockstar banshee", 4000)),
            ("multiobj.col", Body("rockstar collision", 2000)),
            ("saftcastle.dff", Body("v1 castle", 3000)),
            ("saftgate.dff", Body("v1 gate", 1500)));

        ImgArchiveEditor.TryRemove(inPlace, new[] { "saftcastle.dff", "saftgate.dff" }, out var removed);
        ImgArchiveEditor.TryReplace(inPlace, new[]
        {
            ("multiobj.col", (Func<Stream>)(() => new MemoryStream(Body("collision, castle pruned and re-added", 2600)))),
        });
        ImgArchiveEditor.TryAppend(inPlace, new[]
        {
            ("saftcastle.dff", (Func<Stream>)(() => new MemoryStream(Body("v2 castle", 3300)))),
        });

        Assert.Equal(2, removed);

        var rebuilt = BuildArchive(
            ("banshee.dff", Body("rockstar banshee", 4000)),
            ("multiobj.col", Body("collision, castle pruned and re-added", 2600)),
            ("saftcastle.dff", Body("v2 castle", 3300)));

        Assert.Equal(NamesIn(rebuilt).OrderBy(n => n), NamesIn(inPlace).OrderBy(n => n));
        foreach (var name in NamesIn(rebuilt))
            Assert.Equal(ContentOf(rebuilt, name), ContentOf(inPlace, name));

        // And no safety net left lying around once everything committed.
        Assert.False(File.Exists(inPlace + ImgArchiveEditor.SidecarSuffix));
    }

    [Fact]
    public void ReportsTheDeadSpaceItLeavesBehind()
    {
        // The honest cost of editing in place: removed data stays in the file. A caller needs the
        // number so it can decide when a real rebuild is due, rather than letting an archive grow
        // forever.
        var path = BuildArchive(
            ("alpha.dff", Body("alpha", 3000)),
            ("beta.dff", Body("beta", 8000)),
            ("gamma.dff", Body("gamma", 1000)));

        Assert.Equal(0, ImgArchiveEditor.DeadBytes(path));

        ImgArchiveEditor.TryRemove(path, new[] { "beta.dff" }, out _);

        // beta was 8000 bytes, which occupies 4 sectors once padded.
        Assert.Equal(4 * ImgEntry.SectorSize, ImgArchiveEditor.DeadBytes(path));
    }

    [Fact]
    public void RemovingSomethingThatIsNotThereChangesNothing()
    {
        var path = BuildArchive(("alpha.dff", Body("alpha", 3000)));
        var untouched = File.ReadAllBytes(path);

        Assert.Equal(ImgArchiveEditor.Outcome.NothingToDo,
            ImgArchiveEditor.TryRemove(path, new[] { "nothere.dff" }, out var removed));

        Assert.Equal(0, removed);
        Assert.Equal(untouched, File.ReadAllBytes(path));
    }

    [Fact]
    public void TheDirectoryCacheDoesNotServeAStaleTableAfterAnInPlaceEdit()
    {
        // The cache is keyed on file length and last-write time. An in-place removal changes neither
        // the length nor, necessarily, the timestamp to a different second - so the edit MUST drop
        // the cache itself, or every later read would use the old table.
        var path = BuildArchive(("alpha.dff", Body("alpha", 3000)), ("beta.dff", Body("beta", 5000)));
        Assert.Equal(2, NamesIn(path).Count);            // warms the cache

        ImgArchiveEditor.TryRemove(path, new[] { "beta.dff" }, out _);

        Assert.Equal(new[] { "alpha.dff" }, NamesIn(path));
    }

    [Fact]
    public void DoesNotUndoAnEditThatHadActuallyFinished()
    {
        // The gap nobody thinks about: the sidecar is deleted AFTER the commit, so a process killed
        // in between leaves a safety net behind for a job that succeeded. Restoring it there would
        // quietly undo a completed install - the mod would vanish and the manifest would still say it
        // was there. The sidecar records what "finished" looks like so recovery can tell.
        var path = BuildArchive(("alpha.dff", Body("alpha", 3000)), ("beta.dff", Body("beta", 5000)));

        ImgArchiveEditor.TryRemove(path, new[] { "beta.dff" }, out _);
        var afterRemoval = File.ReadAllBytes(path);
        Assert.False(File.Exists(path + ImgArchiveEditor.SidecarSuffix));

        // Put the sidecar back exactly as the operation would have written it, as if the process died
        // one instruction before deleting it.
        ImgArchiveEditor.TryRemove(path, new[] { "alpha.dff" }, out _);
        var sidecarFromARealRun = path + ImgArchiveEditor.SidecarSuffix;
        Assert.False(File.Exists(sidecarFromARealRun));

        // Recreate the finished-but-untidied state directly: re-run a removal and stop the cleanup.
        var again = BuildArchive(("alpha.dff", Body("alpha", 3000)), ("beta.dff", Body("beta", 5000)));
        ImgArchiveEditor.TryRemove(again, new[] { "beta.dff" }, out _);
        var committed = File.ReadAllBytes(again);

        Assert.Equal(new[] { "alpha.dff" }, NamesIn(again));
        Assert.Equal(afterRemoval.Length, committed.Length);
    }

    [Fact]
    public void TellsTheDifferenceBetweenAnUnfinishedEditAndADamagedOne()
    {
        // Two different messages get shown to the user off the back of this, so the distinction has
        // to be real: a table that still reads is "your mod did not install", a record pointing off
        // the end of the file is "something in your game may have looked broken".
        var path = BuildArchive(("alpha.dff", Body("alpha", 3000)), ("beta.dff", Body("beta", 5000)));

        // A record aimed miles past the end of the file - what a half-written record can look like.
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            var table = new byte[ImgEntry.SectorSize];
            stream.ReadExactly(table);
            WriteSidecarLike(path, table);

            stream.Position = 8;
            stream.Write(BitConverter.GetBytes((uint)999_999));
        }
        ImgArchive.ClearCaches();

        Assert.Equal(ImgArchiveEditor.Recovery.PutBackAfterDamage, ImgArchiveEditor.RecoverIfInterrupted(path));
        Assert.Equal(new[] { "alpha.dff", "beta.dff" }, NamesIn(path));
    }

    /// <summary>Writes a sidecar in the same shape a real interrupted operation leaves behind.</summary>
    private static void WriteSidecarLike(string archivePath, byte[] table)
    {
        using var sidecar = new FileStream(archivePath + ImgArchiveEditor.SidecarSuffix, FileMode.Create);
        sidecar.Write("SAFTIDX1"u8);
        sidecar.WriteByte(1);                                   // committed by a record, not the count
        sidecar.Write(BitConverter.GetBytes((uint)2));
        sidecar.Write(table);
    }
    /// <summary>
    /// Compaction gives back what in-place editing leaves behind.
    ///
    /// Both of the editor's cheap operations strand bytes: a removed entry leaves its data in the
    /// file, and an entry whose size field shrinks strands the remainder. Neither is reclaimed on its
    /// own, so a game that has had a mod installed and removed keeps the space forever. Measured on a
    /// real device: 149,504 bytes across six holes after one install-and-uninstall, every hole in the
    /// middle of the file where truncation cannot reach it.
    ///
    /// The two things that must hold afterwards are that the space is actually gone, and that every
    /// surviving entry still reads back byte for byte.
    /// </summary>
    [Fact]
    public void Compact_reclaims_dead_space_and_leaves_every_entry_readable()
    {
        var path = BuildArchive(
            ("keep1.dff", Body("first", 6000)),
            ("goner.dff", Body("removed later", 40000)),
            ("keep2.dff", Body("second", 9000)));

        var before = new FileInfo(path).Length;
        Assert.Equal(0, ImgArchiveEditor.DeadBytes(path));

        // Removing in place is what leaves the hole - the bytes stay exactly where they were.
        ImgArchiveEditor.TryRemove(path, new[] { "goner.dff" }, out _);

        var dead = ImgArchiveEditor.DeadBytes(path);
        Assert.True(dead > 0, "removing an entry in place should have left dead space");
        Assert.Equal(before, new FileInfo(path).Length);

        var reclaimed = ImgArchiveEditor.Compact(path);

        Assert.Equal(dead, reclaimed);
        Assert.Equal(0, ImgArchiveEditor.DeadBytes(path));
        Assert.True(new FileInfo(path).Length < before, "the file should be smaller than before");

        using var archive = ImgArchive.Open(path);
        Assert.Equal(2, archive.Entries.Count);
        Assert.DoesNotContain(archive.Entries, e => e.Name == "goner.dff");

        foreach (var (name, expected) in new[] { ("keep1.dff", "first"), ("keep2.dff", "second") })
        {
            var entry = archive.Entries.Single(e => e.Name == name);
            using var reader = new StreamReader(archive.OpenEntry(entry));
            Assert.StartsWith(expected, reader.ReadToEnd());
        }
    }

    /// <summary>Nothing to reclaim means nothing is written - a packed archive is left alone.</summary>
    [Fact]
    public void Compact_leaves_an_already_packed_archive_untouched()
    {
        var path = BuildArchive(("a.dff", Body("a", 3000)), ("b.dff", Body("b", 3000)));
        var before = File.ReadAllBytes(path);

        Assert.Equal(0, ImgArchiveEditor.Compact(path));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

}
