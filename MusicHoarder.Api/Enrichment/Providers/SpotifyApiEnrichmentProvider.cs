using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Enrichment.Providers;

public class SpotifyApiEnrichmentProvider(
    IServiceScopeFactory scopeFactory,
    ISpotifyCatalogSearchService catalogSearch,
    IOwnerLookupService ownerLookup,
    IOptions<MusicEnricherOptions> options,
    IOptions<SpotifyOptions> spotifyOptions,
    ILogger<SpotifyApiEnrichmentProvider> logger) : IEnrichmentProvider
{
    private const double FuzzyThreshold = 85.0;

    public string Name => "SpotifyAPI";
    public int Priority => 300;

    public bool CanHandle(SongMetadata song) =>
        SongSearchText.HasSearchableText(song, options.Value.SourceDirectory);

    public async Task<ProviderOutcome> TryEnrichAsync(SongMetadata song, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var ownerId = ownerLookup.OwnerUserId;
        var settings = await db.SpotifySettings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OwnerUserId == ownerId, ct);
        var (clientId, clientSecret) = SpotifyAppCredentialsResolver.Resolve(settings, spotifyOptions.Value);

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            logger.LogDebug("Spotify API enrichment skipped: no ClientId/ClientSecret (SongId={SongId})", song.Id);
            return new ProviderNoMatch();
        }

        var (effectiveArtist, effectiveTitle) = SongSearchText.Resolve(song, options.Value.SourceDirectory);
        if (string.IsNullOrWhiteSpace(effectiveArtist) || string.IsNullOrWhiteSpace(effectiveTitle))
        {
            logger.LogDebug("Spotify API enrichment: no searchable artist/title (SongId={SongId})", song.Id);
            return new ProviderNoMatch();
        }

        var query = BuildSearchQuery(effectiveArtist!, effectiveTitle!);
        if (string.IsNullOrWhiteSpace(query))
        {
            logger.LogDebug("Spotify API enrichment: empty query after normalize (SongId={SongId})", song.Id);
            return new ProviderNoMatch();
        }

        IReadOnlyList<SpotifyCatalogTrack> tracks;
        try
        {
            // Identifier-first: if the file already carries an ISRC, ask Spotify for that exact
            // recording before falling back to a fuzzy artist+title search.
            var fileIsrc = NormalizeIsrc(song.Isrc);
            tracks = [];
            if (!string.IsNullOrEmpty(fileIsrc))
            {
                tracks = await catalogSearch.SearchTracksAsync(clientId, clientSecret, $"isrc:{fileIsrc}", ct);
                if (tracks.Count > 0)
                    logger.LogDebug("Spotify ISRC lookup hit for SongId={SongId} ({Isrc})", song.Id, fileIsrc);
            }

            if (tracks.Count == 0)
                tracks = await catalogSearch.SearchTracksAsync(clientId, clientSecret, query, ct);
        }
        catch (ProviderRateLimitedException ex)
        {
            logger.LogWarning("Spotify rate limited for song {SongId}, retry after {Delay}s",
                song.Id, ex.RetryAfter.TotalSeconds);
            return new ProviderRateLimited(ex.RetryAfter);
        }

        if (tracks.Count == 0)
        {
            logger.LogDebug("Spotify search returned no tracks for SongId={SongId}", song.Id);
            return new ProviderNoMatch();
        }

        var opts = options.Value;
        SpotifyCatalogTrack? bestTrack = null;
        double bestScore = 0;
        var bestWarnings = new List<string>();

        foreach (var track in tracks)
        {
            var (score, warnings) = ScoreCandidate(song, effectiveArtist, effectiveTitle, track, opts);
            if (score > bestScore)
            {
                bestScore = score;
                bestTrack = track;
                bestWarnings = warnings;
            }
        }

        if (bestTrack is null)
            return new ProviderNoMatch();

        if (bestScore < opts.SpotifyApiMinConfidence - 1e-9)
            return new ProviderNoMatch(BuildResult(song, bestTrack, bestScore, bestWarnings, EnrichmentStatus.NeedsReview));

        var blocking = HasBlockingWarning(bestWarnings);
        var status = bestScore >= opts.SpotifyApiMatchedThreshold - 1e-9 && !blocking
            ? EnrichmentStatus.Matched
            : EnrichmentStatus.NeedsReview;

        return new ProviderMatched(BuildResult(song, bestTrack, bestScore, bestWarnings, status));
    }

    private EnrichmentProviderResult BuildResult(
        SongMetadata song,
        SpotifyCatalogTrack track,
        double score,
        List<string> warnings,
        EnrichmentStatus status)
    {
        var effectiveArtist = string.IsNullOrWhiteSpace(track.Artist) ? song.Artist : track.Artist;
        var albumArtist = ArtistCreditNormalizer.GetPrimaryArtist(effectiveArtist) ?? effectiveArtist;

        return new EnrichmentProviderResult(
            Artist: effectiveArtist,
            AlbumArtist: albumArtist,
            Title: string.IsNullOrWhiteSpace(track.Title) ? song.Title : track.Title,
            Year: track.ReleaseYear,
            TrackNumber: track.TrackNumber,
            MusicBrainzId: null,
            MusicBrainzReleaseId: null,
            SpotifyId: track.Id,
            AcoustIdTrackId: null,
            Isrc: string.IsNullOrWhiteSpace(track.Isrc) ? null : NormalizeIsrc(track.Isrc),
            MatchedBy: Name,
            MatchConfidence: Math.Clamp(score, 0, 1),
            MatchWarnings: warnings,
            RecommendedStatus: status,
            Album: string.IsNullOrWhiteSpace(track.AlbumName) ? null : track.AlbumName);
    }

    private static string BuildSearchQuery(string artist, string title)
    {
        var combined = $"{artist} {title}";
        return SpotifyLibraryComparisonService.Normalize(combined);
    }

    private static (double Score, List<string> Warnings) ScoreCandidate(
        SongMetadata song,
        string? sourceArtist,
        string? sourceTitle,
        SpotifyCatalogTrack track,
        MusicEnricherOptions opts)
    {
        var warnings = new List<string>();

        // Raw-fallback aware: a symbol-only artist like "¥$" no longer scores as a free 100%.
        var artistRatio = FuzzyTextMatch.Ratio(sourceArtist, track.Artist);
        var titleRatio = FuzzyTextMatch.Ratio(sourceTitle, track.Title);

        if (artistRatio is double ar && ar < FuzzyThreshold)
            warnings.Add("artist_mismatch");
        if (titleRatio is double tr && tr < FuzzyThreshold)
            warnings.Add("title_mismatch");

        double score;
        if (artistRatio is double a && titleRatio is double t)
        {
            score = (a / 100.0 + t / 100.0) / 2.0;
        }
        else if (titleRatio is double tOnly)
        {
            // No usable artist signal — a title-only agreement isn't enough to auto-match,
            // so surface it for review (blocking warning) instead of trusting it blindly.
            score = tOnly / 100.0;
            warnings.Add("artist_unknown");
        }
        else
        {
            score = 0;
        }

        var fileIsrc = NormalizeIsrc(song.Isrc);
        var trackIsrc = NormalizeIsrc(track.Isrc);
        if (!string.IsNullOrEmpty(fileIsrc))
        {
            if (!string.IsNullOrEmpty(trackIsrc))
            {
                if (string.Equals(fileIsrc, trackIsrc, StringComparison.Ordinal))
                    score = Math.Min(1.0, score + opts.SpotifyApiIsrcConfidenceBoost);
                else
                {
                    warnings.Add("isrc_mismatch");
                    score *= 0.65;
                }
            }
            else
            {
                warnings.Add("isrc_not_on_spotify_track");
            }
        }

        var songDurationSec = song.DurationSeconds
            ?? (song.DurationMs is int ms ? ms / 1000.0 : (double?)null);
        if (songDurationSec is not null && track.DurationMs > 0)
        {
            var delta = Math.Abs(songDurationSec.Value - track.DurationMs / 1000.0);
            if (delta > opts.SpotifyApiDurationDeltaThresholdSeconds)
            {
                warnings.Add("duration_mismatch");
                score *= opts.SpotifyApiDurationMismatchPenalty;
            }
        }

        // Keep a "Live"/"Remix"/"Acoustic" candidate from satisfying a studio request (and vice-versa).
        var sourceQual = VersionQualifier.Detect(song.Title, song.Album);
        var candQual = VersionQualifier.Detect(track.Title, track.AlbumName);
        if (!VersionQualifier.Compare(sourceQual, candQual))
        {
            warnings.Add("version_mismatch");
            score *= 0.6;
        }

        return (Math.Clamp(score, 0, 1), warnings);
    }

    private static bool HasBlockingWarning(List<string> warnings) =>
        warnings.Exists(static w => w is "duration_mismatch" or "artist_mismatch" or "title_mismatch" or "isrc_mismatch" or "version_mismatch" or "artist_unknown");

    private static string NormalizeIsrc(string? isrc)
    {
        if (string.IsNullOrWhiteSpace(isrc))
            return string.Empty;
        return isrc.Trim().ToUpperInvariant().Replace("-", "", StringComparison.Ordinal);
    }
}
