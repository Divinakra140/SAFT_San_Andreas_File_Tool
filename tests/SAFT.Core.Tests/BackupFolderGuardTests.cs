using SAFT.Core;

namespace SAFT.Core.Tests;

/// <summary>
/// Guards against the backup folder being pointed somewhere that corrupts the very thing it is
/// meant to protect. Both refusals below come from a real incident, not a hypothetical.
/// </summary>
public class BackupFolderGuardTests
{
    private const string Game = "/games/Grand Theft Auto San Andreas";
    private const string Mod = "/packs/SAFT-testpack-C-2.4x";
    private const string Elsewhere = "/packs/Backups";

    [Fact]
    public void A_backup_folder_outside_both_is_fine() =>
        Assert.Null(FolderAccess.WhyBackupFolderIsUnusable(Elsewhere, Game, Mod));

    /// <summary>
    /// What actually happened: the backup folder was set to the mod folder, so the originals landed
    /// in among the mod's files. SAFT scans a mod folder recursively, so the next install matched
    /// every file twice - six replacements reported for a three-file mod.
    /// </summary>
    [Fact]
    public void A_backup_folder_inside_the_mod_folder_is_refused()
    {
        var why = FolderAccess.WhyBackupFolderIsUnusable($"{Mod}/models/gta3.img/txd", Game, Mod);

        Assert.NotNull(why);
        Assert.Contains("inside your mod folder", why);
    }

    [Fact]
    public void The_mod_folder_itself_is_refused_as_a_backup_folder() =>
        Assert.Contains("inside your mod folder", FolderAccess.WhyBackupFolderIsUnusable(Mod, Game, Mod)!);

    [Fact]
    public void A_backup_folder_inside_the_game_folder_is_refused()
    {
        var why = FolderAccess.WhyBackupFolderIsUnusable($"{Game}/models/backups", Game, Mod);

        Assert.NotNull(why);
        Assert.Contains("inside your game folder", why);
    }

    [Fact]
    public void The_game_folder_itself_is_refused_as_a_backup_folder() =>
        Assert.Contains("inside your game folder", FolderAccess.WhyBackupFolderIsUnusable(Game, Game, Mod)!);

    /// <summary>
    /// A sibling whose name merely begins with the same text is not inside anything. Comparing raw
    /// strings without a separator would wrongly refuse this.
    /// </summary>
    [Fact]
    public void A_sibling_with_a_similar_name_is_not_treated_as_inside()
    {
        Assert.Null(FolderAccess.WhyBackupFolderIsUnusable($"{Mod}-backups", Game, Mod));
        Assert.Null(FolderAccess.WhyBackupFolderIsUnusable($"{Game} Backups", Game, Mod));
    }

    [Theory]
    [InlineData("/a/b", "/a", true)]
    [InlineData("/a", "/a", true)]           // the same folder counts as inside
    [InlineData("/a/", "/a", true)]          // a trailing separator changes nothing
    [InlineData("/a/b/../c", "/a", true)]    // ".." is resolved before comparing
    [InlineData("/a/b/../../d", "/a", false)]
    [InlineData("/ab", "/a", false)]         // sibling, not child
    [InlineData("/a", "/a/b", false)]        // parent is not inside its own child
    public void IsInsideOrSame_resolves_paths_before_comparing(string candidate, string container, bool expected) =>
        Assert.Equal(expected, FolderAccess.IsInsideOrSame(candidate, container));

    [Fact]
    public void Blank_paths_are_not_inside_anything()
    {
        Assert.False(FolderAccess.IsInsideOrSame("", Game));
        Assert.False(FolderAccess.IsInsideOrSame(Game, "   "));
    }
}
