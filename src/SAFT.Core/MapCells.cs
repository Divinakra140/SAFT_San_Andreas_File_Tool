namespace SAFT.Core;

/// <summary>
/// The game's map reduced to what anything actually asks of it: per 200m cell, how many objects sit
/// there and which distinct models they use.
///
/// This replaces holding the map as a list of placements. Every consumer of that list — the busiest
/// area, the heaviest area, the heaviest area recomputed with a mod applied — immediately collapsed
/// it into exactly these per-cell figures and threw the rest away. San Andreas has 50,982 placements
/// and 869 cells, so the list was three orders of magnitude larger than the only thing anyone wanted
/// from it, and it was built in full, all at once, on every install.
///
/// That size is the point. SAFT is a 32-bit process whose Large Object Heap is never compacted, and
/// the crashes we chased were never "out of memory" — live heap sat between 13 and 27 MB throughout
/// — they were the allocator unable to find a contiguous block among the holes. Placements arrive
/// here one at a time and are folded in as they come, so the big list never exists to leave holes.
///
/// Textures are deliberately not stored: a model's texture dictionary is a property of the model,
/// so the distinct texture set for a cell is recoverable from its model names and the weight table.
/// Storing it as well would have doubled the size of this for a value that is already implied.
/// </summary>
public sealed class MapCells
{
    private sealed class Cell
    {
        public int ObjectCount;
        public readonly HashSet<string> Models = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<(long X, long Y), Cell> _cells = new();

    /// <summary>How many 200m cells hold at least one placement.</summary>
    public int Count => _cells.Count;

    /// <summary>Total placements folded in — the map's real placement count, though none are kept.</summary>
    public int TotalPlacements { get; private set; }

    /// <summary>
    /// Folds one placement in and discards it. <paramref name="weights"/> decides whether the model
    /// contributes to the byte totals; a placement of something with no weight still counts towards
    /// the object count, matching what the list-based version did.
    /// </summary>
    public void Add(IplInstance placement, IReadOnlyDictionary<string, ModelWeight>? weights)
    {
        TotalPlacements++;

        var key = ((long)Math.Floor(placement.X / PlacementDensity.AreaSizeUnits),
                   (long)Math.Floor(placement.Y / PlacementDensity.AreaSizeUnits));

        if (!_cells.TryGetValue(key, out var cell)) _cells[key] = cell = new Cell();
        cell.ObjectCount++;

        if (weights is not null && weights.TryGetValue(placement.ModelName, out var weight))
            cell.Models.Add(weight.ModelName);
    }

    public void AddRange(IEnumerable<IplInstance> placements, IReadOnlyDictionary<string, ModelWeight>? weights)
    {
        foreach (var placement in placements) Add(placement, weights);
    }

    /// <summary>
    /// Heaviest single cell in bytes: distinct models plus the distinct texture dictionaries they
    /// use, each counted once however many placements share them.
    ///
    /// Takes the weights fresh each call rather than caching a total, because the whole reason this
    /// is asked twice is to compare the game as it stands against the game with a mod applied — same
    /// cells, different weights.
    /// </summary>
    public long HeaviestBytes(IReadOnlyDictionary<string, ModelWeight>? weights)
    {
        if (weights is null) return 0;

        // Built once for this weight table and cached against it, not rebuilt per cell — weights are
        // keyed by model, so without this every texture lookup would be a scan of all 14,823 entries.
        var textureBytes = PlacementDensity.TextureBytesFor(weights);

        long heaviest = 0;
        var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cell in _cells.Values)
        {
            long total = 0;
            textures.Clear(); // reused across cells; one set, not one per cell

            foreach (var name in cell.Models)
            {
                if (!weights.TryGetValue(name, out var weight)) continue;
                total += weight.ModelBytes;
                if (!string.IsNullOrEmpty(weight.TextureName)) textures.Add(weight.TextureName);
            }

            foreach (var textureName in textures)
                if (textureBytes.TryGetValue(textureName, out var bytes)) total += bytes;

            if (total > heaviest) heaviest = total;
        }

        return heaviest;
    }

    /// <summary>
    /// Busiest cell by object count, with the bytes that cell would have to stream, or null for an
    /// empty map.
    /// </summary>
    public DensestArea? Densest(IReadOnlyDictionary<string, ModelWeight>? weights)
    {
        (long X, long Y)? busiestKey = null;
        var busiestCount = -1;

        foreach (var (key, cell) in _cells)
        {
            // Ties broken by position so the answer doesn't depend on dictionary ordering.
            if (cell.ObjectCount < busiestCount) continue;
            if (cell.ObjectCount == busiestCount && busiestKey is { } current &&
                (key.X, key.Y).CompareTo((current.X, current.Y)) >= 0) continue;

            busiestCount = cell.ObjectCount;
            busiestKey = key;
        }

        if (busiestKey is not { } winner) return null;

        var winningCell = _cells[winner];
        long bytes = 0;

        if (weights is not null)
        {
            var textureBytes = PlacementDensity.TextureBytesFor(weights);
            var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in winningCell.Models)
            {
                if (!weights.TryGetValue(name, out var weight)) continue;
                bytes += weight.ModelBytes;
                if (!string.IsNullOrEmpty(weight.TextureName)) textures.Add(weight.TextureName);
            }
            foreach (var textureName in textures)
                if (textureBytes.TryGetValue(textureName, out var value)) bytes += value;
        }

        return new DensestArea(
            (winner.X + 0.5) * PlacementDensity.AreaSizeUnits,
            (winner.Y + 0.5) * PlacementDensity.AreaSizeUnits,
            winningCell.ObjectCount,
            bytes);
    }
}
