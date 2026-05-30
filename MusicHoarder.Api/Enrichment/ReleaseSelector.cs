using MusicHoarder.Api.Matching;

namespace MusicHoarder.Api.Enrichment;

/// <summary>
/// Decides which <b>release</b> (album / year / track number …) a corroborated recording should be
/// attributed to. Recording identity (same ISRC / title / artist / duration) and release
/// attribution are two separate decisions: a single recording legitimately appears on the original
/// album, on later compilations, and on "greatest hits" reissues. The consensus winner supplies the
/// recording identity; this selector supplies the album-level fields by <i>corroborating across the
/// whole agreeing cluster</i> rather than copying whatever release the highest-priority provider
/// happened to return.
/// <para>
/// Candidates are clustered into release groups by fuzzy album agreement (so "The Score" and
/// "The Score (Expanded Edition)" count as one release). The winning release is the one the most
/// distinct providers vote for; ties are broken toward the earliest, non-compilation release and
/// then toward the file's own album tag. Within the winning release, each album-level field is
/// reported as <b>corroborated</b> only when ≥2 providers agree on its value — the signal the
/// merger uses to decide whether it may overwrite a good embedded tag.
/// </para>
/// </summary>
public static class ReleaseSelector
{
    /// <summary>Fuzzy ratio (0–100) above which two album titles are treated as the same release.</summary>
    public const double DefaultAlbumIdentityThreshold = 85.0;

    public sealed record Selection(
        string? Album,
        int? Year,
        int? TrackNumber,
        int? DiscNumber,
        int? TotalDiscs,
        int? TotalTracks,
        string? ReleaseTypePrimary,
        string? ReleaseTypes,
        bool? IsCompilation,
        IReadOnlySet<string> CorroboratedFields);

    private static readonly Selection Empty = new(
        null, null, null, null, null, null, null, null, null, new HashSet<string>());

    /// <summary>
    /// Picks the canonical release for <paramref name="cluster"/> (the candidates that agreed on the
    /// recording). <paramref name="embeddedAlbum"/>/<paramref name="embeddedYear"/> are the file's own
    /// tags, used only to break ties and to keep the curated album-edition string when it agrees.
    /// </summary>
    public static Selection Select(
        IReadOnlyList<EnrichmentProviderResult> cluster,
        string? embeddedAlbum,
        int? embeddedYear,
        bool preferOriginalRelease,
        double albumIdentityThreshold = DefaultAlbumIdentityThreshold)
    {
        // Only candidates that name an album can vote on a release.
        var withAlbum = cluster.Where(c => !string.IsNullOrWhiteSpace(c.Album)).ToList();
        if (withAlbum.Count == 0)
            return Empty;

        // Cluster candidates into release groups by fuzzy album agreement, so editions of the same
        // album ("The Score" / "The Score (Expanded Edition)") count as one release.
        var groups = new List<List<EnrichmentProviderResult>>();
        foreach (var c in withAlbum)
        {
            var placed = false;
            foreach (var g in groups)
            {
                if (FuzzyTextMatch.Ratio(g[0].Album, c.Album) is double r && r >= albumIdentityThreshold)
                {
                    g.Add(c);
                    placed = true;
                    break;
                }
            }

            if (!placed)
                groups.Add([c]);
        }

        // Most-corroborated release wins; only then prefer the original (non-compilation, earliest)
        // pressing, then the one matching the file's own album tag, then aggregate confidence.
        var best = groups
            .OrderByDescending(DistinctProviders)
            .ThenByDescending(g => preferOriginalRelease && IsCompilationGroup(g) ? 0 : 1)
            .ThenBy(g => preferOriginalRelease ? EarliestYear(g) ?? int.MaxValue : 0)
            .ThenByDescending(g => MatchesEmbedded(g, embeddedAlbum, albumIdentityThreshold) ? 1 : 0)
            .ThenByDescending(g => g.Sum(c => c.MatchConfidence))
            .First();

        var corroborated = new HashSet<string>();
        var groupProviders = DistinctProviders(best);

        // Album: every member of the winning group agrees (that's how the group formed), so the album
        // is corroborated when ≥2 providers backed it. Keep the curated edition string when the file's
        // tag is one of those editions, so "(Expanded Edition)" isn't dropped for a barer title.
        string? album = !string.IsNullOrWhiteSpace(embeddedAlbum)
            && best.Any(c => FuzzyTextMatch.Ratio(embeddedAlbum, c.Album) is double r && r >= albumIdentityThreshold)
                ? embeddedAlbum
                : MostVotedString(best, c => c.Album);
        if (groupProviders >= 2)
            corroborated.Add("Album");

        var year = MostVotedNumber(best, c => c.Year, embeddedYear, out var yearVotes);
        if (yearVotes >= 2) corroborated.Add("Year");

        var track = MostVotedNumber(best, c => c.TrackNumber, null, out var trackVotes);
        if (trackVotes >= 2) corroborated.Add("TrackNumber");

        var disc = MostVotedNumber(best, c => c.DiscNumber, null, out var discVotes);
        if (discVotes >= 2) corroborated.Add("DiscNumber");

        var totalDiscs = MostVotedNumber(best, c => c.TotalDiscs, null, out var totalDiscVotes);
        if (totalDiscVotes >= 2) corroborated.Add("TotalDiscs");

        var totalTracks = MostVotedNumber(best, c => c.TotalTracks, null, out var totalTrackVotes);
        if (totalTrackVotes >= 2) corroborated.Add("TotalTracks");

        var releaseTypePrimary = MostVotedString(best, c => c.ReleaseTypePrimary, out var rtpVotes);
        if (rtpVotes >= 2) corroborated.Add("ReleaseTypePrimary");

        var releaseTypes = MostVotedString(best, c => c.ReleaseTypes, out var rtVotes);
        if (rtVotes >= 2) corroborated.Add("ReleaseTypes");

        // Compilation is an additive fact: keep it true if any provider on the winning release says so.
        bool? isCompilation = best.Any(c => c.IsCompilation == true) ? true : null;

        return new Selection(
            album, year, track, disc, totalDiscs, totalTracks,
            releaseTypePrimary, releaseTypes, isCompilation, corroborated);
    }

    private static int DistinctProviders(List<EnrichmentProviderResult> group)
        => group.Select(c => c.MatchedBy).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    private static bool IsCompilationGroup(List<EnrichmentProviderResult> group)
        => group.Any(c => c.IsCompilation == true
            || string.Equals(c.ReleaseTypePrimary, "compilation", StringComparison.OrdinalIgnoreCase));

    private static int? EarliestYear(List<EnrichmentProviderResult> group)
    {
        var years = group.Where(c => c.Year is > 0).Select(c => c.Year!.Value).ToList();
        return years.Count == 0 ? null : years.Min();
    }

    private static bool MatchesEmbedded(List<EnrichmentProviderResult> group, string? embeddedAlbum, double threshold)
        => !string.IsNullOrWhiteSpace(embeddedAlbum)
            && group.Any(c => FuzzyTextMatch.Ratio(embeddedAlbum, c.Album) is double r && r >= threshold);

    private static string? MostVotedString(List<EnrichmentProviderResult> group, Func<EnrichmentProviderResult, string?> select)
        => MostVotedString(group, select, out _);

    /// <summary>Value (most distinct-provider votes; aggregate confidence breaks ties) and its vote count.</summary>
    private static string? MostVotedString(
        List<EnrichmentProviderResult> group, Func<EnrichmentProviderResult, string?> select, out int votes)
    {
        var present = group.Where(c => !string.IsNullOrWhiteSpace(select(c))).ToList();
        if (present.Count == 0) { votes = 0; return null; }

        var top = present
            .GroupBy(c => TitleNormalizer.NormalizeForSearch(select(c)))
            .Select(g => new
            {
                Providers = g.Select(c => c.MatchedBy).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                Confidence = g.Sum(c => c.MatchConfidence),
                Sample = g.OrderByDescending(c => c.MatchConfidence).First(),
            })
            .OrderByDescending(x => x.Providers)
            .ThenByDescending(x => x.Confidence)
            .First();

        votes = top.Providers;
        return select(top.Sample);
    }

    /// <summary>Value (most distinct-provider votes; embedded value then confidence break ties) and its vote count.</summary>
    private static int? MostVotedNumber(
        List<EnrichmentProviderResult> group, Func<EnrichmentProviderResult, int?> select, int? embedded, out int votes)
    {
        var present = group.Where(c => select(c) is int v && v > 0).ToList();
        if (present.Count == 0) { votes = 0; return null; }

        var top = present
            .GroupBy(c => select(c)!.Value)
            .Select(g => new
            {
                Value = g.Key,
                Providers = g.Select(c => c.MatchedBy).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                Confidence = g.Sum(c => c.MatchConfidence),
            })
            .OrderByDescending(x => x.Providers)
            .ThenByDescending(x => embedded.HasValue && x.Value == embedded.Value ? 1 : 0)
            .ThenByDescending(x => x.Confidence)
            .First();

        votes = top.Providers;
        return top.Value;
    }
}
