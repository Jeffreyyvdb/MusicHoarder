using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Spotify;

/// <summary>
/// Best-effort resolution of a Spotify track id from an ISRC using the app (client-credentials) catalog
/// search. Used when snapshotting a Deezer discover playlist so a track that also exists on Spotify shares
/// the owner's cross-provider dedupe key. Returns null (never throws) when credentials are missing, the
/// ISRC is empty, or nothing matches — the caller inserts the item with a null Spotify id.
/// </summary>
public interface ISpotifyIsrcResolver
{
    Task<string?> ResolveTrackIdByIsrcAsync(string? isrc, CancellationToken ct = default);
}

public sealed class SpotifyIsrcResolver(
    ISpotifyCatalogSearchService catalog,
    IServiceScopeFactory scopeFactory,
    IOwnerLookupService ownerLookup,
    IOptions<SpotifyOptions> options,
    ILogger<SpotifyIsrcResolver> logger) : ISpotifyIsrcResolver
{
    public async Task<string?> ResolveTrackIdByIsrcAsync(string? isrc, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(isrc))
            return null;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        // Runs from background sweeps / Task.Run continuations where the HTTP user is gone, so scope the
        // settings read to the owner explicitly and bypass the ambient tenant filter.
        var ownerId = ownerLookup.OwnerUserId;
        var settings = await db.SpotifySettings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OwnerUserId == ownerId, ct);

        var (clientId, clientSecret) = SpotifyAppCredentialsResolver.Resolve(settings, options.Value);
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return null;

        try
        {
            return await catalog.SearchTrackIdByIsrcAsync(clientId, clientSecret, isrc, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug("Spotify ISRC resolution failed for {Isrc}: {Message}", isrc, ex.Message);
            return null;
        }
    }
}
