namespace SAFT.Core;

/// <summary>
/// One definition line from an .ide file — "this object ID IS this model wearing this texture".
/// Note there are no coordinates here; placement lives in .ipl. <see cref="RawLine"/> keeps the
/// original text verbatim so a line SAFT added can later be found and removed by content rather
/// than by line number, which shifts as other mods come and go.
/// </summary>
public sealed record IdeDefinition(
    int ObjectId,
    string ModelName,
    string TextureName,
    string Section,
    string RawLine,
    int LineNumber,
    /// <summary>
    /// The .ide this line came from. Which file defines a model is what separates the game's own
    /// objects from SAFT's: an object defined in saft_additions.ide is SAFT's to update or remove,
    /// and one defined anywhere else belongs to the user - whether it shipped with the game or they
    /// added it by hand last week - and SAFT does not write to it. Defaulted so lines built in tests
    /// and in memory need not carry a path.
    /// </summary>
    string SourcePath = "");

/// <summary>
/// Reads San Andreas .ide (item definition) files — the tables that tell the game what each object
/// ID actually is. A stock v3 install has 59 of them under data/, defining 14,832 IDs between them.
///
/// Every section shares ONE model-ID space: peds, vehicles, weapons and map objects all draw from
/// the same table, so anything allocating a new ID has to account for all of them, not just "objs".
/// </summary>
public static class IdeFile
{
    /// <summary>Section keywords that introduce ID-bearing definition lines. Each is closed by "end".</summary>
    private static readonly HashSet<string> DefinitionSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "objs",   // ordinary map objects
        "tobj",   // timed objects (extra time-on/time-off fields, same leading columns)
        "anim",   // animated objects
        "cars",
        "peds",
        "weap",
        "hier",   // cutscene hierarchy objects
    };

    /// <summary>
    /// Sections that exist in .ide files but whose lines are NOT ID definitions, so they must never
    /// be mistaken for one. "txdp" is texture parenting (two names, no ID) and "2dfx" lines are keyed
    /// to an object ID that is already defined elsewhere — counting either would corrupt the used-ID
    /// set the allocator depends on.
    /// </summary>
    private static readonly HashSet<string> NonDefinitionSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "txdp", "2dfx",
    };

    public static IReadOnlyList<IdeDefinition> Parse(string path) =>
        ParseLines(File.ReadLines(path), path);

    public static IReadOnlyList<IdeDefinition> ParseLines(IEnumerable<string> lines, string sourcePath = "")
    {
        var results = new List<IdeDefinition>();
        string? section = null;
        var lineNumber = 0;

        foreach (var raw in lines)
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (section is null)
            {
                // Outside a section, the only meaningful line is a section keyword.
                if (DefinitionSections.Contains(line) || NonDefinitionSections.Contains(line))
                    section = line;
                continue;
            }

            if (line.Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                section = null;
                continue;
            }

            if (NonDefinitionSections.Contains(section)) continue;

            var fields = line.Split(',');
            if (fields.Length < 3) continue;
            if (!int.TryParse(fields[0].Trim(), out var objectId)) continue;

            results.Add(new IdeDefinition(
                objectId,
                fields[1].Trim(),
                fields[2].Trim(),
                section,
                raw,
                lineNumber,
                sourcePath));
        }

        return results;
    }

    /// <summary>Every .ide file under a game folder or extracted install, in a stable order.</summary>
    public static IReadOnlyList<string> FindAll(string root, GameFiles? files = null) =>
        GameFiles.For(root, files).WithExtension(".ide")
            .Where(p => !FileFilters.IsIgnoredFile(Path.GetFileName(p)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
