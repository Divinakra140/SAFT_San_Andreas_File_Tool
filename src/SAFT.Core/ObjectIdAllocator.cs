namespace SAFT.Core;

/// <summary>How much room a game has left for new objects, and where that room is.</summary>
public sealed record ObjectIdAvailability(int UsedCount, int HighestUsedId, int FreeInGaps, int FreeAboveHighest)
{
    /// <summary>
    /// Every ID a new object can use: the unused gaps between the game's own IDs, plus the headroom
    /// above the highest one.
    ///
    /// Both are genuinely usable. An earlier version counted only the headroom, on the theory that a
    /// gap might belong to a range the engine reserves — that theory came from misreading a crash
    /// which turned out to be missing collision, and it cost roughly 3,700 slots for no reason. A
    /// brand-new object at ID 662, in a gap just past the vehicle range, loads and behaves correctly
    /// once it has a collision record. See <see cref="Allocate"/>.
    /// </summary>
    public int TotalFree => FreeInGaps + FreeAboveHighest;
}

/// <summary>
/// Finds free object IDs — "slots" — for newly added assets.
///
/// A slot is one .ide definition line, NOT a file: that line says "ID 12000 is model X wearing
/// texture Y". The .dff/.txd/.col files cost nothing, since they're referenced by name, and placing
/// the same object a hundred times in .ipl still only uses the one ID.
///
/// Allocation needs no memory between installs: once a mod's definition is written into an .ide,
/// the game's own files say that ID is taken, so the next scan sees it. The game folder is the
/// source of truth; the additions manifest exists only so an install can be undone later.
/// </summary>
public static class ObjectIdAllocator
{
    /// <summary>
    /// IDs below this belong to peds (0-299), weapons (~321-372) and vehicles (400-611). A map
    /// object placed in that space breaks the thing that already owns it, so allocation starts above.
    /// </summary>
    public const int LowestMapObjectId = 616;

    /// <summary>
    /// The engine's model-table limit. This is the commonly cited figure for San Andreas rather than
    /// something measured from the game's files — worth confirming against a limit adjuster's
    /// documentation before leaning on it. Everything below the highest ID a game actually uses is
    /// unaffected by it.
    /// </summary>
    public const int DefaultEngineModelLimit = 20000;

    /// <summary>Every object ID defined anywhere under a game folder or extracted install.</summary>
    public static SortedSet<int> ScanUsedIds(string root)
    {
        var used = new SortedSet<int>();
        foreach (var path in IdeFile.FindAll(root))
        {
            foreach (var definition in IdeFile.Parse(path))
                used.Add(definition.ObjectId);
        }
        return used;
    }

    public static ObjectIdAvailability Describe(IReadOnlySet<int> used, int engineLimit = DefaultEngineModelLimit)
    {
        if (used.Count == 0)
            return new ObjectIdAvailability(0, 0, 0, Math.Max(0, engineLimit - LowestMapObjectId));

        var highest = used.Max();
        var gaps = 0;
        for (var id = LowestMapObjectId; id <= highest; id++)
            if (!used.Contains(id)) gaps++;

        var above = 0;
        for (var id = highest + 1; id < engineLimit; id++)
            if (!used.Contains(id)) above++;

        return new ObjectIdAvailability(used.Count, highest, gaps, above);
    }

    /// <summary>
    /// Reserves <paramref name="count"/> IDs, filling the lowest free ones first.
    ///
    /// The gaps between the game's own IDs are usable, and this was verified on a real install
    /// rather than reasoned about: a new object defined at ID 662 — an unused gap immediately past
    /// the vehicle range, and the exact ID an earlier build was blamed for — loads, renders and
    /// collides correctly.
    ///
    /// That earlier build allocated above the highest used ID instead, because a game crash was
    /// misdiagnosed as an ID-range problem. The crash was actually a missing collision record, which
    /// crashes at world load no matter which ID the object uses. Testing IDs 662, 12241 and 18631 in
    /// turn produced identical crashes, and all three work once collision is present. Allocating low
    /// keeps roughly 5,100 slots available on a stock install instead of 1,369.
    ///
    /// Below <see cref="LowestMapObjectId"/> is still off limits: those IDs aren't free, they're
    /// occupied by peds, weapons and vehicles.
    /// </summary>
    public static IReadOnlyList<int> Allocate(IReadOnlySet<int> used, int count, int engineLimit = DefaultEngineModelLimit)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return Array.Empty<int>();

        var start = LowestMapObjectId;

        var allocated = new List<int>(count);
        for (var id = start; id < engineLimit && allocated.Count < count; id++)
        {
            if (!used.Contains(id)) allocated.Add(id);
        }

        if (allocated.Count < count)
        {
            throw new InvalidOperationException(
                $"Not enough free object slots: {count} needed, {allocated.Count} available between " +
                $"{start} and the engine limit of {engineLimit}.");
        }

        return allocated;
    }
}
