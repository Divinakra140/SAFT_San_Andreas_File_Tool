using System.Text.Json;
using System.Text.Json.Serialization;

namespace SAFT.Core;

public sealed class ManifestArchive
{
    /// <summary>Archive path relative to the original game root, e.g. "models/gta3.img".</summary>
    public required string RelativePath { get; init; }

    /// <summary>Original entry names, in their original on-disk order.</summary>
    public required List<string> OriginalEntryOrder { get; init; }
}

public sealed class SaftManifest
{
    public const string FileName = "manifest.saft.json";
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public required string GameRootPath { get; init; }
    public required DateTimeOffset ExtractedAtUtc { get; init; }
    public required List<ManifestArchive> Archives { get; init; }

    /// <summary>
    /// SFX package names (e.g. "GENRL") that were unpacked into individual Bank_NNN/sound_NNN.wav
    /// files during extraction — audio extraction is opt-in, so most extractions have none. A
    /// package NOT in this list still exists in the extraction folder, just as an untouched loose
    /// copy of the original package file, not unpacked into per-sound files. Not required, so
    /// manifests from before audio extraction existed still load fine (as "nothing was unpacked").
    /// </summary>
    public List<string> UnpackedAudioPackages { get; init; } = new();

    /// <summary>Same idea as <see cref="UnpackedAudioPackages"/>, for streamed-audio stations (e.g. "AA") unpacked into Track_NNN.ogg files.</summary>
    public List<string> UnpackedStreamStations { get; init; } = new();

    public void Save(string destinationDirectory)
    {
        var path = Path.Combine(destinationDirectory, FileName);
        // Source-generated, not reflected over - see SaftJsonContext.
        File.WriteAllText(path, JsonSerializer.Serialize(this, SaftJsonContext.Default.SaftManifest));
    }

    public static SaftManifest Load(string extractionDirectory)
    {
        var path = Path.Combine(extractionDirectory, FileName);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"No {FileName} found in '{extractionDirectory}'. Pick the folder you originally extracted to.", path);

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, SaftJsonContext.Default.SaftManifest)
            ?? throw new InvalidDataException($"'{path}' could not be parsed.");
    }
}
