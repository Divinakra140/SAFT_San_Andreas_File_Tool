using SAFT.Core;

namespace SAFT.Core.Tests;

public class AdditionsManifestTests
{
    private static AdditionsManifest SampleManifest(string gameRoot) => new()
    {
        GameRootPath = gameRoot,
        Mods =
        {
            new AddedMod
            {
                Name = "My Castle",
                AddedAtUtc = DateTimeOffset.UtcNow,
                ObjectIds = { 12000 },
                ArchiveEntries =
                {
                    new AddedArchiveEntry
                    {
                        ArchiveRelativePath = Path.Combine("models", "gta3.img"),
                        EntryName = "saftcastle.dff",
                        Sha256 = AdditionsManifest.ComputeSha256(new byte[] { 1, 2, 3 }),
                    },
                },
                DataLines =
                {
                    new AddedDataLine
                    {
                        FileRelativePath = Path.Combine("data", "maps", "LA", "LAs.ide"),
                        Line = "12000, saftcastle, saftcastletxd, 300, 0",
                    },
                },
            },
        },
    };

    [Fact]
    public void Round_trips_through_the_backup_folder()
    {
        var backupFolder = TestScratch.NewDir();
        SampleManifest("C:\\Games\\GTA").Save(backupFolder);

        var loaded = AdditionsManifest.Load(backupFolder);

        Assert.NotNull(loaded);
        var mod = Assert.Single(loaded!.Mods);
        Assert.Equal("My Castle", mod.Name);
        Assert.Equal(12000, Assert.Single(mod.ObjectIds));
        Assert.Equal("saftcastle.dff", Assert.Single(mod.ArchiveEntries).EntryName);
        Assert.Equal("12000, saftcastle, saftcastletxd, 300, 0", Assert.Single(mod.DataLines).Line);
    }

    [Fact]
    public void Load_returns_null_when_a_backup_folder_holds_no_record_of_additions()
    {
        // This is the ordinary case for a 1.6-era backup folder: no additions were ever made, so
        // the uninstall tab must fall through to plain replacement-restore rather than erroring.
        Assert.Null(AdditionsManifest.Load(TestScratch.NewDir()));
    }

    [Fact]
    public void Refuses_a_manifest_written_by_a_newer_version_of_SAFT()
    {
        var backupFolder = TestScratch.NewDir();
        File.WriteAllText(
            Path.Combine(backupFolder, AdditionsManifest.FileName),
            """{"FormatVersion": 99, "GameRootPath": "C:\\Games\\GTA", "Mods": []}""");

        // Guessing at a format from the future risks removing the wrong lines from a user's game
        // files, so this fails loudly instead.
        var ex = Assert.Throws<InvalidDataException>(() => AdditionsManifest.Load(backupFolder));
        Assert.Contains("newer version of SAFT", ex.Message);
    }

    [Fact]
    public void Hashes_let_uninstall_tell_an_untouched_file_from_one_the_user_replaced_since()
    {
        var dir = TestScratch.NewDir();
        var path = Path.Combine(dir, "saftcastle.dff");
        File.WriteAllBytes(path, new byte[] { 10, 20, 30 });
        var atInstall = AdditionsManifest.ComputeSha256(path);

        Assert.Equal(atInstall, AdditionsManifest.ComputeSha256(path));

        File.WriteAllBytes(path, new byte[] { 10, 20, 99 });   // user swapped it afterwards

        // Uninstall compares this before deleting: a mismatch means report it, don't silently
        // destroy whatever the user put there.
        Assert.NotEqual(atInstall, AdditionsManifest.ComputeSha256(path));
    }
}
