using Xunit;

namespace SAFT.Core.Tests;

/// <summary>
/// GameFiles exists so the game folder is walked once per install instead of eight times. The risk
/// of sharing one listing is that a consumer silently gets the wrong folder's files, so that is what
/// these pin down.
/// </summary>
public class GameFilesTests
{
    private static string FolderWith(params string[] relativePaths)
    {
        var root = TestScratch.NewDir();
        foreach (var relative in relativePaths)
        {
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, new byte[8]);
        }
        return root;
    }

    [Fact]
    public void SkipsFilesystemJunk()
    {
        // A game folder copied to an SD card from a Mac carries a "._name" beside every file. They
        // end with the real file's extension, so anything filtering by extension counts them as real
        // assets unless they are dropped here.
        var root = FolderWith("real.dff", "._real.dff", ".DS_Store", "models/._nested.txd", "Thumbs.db");
        var files = GameFiles.Walk(root);

        var only = Assert.Single(files.Paths);
        Assert.EndsWith("real.dff", only, StringComparison.Ordinal);
        Assert.Single(files.WithExtension(".dff"));
    }

    [Fact]
    public void ListsEveryFileAtEveryDepth()
    {
        var root = FolderWith("a.txt", "models/b.dff", "data/maps/c.ipl");
        var files = GameFiles.Walk(root);

        Assert.Equal(3, files.Paths.Count);
        Assert.Contains(files.Paths, p => p.EndsWith("c.ipl", StringComparison.Ordinal));
    }

    [Fact]
    public void FiltersByExtensionCaseInsensitively()
    {
        var root = FolderWith("one.IPL", "two.ipl", "three.ide", "four.img");
        var files = GameFiles.Walk(root);

        Assert.Equal(2, files.WithExtension(".ipl").Count());
        Assert.Single(files.WithExtension(".img"));
    }

    [Fact]
    public void ReusesASuppliedListingForTheSameFolder()
    {
        var root = FolderWith("a.dff");
        var original = GameFiles.Walk(root);

        // Same folder, so the existing listing is handed straight back rather than re-walked.
        Assert.Same(original, GameFiles.For(root, original));
    }

    [Fact]
    public void WalksAgainWhenTheListingIsForADifferentFolder()
    {
        // The dangerous case: a listing from one game folder must never be served for another, or a
        // consumer would silently see the wrong game's files.
        var first = FolderWith("a.dff");
        var second = FolderWith("b.dff", "c.dff");

        var listingForFirst = GameFiles.Walk(first);
        var result = GameFiles.For(second, listingForFirst);

        Assert.NotSame(listingForFirst, result);
        Assert.Equal(2, result.Paths.Count);
        Assert.All(result.Paths, p => Assert.StartsWith(second, p, StringComparison.Ordinal));
    }

    [Fact]
    public void WalksWhenNoListingIsSupplied()
    {
        var root = FolderWith("a.dff", "b.txd");
        Assert.Equal(2, GameFiles.For(root, null).Paths.Count);
    }

    [Fact]
    public void AnUnreadableFolderYieldsAnEmptyListingRatherThanThrowing()
    {
        // Every consumer already tolerated a folder it could not read; this must not turn that into
        // a crash on somebody's machine.
        var missing = Path.Combine(TestScratch.NewDir(), "does-not-exist");
        Assert.Empty(GameFiles.Walk(missing).Paths);
    }

    [Fact]
    public void ReportsWhatItWalkedWithoutNarratingEveryFolder()
    {
        // The walk is where SAFT has twice stopped dead, so it counts folders and times them. What it
        // must NOT do in a release build is write a line per folder every time a mod is checked: the
        // log is a file users are asked to send, and it should hold findings, not narration.
        var root = FolderWith("a.txt", "models/b.dff", "data/maps/c.ipl");
        var steps = new List<string>();

        GameFiles.Walk(root, steps.Add);

        // Asserted as a shape, not a line count. An earlier version of this test demanded exactly two
        // lines and went red at random: the scratch space these tests run on is an SD card, and a
        // single folder listing there really does sometimes cross the slow-folder threshold, which
        // adds a line. That is the reporting working, not a failure — and it is a fair warning about
        // how easily that threshold trips on the hardware SAFT is used on.
        Assert.DoesNotContain(steps, s => s.StartsWith("files: reading ", StringComparison.Ordinal));
        Assert.Contains(steps, s => s.Contains("3 file(s) across 4 folder(s)", StringComparison.Ordinal));
    }

    // Not pinned by a test: that one unreadable subfolder no longer throws away the whole listing.
    // Making a folder unreadable needs POSIX modes, and the scratch space these tests run in is on an
    // exFAT card that does not carry them - the mode is set and silently ignored. The behaviour is in
    // GameFiles.Walk's per-folder catch, which the walk cannot reach without a real permission denial.

    [Fact]
    public void ConsumersHandedTheListingSeeTheSameFilesTheyWouldHaveWalked()
    {
        var root = FolderWith("data/maps/one.ipl", "data/two.ide", "models/three.dff");
        var listing = GameFiles.Walk(root);

        Assert.Equal(IplFile.FindAll(root), IplFile.FindAll(root, listing));
        Assert.Equal(IdeFile.FindAll(root), IdeFile.FindAll(root, listing));
    }
}
