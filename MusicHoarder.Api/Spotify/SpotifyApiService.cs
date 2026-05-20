using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Spotify;

public class SpotifyApiService(
    IServiceScopeFactory scopeFactory,
    ISpotifyOAuthService oauthService,
    HttpClient httpClient,
    IMemoryCache cache,
    IOwnerLookupService ownerLookup,
    ILogger<SpotifyApiService> logger) : ISpotifyApiService
{
    private const string BaseUrl = "https://api.spotify.com/v1";
    private const string PlaylistsCacheKey = "spotify_playlists";
    private static readonly TimeSpan PlaylistsCacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RateLimitDefaultDelay = TimeSpan.FromSeconds(5);
    private const int MaxRetries = 3;

    public async Task<SpotifyLikedSongsResponse> GetLikedSongsAsync(int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 50);
        offset = Math.Max(offset, 0);

        var json = await SendAuthenticatedRequestAsync($"{BaseUrl}/me/tracks?offset={offset}&limit={limit}", ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var total = root.GetProperty("total").GetInt32();
        var items = ParseSavedTracks(root.GetProperty("items"));

        return new SpotifyLikedSongsResponse(total, offset, limit, items);
    }

    public async Task<SpotifyPlaylistsResponse> GetPlaylistsAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(PlaylistsCacheKey, out SpotifyPlaylistsResponse? cached) && cached is not null)
            return cached;

        var allPlaylists = new List<SpotifyPlaylistItem>();
        string? nextUrl = $"{BaseUrl}/me/playlists?limit=50";

        while (nextUrl is not null)
        {
            var json = await SendAuthenticatedRequestAsync(nextUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            foreach (var item in root.GetProperty("items").EnumerateArray())
            {
                allPlaylists.Add(ParsePlaylist(item));
            }

            nextUrl = root.GetProperty("next").ValueKind == JsonValueKind.String
                ? root.GetProperty("next").GetString()
                : null;
        }

        var response = new SpotifyPlaylistsResponse(allPlaylists);
        cache.Set(PlaylistsCacheKey, response, PlaylistsCacheTtl);
        return response;
    }

    public async Task<SpotifyPlaylistTracksResponse> GetPlaylistTracksAsync(string playlistId, int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 50);
        offset = Math.Max(offset, 0);

        var fields = "total,items(added_at,track(id,name,duration_ms,artists(name),album(name,images)))";
        var url = $"{BaseUrl}/playlists/{Uri.EscapeDataString(playlistId)}/tracks?offset={offset}&limit={limit}&fields={Uri.EscapeDataString(fields)}";
        var json = await SendAuthenticatedRequestAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var total = root.GetProperty("total").GetInt32();
        var items = ParsePlaylistTracks(root.GetProperty("items"));

        return new SpotifyPlaylistTracksResponse(total, offset, limit, items);
    }

    private async Task<string> SendAuthenticatedRequestAsync(string url, CancellationToken ct)
    {
        var accessToken = await GetAccessTokenAsync(ct);

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await httpClient.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? RateLimitDefaultDelay;
                logger.LogWarning("Spotify rate limited. Retrying after {Delay}s (attempt {Attempt}/{Max})",
                    retryAfter.TotalSeconds, attempt + 1, MaxRetries);

                if (attempt < MaxRetries)
                {
                    await Task.Delay(retryAfter, ct);
                    continue;
                }
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (attempt == 0)
                {
                    logger.LogInformation("Spotify returned 401, attempting token refresh");
                    var refreshResult = await oauthService.RefreshAccessTokenAsync(ct);
                    if (refreshResult.Success)
                    {
                        accessToken = await GetAccessTokenAsync(ct);
                        continue;
                    }
                }

                throw new SpotifyNotConnectedException();
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }

        throw new SpotifyRateLimitException("Spotify API rate limit exceeded after maximum retries.");
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await oauthService.EnsureValidTokenAsync(ct);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var ownerId = ownerLookup.OwnerUserId;
        var settings = await db.SpotifySettings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OwnerUserId == ownerId, ct);

        if (settings is null || !settings.IsConnected || string.IsNullOrWhiteSpace(settings.AccessToken))
            throw new SpotifyNotConnectedException();

        return settings.AccessToken;
    }

    private static List<SpotifyTrackItem> ParseSavedTracks(JsonElement items)
    {
        var result = new List<SpotifyTrackItem>();
        foreach (var item in items.EnumerateArray())
        {
            var addedAt = item.GetProperty("added_at").GetDateTime();
            var track = item.GetProperty("track");
            var parsed = ParseTrackObject(track, addedAt);
            if (parsed is not null)
                result.Add(parsed);
        }
        return result;
    }

    private static List<SpotifyTrackItem> ParsePlaylistTracks(JsonElement items)
    {
        var result = new List<SpotifyTrackItem>();
        foreach (var item in items.EnumerateArray())
        {
            var addedAt = item.TryGetProperty("added_at", out var addedAtProp) && addedAtProp.ValueKind == JsonValueKind.String
                ? addedAtProp.GetDateTime()
                : DateTime.MinValue;

            if (!item.TryGetProperty("track", out var track) || track.ValueKind == JsonValueKind.Null)
                continue;

            var parsed = ParseTrackObject(track, addedAt);
            if (parsed is not null)
                result.Add(parsed);
        }
        return result;
    }

    private static SpotifyTrackItem? ParseTrackObject(JsonElement track, DateTime addedAt)
    {
        if (track.ValueKind == JsonValueKind.Null)
            return null;

        var id = track.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString() ?? ""
            : "";

        if (string.IsNullOrEmpty(id))
            return null;

        var name = track.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
            ? nameProp.GetString() ?? ""
            : "";

        var durationMs = track.TryGetProperty("duration_ms", out var durProp)
            ? durProp.GetInt32()
            : 0;

        var artist = "";
        if (track.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
        {
            var artistNames = new List<string>();
            foreach (var a in artists.EnumerateArray())
            {
                if (a.TryGetProperty("name", out var an) && an.ValueKind == JsonValueKind.String)
                    artistNames.Add(an.GetString()!);
            }
            artist = string.Join(", ", artistNames);
        }

        var album = "";
        string? albumArt = null;
        if (track.TryGetProperty("album", out var albumObj) && albumObj.ValueKind == JsonValueKind.Object)
        {
            if (albumObj.TryGetProperty("name", out var albumName) && albumName.ValueKind == JsonValueKind.String)
                album = albumName.GetString() ?? "";

            if (albumObj.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0)
            {
                var firstImage = images[0];
                if (firstImage.TryGetProperty("url", out var imgUrl) && imgUrl.ValueKind == JsonValueKind.String)
                    albumArt = imgUrl.GetString();
            }
        }

        return new SpotifyTrackItem(id, name, artist, album, albumArt, durationMs, addedAt);
    }

    private static SpotifyPlaylistItem ParsePlaylist(JsonElement item)
    {
        var id = item.GetProperty("id").GetString() ?? "";
        var name = item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
            ? nameProp.GetString() ?? ""
            : "";

        string? description = null;
        if (item.TryGetProperty("description", out var descProp) && descProp.ValueKind == JsonValueKind.String)
            description = descProp.GetString();

        string? imageUrl = null;
        if (item.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0)
        {
            if (images[0].TryGetProperty("url", out var imgUrl) && imgUrl.ValueKind == JsonValueKind.String)
                imageUrl = imgUrl.GetString();
        }

        var trackCount = 0;
        if (item.TryGetProperty("tracks", out var tracks) && tracks.TryGetProperty("total", out var totalProp))
            trackCount = totalProp.GetInt32();

        string? ownerName = null;
        if (item.TryGetProperty("owner", out var owner) && owner.TryGetProperty("display_name", out var ownerNameProp))
            ownerName = ownerNameProp.GetString();

        return new SpotifyPlaylistItem(id, name, description, imageUrl, trackCount, ownerName);
    }
}

public class SpotifyNotConnectedException() : Exception("Spotify is not connected.");

public class SpotifyRateLimitException(string message) : Exception(message);
