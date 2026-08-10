namespace SAFT.Core;

/// <summary>
/// One placement line from an .ipl file's "inst" section — an instance of an already-defined object
/// standing somewhere in the world. The object ID here must match an <see cref="IdeDefinition"/>;
/// .ide says what a thing is, .ipl says where it is.
/// </summary>
public sealed record IplInstance(
    int ObjectId,
    string ModelName,
    int Interior,
    double X,
    double Y,
    double Z,
    string RawLine,
    int LineNumber);

/// <summary>
/// Reads San Andreas .ipl (item placement) files. Only the "inst" section is parsed — the others
/// (cull, zone, enex, pick, path, grge, auzo, jump, tcyc, mult) describe zones, triggers and
/// effects rather than placed objects, and adding an asset never needs to touch them.
///
/// Note the game also keeps 164 BINARY .ipl files inside gta3.img for streamed map sections. Those
/// are a different format and aren't handled here; additions go in the plain-text ones under data/.
/// </summary>
public static class IplFile
{
    private const string InstanceSection = "inst";

    public static IReadOnlyList<IplInstance> Parse(string path) =>
        ParseLines(File.ReadAllLines(path));

    public static IReadOnlyList<IplInstance> ParseLines(IEnumerable<string> lines)
    {
        var results = new List<IplInstance>();
        var inInstanceSection = false;
        var lineNumber = 0;

        foreach (var raw in lines)
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (!inInstanceSection)
            {
                if (line.Equals(InstanceSection, StringComparison.OrdinalIgnoreCase)) inInstanceSection = true;
                continue;
            }

            if (line.Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                inInstanceSection = false;
                continue;
            }

            // id, model, interior, x, y, z, rotX, rotY, rotZ, rotW, lod
            var fields = line.Split(',');
            if (fields.Length < 6) continue;
            if (!int.TryParse(fields[0].Trim(), out var objectId)) continue;
            if (!TryParseCoordinate(fields[3], out var x)) continue;
            if (!TryParseCoordinate(fields[4], out var y)) continue;
            if (!TryParseCoordinate(fields[5], out var z)) continue;
            int.TryParse(fields[2].Trim(), out var interior);

            results.Add(new IplInstance(objectId, fields[1].Trim(), interior, x, y, z, raw, lineNumber));
        }

        return results;
    }

    /// <summary>
    /// Coordinates are written in invariant form, including scientific notation for near-zero
    /// rotation components ("-8.742277657e-008"). Parsing must not depend on the machine's locale,
    /// or a comma-decimal system would read these files as garbage.
    /// </summary>
    private static bool TryParseCoordinate(string field, out double value) =>
        double.TryParse(
            field.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out value);

    /// <summary>Every plain-text .ipl file under a game folder or extracted install, in a stable order.</summary>
    public static IReadOnlyList<string> FindAll(string root) =>
        Directory.EnumerateFiles(root, "*.ipl", SearchOption.AllDirectories)
            .Where(p => !FileFilters.IsIgnoredFile(Path.GetFileName(p)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Formats a placement line the way the game's own files do. Invariant culture throughout, for
    /// the same reason parsing uses it.
    /// </summary>
    public static string FormatInstance(int objectId, string modelName, double x, double y, double z, int interior = 0, int lod = -1) =>
        string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{0}, {1}, {2}, {3}, {4}, {5}, 0, 0, 0, 1, {6}",
            objectId, modelName, interior, x, y, z, lod);
}
