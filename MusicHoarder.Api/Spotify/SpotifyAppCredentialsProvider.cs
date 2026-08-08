using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Spotify;

/// <summary>
/// Injectable form of <see cref="SpotifyAppCredentialsResolver"/> for services that aren't scoped to
/// a request: resolves the owner's Settings-UI credentials (DB) with the configured
/// <see cref="SpotifyOptions"/> as fallback. Either component may be null when unconfigured.
/// </summary>
public interface ISpotifyAppCredentialsProvider
{
    Task<(string? ClientId, string? ClientSecret)> ResolveAsync(CancellationToken ct = default);
}

public sealed class SpotifyAppCredentialsProvider(
    IServiceScopeFactory scopeFactory,
    IOwnerLookupService ownerLookup,
    IOptions<SpotifyOptions> spotifyOptions) : ISpotifyAppCredentialsProvider
{
    public async Task<(string? ClientId, string? ClientSecret)> ResolveAsync(CancellationToken ct = default)
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
