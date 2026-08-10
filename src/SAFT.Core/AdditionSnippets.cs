namespace SAFT.Core;

/// <summary>A mod's definition and placements, rewritten to use object IDs that are free on THIS install.</summary>
public sealed record RewrittenAddition(
    IReadOnlyList<string> IdeLines,
    IReadOnlyList<string> IplLines,
    IReadOnlyDictionary<int, int> IdMapping,
    IReadOnlyList<string> Problems);

/// <summary>
/// Rewrites a mod's .ide/.ipl snippets onto free object IDs.
///
/// A mod author picks IDs when they build the mod, with no way of knowing what's already taken on
/// anyone else's install — two mods routinely claim the same "obviously free" slot. SAFT reassigns
/// them, and the crucial part is doing it CONSISTENTLY: a definition rewritten to 12046 is useless
/// unless every placement referring to the old ID moves with it.
/// </summary>
public static class AdditionSnippets
{
    /// <summary>
    /// <paramref name="allocatedIds"/> must hold one free ID per definition, in the same order as
    /// <paramref name="definitions"/> — see <see cref="ObjectIdAllocator.Allocate"/>.
    /// </summary>
    public static RewrittenAddition Rewrite(
        IReadOnlyList<IdeDefinition> definitions,
        IReadOnlyList<IplInstance> placements,
        IReadOnlyList<int> allocatedIds)
    {
        if (allocatedIds.Count < definitions.Count)
            throw new ArgumentException("Fewer allocated IDs than definitions.", nameof(allocatedIds));

        var mapping = new Dictionary<int, int>();
        var ideLines = new List<string>();
        var problems = new List<string>();

        for (var i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i];
            var newId = allocatedIds[i];
            mapping[definition.ObjectId] = newId;

            ideLines.Add(FormatDefinition(definition, newId));
        }

        var iplLines = new List<string>();
        foreach (var placement in placements)
        {
            if (!mapping.TryGetValue(placement.ObjectId, out var newId))
            {
                // The mod places something its own .ide never defines. Installing it would put a
                // reference to an undefined object into the game, so it's reported and skipped
                // rather than written out and left to misbehave in-game.
                problems.Add(
                    $"'{placement.ModelName}' is placed (object {placement.ObjectId}) but no .ide file in " +
                    "this mod defines it — that placement was skipped.");
                continue;
            }

            iplLines.Add(IplFile.FormatInstance(newId, placement.ModelName, placement.X, placement.Y, placement.Z, placement.Interior));
        }

        return new RewrittenAddition(ideLines, iplLines, mapping, problems);
    }

    /// <summary>
    /// Rebuilds a definition line with a new ID, preserving every other field exactly as the mod
    /// author wrote it — draw distance and flags are the author's tuning and SAFT has no business
    /// changing them.
    /// </summary>
    private static string FormatDefinition(IdeDefinition definition, int newId)
    {
        var fields = definition.RawLine.Split(',');
        if (fields.Length < 3) return $"{newId}, {definition.ModelName}, {definition.TextureName}, 300, 0";

        fields[0] = newId.ToString();
        for (var i = 1; i < fields.Length; i++) fields[i] = fields[i].Trim();
        return string.Join(", ", fields.Select(f => f.Trim()));
    }
}
