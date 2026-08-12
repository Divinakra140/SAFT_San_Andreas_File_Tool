using Xunit;

namespace SAFT.Core.Tests;

/// <summary>
/// The archive directory table is cached so that one analysis pass stops re-parsing the same 940 MB
/// file four times. The only way that change can do harm is by serving a table that no longer
/// matches the file, so these tests are about staleness, not speed.
/// </summary>
[Collection("ImgArchiveCache")]
public class ImgArchiveCacheTests
{
    private static string WriteArchive(string path, params (string Name, byte[] Content)[] files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ImgArchive.Write(
            path,
            files.Select(f => (f.Name, (Func<Stream>)(() => new MemoryStream(f.Content)))).ToList());
        return path;
    }

    [Fact]
    public void ReadsTheSameEntriesWhenNothingChanged()
    {
        var path = Path.Combine(TestScratch.NewDir(), "test.img");
        WriteArchive(path, ("alpha.dff", new byte[100]), ("beta.txd", new byte[200]));

        List<string> First, Second;
        using (var a = ImgArchive.Open(path)) First = a.Entries.Select(e => e.Name).ToList();
        using (var b = ImgArchive.Open(path)) Second = b.Entries.Select(e => e.Name).ToList();

        Assert.Equal(First, Second);
        Assert.Equal(new[] { "alpha.dff", "beta.txd" }, First);
    }

    [Fact]
    public void ReadingTheTableAloneAgreesWithOpeningTheArchive()
    {
        // Weighing the game's assets, indexing what a mod can replace and listing the game's known
        // names all want the table and nothing else. They opened the whole archive for it - and a
        // device died on the log line for exactly that open. The table-only read has to give the same
        // answer, or three separate parts of the checks start disagreeing with each other.
        var path = Path.Combine(TestScratch.NewDir(), "test.img");
        WriteArchive(path, ("alpha.dff", new byte[100]), ("beta.txd", new byte[3000]));

        List<(string Name, ushort SizeSectors)> opened;
        using (var a = ImgArchive.Open(path))
            opened = a.Entries.Select(e => (e.Name, e.SizeSectors)).ToList();

        var tableOnly = ImgArchive.ReadDirectory(path).Select(e => (e.Name, e.SizeSectors)).ToList();

        Assert.Equal(opened, tableOnly);
    }

    [Fact]
    public void TheTableOnlyReadIsAlsoInvalidatedByARebuild()
    {
        // Same staleness rule as everything else here: it shares the cache, so it must share the
        // invalidation. A table-only read that went stale would weigh the wrong sizes into the
        // streaming verdict, silently.
        var path = Path.Combine(TestScratch.NewDir(), "test.img");
        WriteArchive(path, ("alpha.dff", new byte[100]));
        Assert.Equal(new[] { "alpha.dff" }, ImgArchive.ReadDirectory(path).Select(e => e.Name));

        WriteArchive(path, ("gamma.dff", new byte[100]), ("delta.txd", new byte[100]));

        Assert.Equal(
            new[] { "gamma.dff", "delta.txd" },
            ImgArchive.ReadDirectory(path).Select(e => e.Name));
    }

    [Fact]
    public void DoesNotServeAStaleTableAfterTheArchiveIsRebuilt()
    {
        // The dangerous case: read it, rebuild it with DIFFERENT contents, read it again. A cache
        // keyed only on a coarse timestamp could hand back the old entries here, and every offset in
        // them would point at the wrong bytes.
        var path = Path.Combine(TestScratch.NewDir(), "test.img");

        WriteArchive(path, ("alpha.dff", new byte[100]));
        using (var before = ImgArchive.Open(path))
            Assert.Equal(new[] { "alpha.dff" }, before.Entries.Select(e => e.Name));

        WriteArchive(path, ("gamma.dff", new byte[100]), ("delta.txd", new byte[100]));
        using (var after = ImgArchive.Open(path))
            Assert.Equal(new[] { "gamma.dff", "delta.txd" }, after.Entries.Select(e => e.Name));
    }

    [Fact]
    public void EntryContentStillReadsCorrectlyThroughACachedTable()
    {
        // The table is cached; the bytes are not. Reading an entry through a reused table must still
        // land on the right offset in the real file.
        var path = Path.Combine(TestScratch.NewDir(), "test.img");
        var alpha = new byte[] { 1, 2, 3, 4 };
        var beta = new byte[] { 9, 8, 7, 6 };
        WriteArchive(path, ("alpha.dff", alpha), ("beta.txd", beta));

        using (var warm = ImgArchive.Open(path)) { _ = warm.Entries.Count; } // populate the cache

        using var archive = ImgArchive.Open(path);
        var entry = archive.Entries.Single(e => e.Name == "beta.txd");
        using var stream = archive.OpenEntry(entry);
        var buffer = new byte[4];
        stream.ReadExactly(buffer);

        Assert.Equal(beta, buffer);
    }

    [Fact]
    public void MagicCheckDoesNotSurviveTheFileBecomingSomethingElse()
    {
        var dir = TestScratch.NewDir();
        var path = Path.Combine(dir, "test.img");
        WriteArchive(path, ("alpha.dff", new byte[100]));
        Assert.True(ImgArchive.IsImgArchive(path));

        ImgArchive.ClearCaches();
        File.WriteAllBytes(path, new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 });

        Assert.False(ImgArchive.IsImgArchive(path));
    }

    [Fact]
    public void ClearCachesForcesAFreshRead()
    {
        var path = Path.Combine(TestScratch.NewDir(), "test.img");
        WriteArchive(path, ("alpha.dff", new byte[100]));
        using (var a = ImgArchive.Open(path)) Assert.Single(a.Entries);

        ImgArchive.ClearCaches();

        using var b = ImgArchive.Open(path);
        Assert.Single(b.Entries);
        Assert.Equal("alpha.dff", b.Entries[0].Name);
    }
}
