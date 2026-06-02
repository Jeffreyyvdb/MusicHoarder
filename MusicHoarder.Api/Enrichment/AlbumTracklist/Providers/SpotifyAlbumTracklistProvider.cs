using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Enrichment.AlbumTracklist.Providers;

/// <summary>
/// Album tracklists from the Spotify Web API. Resolves the album id from a song's stored track id
/// (precise) or an artist+album search, then fetches the album's tracks. Gated on the Spotify provider
/// flag plus configured app credentials (Settings UI row or options).
/// </summary>
public sealed class SpotifyAlbumTracklistProvider(
    IServiceScopeFactory scopeFactory,
    ISpotifyCatalogSearchService catalog,
    IOwnerLookupService ownerLookup,
    IOptions<SpotifyOptions> spotifyOptions,
    ILogger<SpotifyAlbumTracklistProvider> logger) : IAlbumTracklistProvider
{
    public EnrichmentProvider Source => EnrichmentProvider.SpotifyAPI;

    public bool IsEnabled(MusicEnricherOptions options) => options.EnableSpotifyApiProvider;

    public async Task<AlbumTracklistCandidate?> FetchAsync(AlbumQuery query, CancellationToken ct = default)
    {
        var (clientId, clientSecret) = await ResolveCredentialsAsync(ct);
        if (clientId is null || clientSecret is null)
        {
            logger.LogDebug("Spotify album tracklist skipped: no app credentials");
            return null;
        }

        string? albumId = null;
        if (!string.IsNullOrWhiteSpace(query.SpotifyTrackId))
            albumId = await catalog.GetTrackAlbumIdAsync(clientId, clientSecret, query.SpotifyTrackId, ct);
        albumId ??= await catalog.SearchAlbumIdAsync(clientId, clientSecret, query.AlbumArtist, query.Album, ct);
        if (albumId is null)
            return null;

        var album = await catalog.GetAlbumAsync(clientId, clientSecret, albumId, ct);
        if (album is null || album.Tracks.Count == 0)
            return null;

        return new AlbumTracklistCandidate(
            Source,
            album.Id,
            album.Name,
            album.Artist,
            album.Year,
            album.ImageUrl,
            album.Tracks
                .Select(t => new CandidateTrack(t.DiscNumber, t.TrackNumber, t.Title, t.DurationMs, t.Id))
                .ToList());
    }

    private async Task<(string? ClientId, string? ClientSecret)> ResolveCredentialsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var settings = await db.SpotifySettings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OwnerUserId == ownerLookup.OwnerUserId, ct);
        return SpotifyAppCredentialsResolver.Resolve(settings, spotifyOptions.Value);
    }
}
