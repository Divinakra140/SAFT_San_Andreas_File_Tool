using SAFT.Core;

namespace SAFT.Core.Tests;

public class AddingAssetsGuideTests
{
    /// <summary>A game with a couple of defined ids, enough for the guide to compute free slots.</summary>
    private static string BuildGame()
    {
        var root = TestScratch.NewDir();
        Directory.CreateDirectory(Path.Combine(root, "data", "maps"));
        File.WriteAllText(Path.Combine(root, "data", "maps", "existing.ide"), """
            objs
            700, someobject, sometxd, 150, 0
            701, another, sometxd, 150, 0
            end
            """);
        return root;
    }

    [Fact]
    public void The_guide_is_plain_ascii()
    {
        // A .txt carries no encoding declaration, so a reader is free to guess. Notepad under
        // Winlator guesses ANSI, which turned every em dash into "aI~" and made whole sentences
        // look like corruption. Staying inside ASCII means it reads correctly everywhere.
        var text = AddingAssetsGuide.Build(BuildGame(), slotsAvailable: 100);

        var offenders = text
            .Where(c => c > 127)
            .Distinct()
            .Select(c => $"U+{(int)c:X4} '{c}'")
            .ToList();

        Assert.True(offenders.Count == 0,
            "the guide must be pure ASCII, found: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_guide_covers_the_three_things_a_mod_must_supply()
    {
        var text = AddingAssetsGuide.Build(BuildGame(), slotsAvailable: 100);

        Assert.Contains(".ide", text);
        Assert.Contains(".ipl", text);
        Assert.Contains(".col", text);

        // The collision rule is the one that crashes games when missed, so it must be stated as
        // required rather than as a nicety.
        Assert.Contains("REQUIRED", text);
        Assert.Contains("CRASHES", text);
    }

    [Fact]
    public void The_guide_lists_this_games_own_free_slots()
    {
        var text = AddingAssetsGuide.Build(BuildGame(), slotsAvailable: 100);

        // Compared whole-line: a bare substring check for "700  [X]" also matches "1700  [X]".
        var listed = new HashSet<string>(
            text.Split('\n').Select(l => l.Trim()).Where(l => l.EndsWith("[X]")));

        // 700 and 701 are taken by the stub game, so they must not be offered; 702 is free.
        Assert.Contains("702  [X]", listed);
        Assert.DoesNotContain("700  [X]", listed);
        Assert.DoesNotContain("701  [X]", listed);
    }
}
