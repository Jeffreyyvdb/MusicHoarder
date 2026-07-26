namespace MusicHoarder.Api.Enrichment.Providers;

/// <summary>
/// The shared free-text search strategy for the catalog (name-based) enrichment providers that query
/// by artist/title (Deezer, Apple Music). Each provider talks to its own catalog client, but the
/// <b>decision of which query strings to try, and in what order</b> is identical across them:
/// <list type="bullet">
/// <item>a <b>path-derived</b> identity queries on the cleaned filename free-text alone
/// (<see cref="SongSearchText.Resolved.PathQuery"/>) — never a positional folder guess;</item>
/// <item>a <b>tagged</b> file queries on <c>"artist title"</c>, narrowed by the album first so the
/// original pressing surfaces ahead of a compilation, then falls back to the un-narrowed
/// <c>"artist title"</c> query so recall never drops when the album-narrowed search finds nothing.</item>
/// </list>
/// Centralising the plan here keeps that rule in one tested place instead of two providers copying the
/// same branch-and-fallback block and drifting out of sync.
/// </summary>
public static class CatalogSearchPlanner
{
    /// <summary>
    /// The ordered list of query strings to try for <paramref name="resolved"/>. The first entry is
    /// the most specific; later entries are recall fallbacks. Callers run them in order and stop at the
    /// first non-empty result set (see <see cref="SearchAsync{T}"/>).
    /// </summary>
    public static IReadOnlyList<string> PlanQueries(in SongSearchText.Resolved resolved)
    {
        // Untagged files: let the search engine parse the cleaned filename free-text rather than a
        // positional artist/title guess (which on loose downloads is the download-tool/bucket folder).
        var pathQuery = resolved.IdentityFromPath ? resolved.PathQuery : null;
        if (!string.IsNullOrWhiteSpace(pathQuery))
            return [pathQuery.Trim()];

        var baseQuery = $"{resolved.Artist} {resolved.Title}".Trim();
        if (string.IsNullOrWhiteSpace(resolved.Album))
            return [baseQuery];

        // Album sharpens the search so the original pressing surfaces ahead of a compilation; the
        // un-narrowed query is kept as a fallback so a missing/mismatched album tag never zeroes recall.
        return [$"{baseQuery} {resolved.Album}".Trim(), baseQuery];
    }

    /// <summary>
    /// Runs <see cref="PlanQueries"/> in order through <paramref name="search"/>, returning the first
    /// non-empty result set (or the last — empty — set if none hit). Mirrors the providers' original
    /// "try narrowed, then fall back" loop, so no extra call is made once a query returns results.
    /// </summary>
    public static async Task<IReadOnlyList<T>> SearchAsync<T>(
        SongSearchText.Resolved resolved,
        Func<string, CancellationToken, Task<IReadOnlyList<T>>> search,
        CancellationToken ct = default)
    {
        IReadOnlyList<T> results = [];
        foreach (var query in PlanQueries(resolved))
        {
            results = await search(query, ct);
            if (results.Count > 0)
                return results;
        }

        return results;
    }
}
