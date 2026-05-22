using Microsoft.Extensions.Options;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment.Providers;

/// <summary>
/// Community-tracker provider for unreleased / leaked files. Backed by a single-artist database
/// (juicewrldapi.com) that mainstream catalogs (Spotify / MusicBrainz / AcoustID) don't cover, so
/// it's gated to a configured artist allowlist (<see cref="MusicEnricherOptions.TrackerArtistAllowlist"/>)
/// to avoid wasted calls and false matches on unrelated music. Enabled by default
/// (<see cref="MusicEnricherOptions.EnableTrackerProvider"/>); the allowlist gate means it's a
/// no-op for songs outside the artists it covers.
/// </summary>
public class TrackerEnrichmentProvider(
    ITrackerCatalogService catalog,
    IOptions<MusicEnricherOptions> options,
    ILogger<TrackerEnrichmentProvider> logger) : IEnrichmentProvider
{
    private const double FuzzyThreshold = 85.0;

    public string Name => "Tracker";
    public int Priority => 400;

    public bool CanHandle(SongMetadata song)
    {
        if (!SongSearchText.HasSearchableText(song, options.Value.SourceDirectory))
            return false;

        var (artist, _) = SongSearchText.Resolve(song, options.Value.SourceDirectory);
        return MatchesArtistAllowlist(artist);
    }

    public async Task<ProviderOutcome> TryEnrichAsync(SongMetadata song, CancellationToken ct = default)
    {
        var (effectiveArtist, effectiveTitle) = SongSearchText.Resolve(song, options.Value.SourceDirectory);
        if (string.IsNullOrWhiteSpace(effectiveTitle))
        {
            logger.LogDebug("Tracker enrichment: no searchable title (SongId={SongId})", song.Id);
            return new ProviderNoMatch();
        }

        IReadOnlyList<TrackerSong> candidates;
        try
        {
            candidates = await catalog.SearchAsync(effectiveTitle!, ct);
        }
        catch (ProviderRateLimitedException ex)
        {
            logger.LogWarning("Tracker rate limited for song {SongId}, retry after {Delay}s",
                song.Id, ex.RetryAfter.TotalSeconds);
            return new ProviderRateLimited(ex.RetryAfter);
        }

        if (candidates.Count == 0)
        {
            logger.LogDebug("Tracker search returned no songs for SongId={SongId}", song.Id);
            return new ProviderNoMatch();
        }

        var opts = options.Value;
        TrackerSong? best = null;
        double bestScore = 0;
        var bestWarnings = new List<string>();

        foreach (var candidate in candidates)
        {
            var (score, warnings) = ScoreCandidate(song, effectiveTitle, candidate, opts);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
                bestWarnings = warnings;
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

        foreach (var allowed in options.Value.TrackerArtistAllowlist)
        {
            if (FuzzyTextMatch.Ratio(artist, allowed) is double ratio && ratio >= options.Value.IdentityArtistThreshold)
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
        // tracker's credit (often a sub-alias like "JuiceTheKidd") for otherwise-untagged files.
        var artist = string.IsNullOrWhiteSpace(song.Artist)
            ? ArtistCreditNormalizer.NormalizeDisplayCredit(track.CreditedArtists)
            : song.Artist;
        var albumArtist = ArtistCreditNormalizer.GetPrimaryArtist(artist) ?? artist;

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

    private static (double Score, List<string> Warnings) ScoreCandidate(
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
            return (0, ["title_unknown"]);

        if (titleRatio < FuzzyThreshold)
            warnings.Add("title_mismatch");

        var score = titleRatio / 100.0;

        var songDurationSec = song.DurationSeconds
            ?? (song.DurationMs is int ms ? ms / 1000.0 : (double?)null);
        if (songDurationSec is not null && track.DurationSeconds is double trackDuration && trackDuration > 0)
        {
            var delta = Math.Abs(songDurationSec.Value - trackDuration);
            if (delta > opts.SpotifyApiDurationDeltaThresholdSeconds)
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

        return (Math.Clamp(score, 0, 1), warnings);
    }

    private static bool HasBlockingWarning(List<string> warnings) =>
        warnings.Exists(static w => w is "title_mismatch" or "title_unknown" or "duration_mismatch" or "version_mismatch");
}
