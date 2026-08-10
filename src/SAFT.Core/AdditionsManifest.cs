using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SAFT.Core;

/// <summary>One file SAFT appended into an archive as part of an addition.</summary>
public sealed class AddedArchiveEntry
{
    public required string ArchiveRelativePath { get; init; }
    public required string EntryName { get; init; }

    /// <summary>
    /// SHA-256 of the bytes SAFT wrote. Uninstall compares this before removing anything: matching
    /// means "still exactly what we added, safe to take back out", differing means the user has
    /// replaced it since and it should be reported rather than silently deleted.
    /// </summary>
    public required string Sha256 { get; init; }
}

/// <summary>One line SAFT wrote into a game data file (.ide or .ipl).</summary>
public sealed class AddedDataLine
{
    /// <summary>Path of the edited file, relative to the game folder.</summary>
    public required string FileRelativePath { get; init; }

    /// <summary>
    /// The line exactly as written. Removal matches on this text rather than on a line number,
    /// because line numbers shift as other mods are installed and removed around it.
    /// </summary>
    public required string Line { get; init; }
}

/// <summary>One collision record appended into a .col bundle.</summary>
public sealed class AddedCollision
{
    public required string BundleName { get; init; }
    public required string ModelName { get; init; }
    public required string Sha256 { get; init; }
}

/// <summary>Everything one mod added, recorded so it can be taken back out again.</summary>
public sealed class AddedMod
{
    public required string Name { get; init; }
    public required DateTimeOffset AddedAtUtc { get; init; }

    /// <summary>Object IDs allocated to this mod, which return to the pool when it's removed.</summary>
    public List<int> ObjectIds { get; init; } = new();

    public List<AddedArchiveEntry> ArchiveEntries { get; init; } = new();
    public List<AddedDataLine> DataLines { get; init; } = new();
    public List<AddedCollision> Collisions { get; init; } = new();
}

/// <summary>
/// The record of assets SAFT has ADDED to a game, as opposed to replaced.
///
/// This lives in the user's backup folder, written at install time next to the backed-up originals
/// of any replaced files. That placement is the hinge of the whole design: an added asset has no
/// vanilla counterpart, so nothing about it would otherwise land in the backup folder, and the
/// uninstall tab would have no way to know the addition ever happened.
///
/// Deliberately separate from <see cref="SaftManifest"/>, which describes an extraction. This one
/// describes modifications made to a live game.
/// </summary>
public sealed class AdditionsManifest
{
    public const string FileName = "saft-additions.json";
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public required string GameRootPath { get; init; }
    public List<AddedMod> Mods { get; init; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Save(string backupFolder)
    {
        Directory.CreateDirectory(backupFolder);
        File.WriteAllText(Path.Combine(backupFolder, FileName), JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>Loads the manifest from a backup folder, or null if that folder holds no record of additions.</summary>
    public static AdditionsManifest? Load(string backupFolder)
    {
        var path = Path.Combine(backupFolder, FileName);
        if (!File.Exists(path)) return null;

        var manifest = JsonSerializer.Deserialize<AdditionsManifest>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"'{path}' could not be read as a SAFT additions record.");

        if (manifest.FormatVersion > CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"'{path}' was written by a newer version of SAFT (format {manifest.FormatVersion}); " +
                $"this build understands up to format {CurrentFormatVersion}.");
        }

        return manifest;
    }

    /// <summary>
    /// Hashes an asset's meaningful content, ignoring trailing zero bytes.
    ///
    /// Trimming is what makes install and uninstall agree. A file goes into an .img padded out to a
    /// 2048-byte sector boundary, so the bytes read back out are never the bytes that went in.
    /// Hashing them raw meant uninstall rejected every asset SAFT had just added, believing the user
    /// had replaced it — leaving added files stranded in the archive with no way to remove them.
    /// Both sides now hash content-without-padding, so they match.
    /// </summary>
    public static string ComputeSha256(byte[] contents)
    {
        var end = contents.Length;
        while (end > 0 && contents[end - 1] == 0) end--;
        return Convert.ToHexString(SHA256.HashData(contents.AsSpan(0, end))).ToLowerInvariant();
    }

    public static string ComputeSha256(string filePath) => ComputeSha256(File.ReadAllBytes(filePath));
}
