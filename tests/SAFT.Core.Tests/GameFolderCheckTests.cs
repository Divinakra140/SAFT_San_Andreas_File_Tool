using SAFT.Core;

namespace SAFT.Core.Tests;

public class GameFolderCheckTests
{
    /// <summary>
    /// The case this exists for: one game's backup folder used against another game. The uninstall
    /// finds nothing to remove, and the game the assets are really in is never told.
    /// </summary>
    [Fact]
    public void Notices_a_record_written_for_a_different_game()
    {
        Assert.True(GameFolderCheck.LooksLikeADifferentGame(
            @"E:\San Andreas\GTA-San-Andreas-SteamRIP.com\Grand Theft Auto San Andreas",
            @"D:\Games\GTA SA Vanilla"));
    }

    /// <summary>
    /// The same install reached from Windows and from Android. This must NOT warn: someone modding
    /// one game folder over a cable from both sides would see it every single run, and a warning that
    /// cries wolf is worse than no warning at all — it teaches people to click through the real one.
    /// </summary>
    [Theory]
    [InlineData(
        @"E:\San Andreas\GTA-San-Andreas-SteamRIP.com\Grand Theft Auto San Andreas",
        "/storage/emulated/0/Download/GTA SA/Grand Theft Auto San Andreas")]
    [InlineData(
        "/storage/emulated/0/Download/GTA SA/[Place Game Directory Here]",
        @"E:\San Andreas\[Place Game Directory Here]")]
    public void Stays_quiet_when_the_same_folder_is_reached_by_different_paths(string recorded, string current)
    {
        Assert.False(GameFolderCheck.LooksLikeADifferentGame(recorded, current));
    }

    /// <summary>Case and trailing separators are noise, not a difference.</summary>
    [Theory]
    [InlineData(@"E:\Games\GTA SA\", @"E:\Games\gta sa")]
    [InlineData("/sdcard/GTA SA//", "/sdcard/GTA SA")]
    public void Ignores_case_and_trailing_separators(string recorded, string current)
    {
        Assert.False(GameFolderCheck.LooksLikeADifferentGame(recorded, current));
    }

    /// <summary>
    /// It answers "no" whenever it cannot tell. A first install has no record, and an older record may
    /// carry no path at all — neither is a reason to put a warning in front of someone.
    /// </summary>
    [Theory]
    [InlineData(null, @"E:\Games\GTA SA")]
    [InlineData("", @"E:\Games\GTA SA")]
    [InlineData("   ", @"E:\Games\GTA SA")]
    [InlineData(@"E:\Games\GTA SA", null)]
    [InlineData(@"E:\Games\GTA SA", "")]
    [InlineData("/", "/")]
    public void Says_nothing_when_it_cannot_tell(string? recorded, string? current)
    {
        Assert.False(GameFolderCheck.LooksLikeADifferentGame(recorded, current));
    }

    /// <summary>
    /// A bare folder name with no separators at all is still a name worth comparing.
    /// </summary>
    [Fact]
    public void Compares_bare_folder_names()
    {
        Assert.True(GameFolderCheck.LooksLikeADifferentGame("GTA SA", "GTA VC"));
        Assert.False(GameFolderCheck.LooksLikeADifferentGame("GTA SA", "GTA SA"));
    }

    /// <summary>The message has to name the folder, or it tells the user nothing they can act on.</summary>
    [Fact]
    public void The_warning_names_the_folder_it_was_written_for()
    {
        Assert.Contains(@"D:\Other\GTA SA", GameFolderCheck.Warning(@"D:\Other\GTA SA"));
    }
}
