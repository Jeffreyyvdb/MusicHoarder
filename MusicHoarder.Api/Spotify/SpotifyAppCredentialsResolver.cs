using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Spotify;

/// <summary>
/// Resolves Spotify app credentials: database row (Settings UI) when complete, otherwise
/// <see cref="SpotifyOptions"/> (e.g. user-secrets / environment).
/// </summary>
public static class SpotifyAppCredentialsResolver
{
    public static (string? ClientId, string? ClientSecret) Resolve(
        SpotifySettings? db,
        SpotifyOptions options)
    {
        if (db?.HasCredentials == true)
            return (db.ClientId!.Trim(), db.ClientSecret!.Trim());

        var id = options.ClientId?.Trim();
        var secret = options.ClientSecret?.Trim();
        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(secret))
            return (id, secret);

        return (null, null);
    }

    public static bool HasAppCredentials(SpotifySettings? db, SpotifyOptions options)
    {
        var (a, b) = Resolve(db, options);
        return !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b);
    }
}
