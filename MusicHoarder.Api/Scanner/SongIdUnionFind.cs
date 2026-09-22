namespace MusicHoarder.Api.Scanner;

/// <summary>
/// Disjoint-set over song ids, for turning pairwise duplicate links into clusters. The root of every
/// set is its lowest id — <see cref="Union"/> always hangs the larger root under the smaller — so a
/// cluster's root doubles as a stable group id that survives a detection re-run for as long as its
/// lowest member does. Ids register lazily: the first <see cref="Find"/> or <see cref="Union"/> that
/// names an id makes it a singleton set, which is why <see cref="Clusters"/> covers every id ever
/// mentioned, linked or not.
/// </summary>
public sealed class SongIdUnionFind
{
    private readonly Dictionary<int, int> _parent = [];

    /// <summary>The root (lowest id) of the set containing <paramref name="x"/>, registering
    /// <paramref name="x"/> as a singleton when it is new. Compresses the path it walks.</summary>
    public int Find(int x)
    {
        if (!_parent.TryGetValue(x, out var p))
        {
            _parent[x] = x;
            return x;
        }
        if (p == x)
            return x;
        var root = Find(p);
        _parent[x] = root;
        return root;
    }

    /// <summary>Merges the sets of <paramref name="a"/> and <paramref name="b"/>; the lower root wins.</summary>
    public void Union(int a, int b)
    {
        var ra = Find(a);
        var rb = Find(b);
        if (ra != rb)
            _parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
    }

    /// <summary>Every registered id grouped with its set, singletons included, in no particular order.</summary>
    public IEnumerable<List<int>> Clusters() =>
        _parent.Keys.ToList().GroupBy(Find).Select(g => g.ToList());
}
