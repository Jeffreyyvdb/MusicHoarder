using FuzzySharp;

namespace MusicHoarder.Api.Matching;

/// <summary>
/// Thresholds for deciding whether two provider-proposed identities describe the
/// same recording. Built from <c>MusicEnricherOptions</c> by callers.
/// </summary>
public readonly record struct IdentityMatchOptions(
    double ArtistThreshold,
    double TitleThreshold,
    double DurationDeltaSeconds)
{
    public static IdentityMatchOptions Default => new(85, 85, 8);
}

/// <summary>
/// A single provider's proposed identity for a song. Consensus is computed by
/// clustering these via <see cref="AgreesWith"/>.
/// </summary>
public sealed record ProviderIdentity(
    string? Artist,
    string? Title,
    string? Album,
    int? DurationSeconds,
    string? Isrc,
    string? MusicBrainzId,
    string? SpotifyId,
    VersionQualifiers Qualifiers)
{
    /// <summary>
    /// True when this identity and <paramref name="other"/> describe the same recording:
    /// either they share a strong identifier (ISRC / MBID / Spotify ID), or their
    /// normalized artist+title match within thresholds, durations are close, and their
    /// strong version qualifiers agree.
    /// </summary>
    public bool AgreesWith(ProviderIdentity other, IdentityMatchOptions opts)
    {
        if (SharesStrongIdentifier(other))
            return true;

        if (!VersionQualifier.Compare(Qualifiers, other.Qualifiers))
            return false;

        var artistA = TitleNormalizer.NormalizeForSearch(Artist);
        var artistB = TitleNormalizer.NormalizeForSearch(other.Artist);
        var titleA = TitleNormalizer.NormalizeForSearch(Title);
        var titleB = TitleNormalizer.NormalizeForSearch(other.Title);

        // An empty side can't corroborate identity on its own.
        if (titleA.Length == 0 || titleB.Length == 0)
            return false;

        var titleRatio = Fuzz.WeightedRatio(titleA, titleB);
        if (titleRatio < opts.TitleThreshold)
            return false;

        // Artist may legitimately be missing on one side (e.g. fingerprint-only); only
        // penalize when both sides have an artist and they disagree.
        if (artistA.Length > 0 && artistB.Length > 0)
        {
            var artistRatio = Fuzz.WeightedRatio(artistA, artistB);
            if (artistRatio < opts.ArtistThreshold)
                return false;
        }

        if (DurationSeconds is int a && other.DurationSeconds is int b)
        {
            if (Math.Abs(a - b) > opts.DurationDeltaSeconds)
                return false;
        }

        return true;
    }

    private bool SharesStrongIdentifier(ProviderIdentity other)
        => IdEquals(NormalizeIsrc(Isrc), NormalizeIsrc(other.Isrc))
           || IdEquals(MusicBrainzId, other.MusicBrainzId)
           || IdEquals(SpotifyId, other.SpotifyId);

    private static bool IdEquals(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a)
           && !string.IsNullOrWhiteSpace(b)
           && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static string NormalizeIsrc(string? isrc)
        => string.IsNullOrWhiteSpace(isrc)
            ? string.Empty
            : isrc.Trim().ToUpperInvariant().Replace("-", "", StringComparison.Ordinal);
}
