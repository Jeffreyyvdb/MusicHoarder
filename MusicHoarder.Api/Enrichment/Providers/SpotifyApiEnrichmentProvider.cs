using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Matching;
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

        var resolved = SongSearchText.ResolveDetailed(song, options.Value.SourceDirectory);
        var (effectiveArtist, effectiveAlbum, effectiveTitle) = (resolved.Artist, resolved.Album, resolved.Title);
        if (string.IsNullOrWhiteSpace(effectiveArtist) || string.IsNullOrWhiteSpace(effectiveTitle))
        {
            logger.LogDebug("Spotify API enrichment: no searchable artist/title (SongId={SongId})", song.Id);
            return new ProviderNoMatch();
        }

        // Untagged files: let the search engine parse the cleaned filename free-text rather than a
        // positional artist/title guess (which on loose downloads is the download-tool/bucket folder).
        var query = resolved.IdentityFromPath && !string.IsNullOrWhiteSpace(resolved.PathQuery)
            ? SpotifyLibraryComparisonService.Normalize(resolved.PathQuery!)
            : BuildSearchQuery(effectiveArtist!, effectiveTitle!, effectiveAlbum);
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
            var fileIsrc = ProviderIdentity.NormalizeIsrc(song.Isrc);
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
        var best = CatalogMatchResolver.SelectBest(
            tracks, track => ScoreCandidate(song, resolved, track, opts));
        if (best is null)
            return new ProviderNoMatch();

        // Best-effort album-detail fetch for the winning candidate only — supplies the descriptive
        // album-level fields (Copyright, Label, UPC, full release date) the track search omits. Any
        // failure (including a rate limit on this secondary call) is swallowed: the core match already
        // succeeded, and these fields are optional extras.
        var album = await TryGetAlbumDetailAsync(clientId, clientSecret, best.Candidate.AlbumId, ct);

        return CatalogMatchResolver.Finalize(
            best.Score,
            best.Warnings,
            new CatalogMatchResolver.MatchThresholds(opts.SpotifyApiMinConfidence, opts.SpotifyApiMatchedThreshold),
            status => BuildResult(song, best.Candidate, best.Score, best.Warnings, status, album));
    }

    private async Task<SpotifyAlbumDetail?> TryGetAlbumDetailAsync(
        string clientId, string clientSecret, string? albumId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(albumId))
            return null;

        try
        {
            return await catalogSearch.GetAlbumAsync(clientId, clientSecret, albumId!, ct);
        }
        catch (ProviderRateLimitedException)
        {
            // Don't fail (or defer) the whole match for the optional descriptive extras.
            logger.LogDebug("Spotify album-detail fetch rate limited for album {AlbumId}; skipping copyright/label", albumId);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Spotify album-detail fetch failed for album {AlbumId}", albumId);
            return null;
        }
    }

    private EnrichmentProviderResult BuildResult(
        SongMetadata song,
        SpotifyCatalogTrack track,
        double score,
        List<string> warnings,
        EnrichmentStatus status,
        SpotifyAlbumDetail? album = null)
    {
        var (effectiveArtist, albumArtist) = CatalogResultArtists.Resolve(song, track.Artist);

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
            Isrc: string.IsNullOrWhiteSpace(track.Isrc) ? null : ProviderIdentity.NormalizeIsrc(track.Isrc),
            MatchedBy: Name,
            MatchConfidence: Math.Clamp(score, 0, 1),
            MatchWarnings: warnings,
            RecommendedStatus: status,
            Album: string.IsNullOrWhiteSpace(track.AlbumName) ? null : track.AlbumName,
            Artists: track.Artists,
            DiscNumber: track.DiscNumber,
            TotalTracks: track.TotalTracks,
            IsCompilation: string.Equals(track.AlbumType, "compilation", StringComparison.OrdinalIgnoreCase) ? true : null,
            ReleaseTypePrimary: string.IsNullOrWhiteSpace(track.AlbumType) ? null : track.AlbumType,
            ReleaseTypes: string.IsNullOrWhiteSpace(track.AlbumType) ? null : track.AlbumType,
            DurationSeconds: track.DurationMs > 0 ? track.DurationMs / 1000 : null,
            // Descriptive album-level fields from the album detail (fetched only for the winning match).
            // Copyright is Spotify-only; the rest are corroborating sources for the MusicBrainz values.
            ReleaseDate: album?.ReleaseDate,
            Label: album?.Label,
            Upc: album?.Upc,
            Copyright: album?.Copyright);
    }

    private static string BuildSearchQuery(string artist, string title, string? album)
    {
        // Album, when known (from tags or the file path), sharpens the free-text search so common
        // titles resolve to the right release; it stays plain text the normalizer can fold.
        var combined = string.IsNullOrWhiteSpace(album) ? $"{artist} {title}" : $"{artist} {title} {album}";
        return SpotifyLibraryComparisonService.Normalize(combined);
    }

    // Spotify is the reference tuning the other catalog providers mirror; the shared scorer applies
    // the signals in the common order (identity → ISRC → duration → version → album → track-number).
    // The album-agreement step compares the file's own album (song.Album), which a path-derived hint
    // leaves null — equivalent to the previous explicit AlbumFromPath guard, since an album guessed
    // from the path is only ever set when the embedded album tag was missing.
    private static (double Score, List<string> Warnings) ScoreCandidate(
        SongMetadata song,
        SongSearchText.Resolved source,
        SpotifyCatalogTrack track,
        MusicEnricherOptions opts)
        => CatalogCandidateScorer.Score(
            song,
            source,
            new CatalogCandidateScorer.CatalogCandidate(
                track.Artist, track.Title, track.AlbumName, track.Isrc, track.DurationMs, track.TrackNumber),
            new CatalogCandidateScorer.ScoringTuning(
                DurationDeltaThresholdSeconds: opts.SpotifyApiDurationDeltaThresholdSeconds,
                DurationMismatchPenalty: opts.SpotifyApiDurationMismatchPenalty,
                VersionMismatchPenalty: 0.6,
                AlbumAgreementConfidenceBoost: opts.AlbumAgreementConfidenceBoost,
                Isrc: new CatalogCandidateScorer.IsrcScoring(
                    ConfidenceBoost: opts.SpotifyApiIsrcConfidenceBoost,
                    MismatchPenalty: 0.65,
                    NotOnCandidateWarning: "isrc_not_on_spotify_track"),
                TrackNumberBoost: 0.02));
}
