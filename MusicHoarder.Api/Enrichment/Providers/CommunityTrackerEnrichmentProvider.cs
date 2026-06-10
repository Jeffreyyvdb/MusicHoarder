using Microsoft.Extensions.Options;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment.Providers;

/// <summary>
/// Shared logic for community-tracker providers (unreleased / leaked files). Each tracker is a
/// single-artist database that mainstream catalogs (Spotify / MusicBrainz / AcoustID) don't cover,
/// so it's gated to a configured artist allowlist to avoid wasted calls and false matches on
/// unrelated music. Concrete subclasses pick the <see cref="Name"/>, <see cref="Priority"/>,
/// artist allowlist and backing <see cref="ITrackerCatalogService"/> (live API or local catalog) —
/// the matching/scoring is identical for every tracker.
/// </summary>
public abstract class CommunityTrackerEnrichmentProvider(
    ITrackerCatalogService catalog,
    IOptions<MusicEnricherOptions> options,
    ILogger logger) : IEnrichmentProvider
{
    private const double FuzzyThreshold = 85.0;

    public abstract string Name { get; }
    public abstract int Priority { get; }

    /// <summary>Artist names/aliases this tracker covers; the gate opens only on a fuzzy match.</summary>
    protected abstract IReadOnlyList<string> ArtistAllowlist { get; }

    protected MusicEnricherOptions Options => options.Value;

    public bool CanHandle(SongMetadata song)
    {
        if (!SongSearchText.HasSearchableText(song, Options.SourceDirectory))
            return false;

        var (artist, _) = SongSearchText.Resolve(song, Options.SourceDirectory);
        return MatchesArtistAllowlist(artist);
    }

    public async Task<ProviderOutcome> TryEnrichAsync(SongMetadata song, CancellationToken ct = default)
    {
        var (_, effectiveTitle) = SongSearchText.Resolve(song, Options.SourceDirectory);
        if (string.IsNullOrWhiteSpace(effectiveTitle))
        {
            logger.LogDebug("{Provider} enrichment: no searchable title (SongId={SongId})", Name, song.Id);
            return new ProviderNoMatch();
        }

        IReadOnlyList<TrackerSong> candidates;
        try
        {
            candidates = await catalog.SearchAsync(effectiveTitle!, ct);
        }
        catch (ProviderRateLimitedException ex)
        {
            logger.LogWarning("{Provider} rate limited for song {SongId}, retry after {Delay}s",
                Name, song.Id, ex.RetryAfter.TotalSeconds);
            return new ProviderRateLimited(ex.RetryAfter);
        }

        logger.LogInformation(
            "{Provider} searched '{Query}' for SongId={SongId} → {CandidateCount} candidate(s)",
            Name, effectiveTitle, song.Id, candidates.Count);

        if (candidates.Count == 0)
            return new ProviderNoMatch();

        var opts = Options;
        TrackerSong? best = null;
        double bestScore = 0;
        double bestDurationDelta = double.PositiveInfinity;
        var bestWarnings = new List<string>();

        foreach (var candidate in candidates)
        {
            var (score, warnings, durationDelta) = ScoreCandidate(song, effectiveTitle, candidate, opts);

            // Highest title agreement wins. But a single song often has many versions with the same
            // title ([V1], [V2], …) that tie on title score — so the length closest to the file is the
            // tiebreaker that picks the right version (a candidate with no length sorts last).
            var better = score > bestScore + 1e-9
                || (score > bestScore - 1e-9 && durationDelta < bestDurationDelta);
            if (better)
            {
                bestScore = score;
                best = candidate;
                bestWarnings = warnings;
                bestDurationDelta = durationDelta;
            }
        }

        if (best is null)
            return new ProviderNoMatch();

        if (bestScore < opts.TrackerMinConfidence - 1e-9)
            return new ProviderNoMatch(BuildResult(song, best, bestScore, bestWarnings, EnrichmentStatus.NeedsReview));

        var blocking = HasBlockingWarning(bestWarnings);
        var status = bestScore >= opts.TrackerMatchedThreshold - 1e-9 && !blocking
            ? EnrichmentStatus.Matched
            : EnrichmentStatus.NeedsReview;

        return new ProviderMatched(BuildResult(song, best, bestScore, bestWarnings, status));
    }

    private bool MatchesArtistAllowlist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return false;

        foreach (var allowed in ArtistAllowlist)
        {
            if (FuzzyTextMatch.Ratio(artist, allowed) is double ratio && ratio >= Options.IdentityArtistThreshold)
                return true;
        }

        return false;
    }

    private EnrichmentProviderResult BuildResult(
        SongMetadata song,
        TrackerSong track,
        double score,
        List<string> warnings,
        EnrichmentStatus status)
    {
        // Keep the song's existing (allowlisted) artist when it has one; only fall back to the
        // tracker's credit for otherwise-untagged files.
        var artist = string.IsNullOrWhiteSpace(song.Artist)
            ? ArtistCreditNormalizer.NormalizeDisplayCredit(track.CreditedArtists)
            : song.Artist;
        // Album-artist is album-level — don't derive it from the track credit (a featured guest on
        // compilations/collabs, and comma-names get truncated). Keep the song's curated album-artist;
        // fall back to the track's primary artist only for genuinely untagged files.
        var albumArtist = !string.IsNullOrWhiteSpace(song.AlbumArtist)
            ? song.AlbumArtist
            : ArtistCreditNormalizer.GetPrimaryArtist(artist) ?? artist;

        var resultWarnings = new List<string>(warnings);
        if (!string.IsNullOrWhiteSpace(track.Category))
            resultWarnings.Add($"category:{track.Category}");

        return new EnrichmentProviderResult(
            Artist: artist,
            AlbumArtist: albumArtist,
            Title: string.IsNullOrWhiteSpace(track.Name) ? song.Title : track.Name,
            Year: track.Year,
            TrackNumber: null,
            MusicBrainzId: null,
            MusicBrainzReleaseId: null,
            SpotifyId: null,
            AcoustIdTrackId: null,
            Isrc: null,
            MatchedBy: Name,
            MatchConfidence: Math.Clamp(score, 0, 1),
            MatchWarnings: resultWarnings,
            RecommendedStatus: status,
            Album: string.IsNullOrWhiteSpace(track.Era) ? null : track.Era);
    }

    private static (double Score, List<string> Warnings, double DurationDelta) ScoreCandidate(
        SongMetadata song,
        string? sourceTitle,
        TrackerSong track,
        MusicEnricherOptions opts)
    {
        var warnings = new List<string>();

        // The DB is single-artist (and artist agreement was already established by the allowlist
        // gate), and leaks are heavily aliased — so the match is driven by the best title agreement
        // across the canonical name and every alias track title.
        double? bestTitleRatio = FuzzyTextMatch.Ratio(sourceTitle, track.Name);
        foreach (var alias in track.TrackTitles)
        {
            if (FuzzyTextMatch.Ratio(sourceTitle, alias) is double r &&
                (bestTitleRatio is not double current || r > current))
            {
                bestTitleRatio = r;
            }
        }

        if (bestTitleRatio is not double titleRatio)
            return (0, ["title_unknown"], double.PositiveInfinity);

        if (titleRatio < FuzzyThreshold)
            warnings.Add("title_mismatch");

        var score = titleRatio / 100.0;

        // Distance (seconds) between the file and this candidate; PositiveInfinity when either side
        // has no length, so length-less candidates lose the version tiebreak to ones that match.
        var durationDelta = double.PositiveInfinity;
        var songDurationSec = song.DurationSeconds
            ?? (song.DurationMs is int ms ? ms / 1000.0 : (double?)null);
        if (songDurationSec is not null && track.DurationSeconds is double trackDuration && trackDuration > 0)
        {
            durationDelta = Math.Abs(songDurationSec.Value - trackDuration);
            if (durationDelta > opts.SpotifyApiDurationDeltaThresholdSeconds)
            {
                warnings.Add("duration_mismatch");
                score *= opts.SpotifyApiDurationMismatchPenalty;
            }
        }

        // Keep a "Live"/"Remix"/"Instrumental" alias from satisfying a request for the studio cut.
        var sourceQual = VersionQualifier.Detect(song.Title, song.Album);
        var candQual = VersionQualifier.Detect(track.Name);
        if (!VersionQualifier.Compare(sourceQual, candQual))
        {
            warnings.Add("version_mismatch");
            score *= 0.6;
        }

        return (Math.Clamp(score, 0, 1), warnings, durationDelta);
    }

    private static bool HasBlockingWarning(List<string> warnings) =>
        warnings.Exists(static w => w is "title_mismatch" or "title_unknown" or "duration_mismatch" or "version_mismatch");
}
