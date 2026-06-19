using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Spotify;

public class SpotifyApiServiceTests
{
    [Fact]
    public async Task GetLikedSongs_WhenNotConnected_ThrowsSpotifyNotConnectedException()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        await Assert.ThrowsAsync<SpotifyNotConnectedException>(
            () => service.GetLikedSongsAsync());
    }

    [Fact]
    public async Task GetLikedSongs_ReturnsParsedTracks()
    {
        await using var db = CreateDb();
        SeedConnectedSettings(db);

        var spotifyResponse = new
        {
            total = 2,
            items = new[]
            {
                new
                {
                    added_at = "2024-01-15T10:00:00Z",
                    track = new
                    {
                        id = "track1",
                        name = "Test Song",
                        duration_ms = 214000,
                        artists = new[] { new { name = "Artist One" }, new { name = "Artist Two" } },
                        album = new
                        {
                            name = "Test Album",
                            images = new[] { new { url = "https://img.spotify.com/album1.jpg", height = 640, width = 640 } }
                        }
                    }
                },
                new
                {
                    added_at = "2024-02-20T15:30:00Z",
                    track = new
                    {
                        id = "track2",
                        name = "Another Song",
                        duration_ms = 180000,
                        artists = new[] { new { name = "Solo Artist" } },
                        album = new
                        {
                            name = "Another Album",
                            images = new[] { new { url = "https://img.spotify.com/album2.jpg", height = 300, width = 300 } }
                        }
                    }
                }
            }
        };

        var handler = new FakeHttpHandler(HttpStatusCode.OK, JsonSerializer.Serialize(spotifyResponse));
        var service = CreateService(db, handler);

        var result = await service.GetLikedSongsAsync(0, 50);

        Assert.Equal(2, result.Total);
        Assert.Equal(0, result.Offset);
        Assert.Equal(50, result.Limit);
        Assert.Equal(2, result.Items.Count);

        Assert.Equal("track1", result.Items[0].SpotifyId);
        Assert.Equal("Test Song", result.Items[0].Title);
        Assert.Equal("Artist One, Artist Two", result.Items[0].Artist);
        Assert.Equal("Test Album", result.Items[0].Album);
        Assert.Equal("https://img.spotify.com/album1.jpg", result.Items[0].AlbumArt);
        Assert.Equal(214000, result.Items[0].DurationMs);

        Assert.Equal("track2", result.Items[1].SpotifyId);
        Assert.Equal("Solo Artist", result.Items[1].Artist);
    }

    [Fact]
    public async Task GetLikedSongs_ClampsLimitTo50()
    {
        await using var db = CreateDb();
        SeedConnectedSettings(db);

        var spotifyResponse = new { total = 0, items = Array.Empty<object>() };
        var handler = new RequestCapturingHandler(HttpStatusCode.OK, JsonSerializer.Serialize(spotifyResponse));
        var service = CreateService(db, handler);

        await service.GetLikedSongsAsync(0, 100);

        Assert.Contains("limit=50", handler.LastRequestUrl!);
    }

    [Fact]
    public async Task GetLikedSongs_ClampsNegativeOffset()
    {
        await using var db = CreateDb();
        SeedConnectedSettings(db);

        var spotifyResponse = new { total = 0, items = Array.Empty<object>() };
        var handler = new RequestCapturingHandler(HttpStatusCode.OK, JsonSerializer.Serialize(spotifyResponse));
        var service = CreateService(db, handler);

        await service.GetLikedSongsAsync(-5, 20);

        Assert.Contains("offset=0", handler.LastRequestUrl!);
    }

    [Fact]
    public async Task GetPlaylists_ReturnsParsedPlaylists()
    {
        await using var db = CreateDb();
        SeedConnectedSettings(db);

        var spotifyResponse = JsonSerializer.Serialize(new
        {
            items = new object[]
            {
                new
                {
                    id = "playlist1",
                    name = "My Playlist",
                    description = "A great playlist",
                    images = new[] { new { url = "https://img.spotify.com/pl1.jpg" } },
                    tracks = new { total = 42 },
                    owner = new { display_name = "TestUser" }
                },
                new
                {
                    id = "playlist2",
                    name = "Another Playlist",
                    description = "",
                    images = Array.Empty<object>(),
                    tracks = new { total = 10 },
                    owner = new { display_name = "OtherUser" }
                }
            },
            next = (string?)null
        });

        var handler = new FakeHttpHandler(HttpStatusCode.OK, spotifyResponse);
        var service = CreateService(db, handler);

        var result = await service.GetPlaylistsAsync();

        Assert.Equal(2, result.Items.Count);

        Assert.Equal("playlist1", result.Items[0].SpotifyId);
        Assert.Equal("My Playlist", result.Items[0].Name);
        Assert.Equal("A great playlist", result.Items[0].Description);
        Assert.Equal("https://img.spotify.com/pl1.jpg", result.Items[0].ImageUrl);
        Assert.Equal(42, result.Items[0].TrackCount);
        Assert.Equal("TestUser", result.Items[0].OwnerName);

        Assert.Equal("playlist2", result.Items[1].SpotifyId);
        Assert.Null(result.Items[1].ImageUrl);
    }

    [Fact]
    public async Task GetPlaylists_CachesResponse()
    {
        await using var db = CreateDb();
        SeedConnectedSettings(db);

        var spotifyResponse = new
        {
            items = new[] { new { id = "p1", name = "Cached", description = "", images = Array.Empty<object>(), tracks = new { total = 5 }, owner = new { display_name = "Me" } } },
            next = (string?)null
        };

        var callCount = 0;
        var handler = new FakeHttpHandler(HttpStatusCode.OK, JsonSerializer.Serialize(spotifyResponse), () => callCount++);
        var service = CreateService(db, handler);

        var first = await service.GetPlaylistsAsync();
        var second = await service.GetPlaylistsAsync();

        Assert.Equal(1, callCount);
        Assert.Equal(first.Items.Count, second.Items.Count);
    }

    [Fact]
    public async Task GetPlaylists_PaginatesThroughAllPages()
    {
        await using var db = CreateDb();
        SeedConnectedSettings(db);

        var page1 = JsonSerializer.Serialize(new
        {
            items = new[] { new { id = "p1", name = "Playlist 1", description = "", images = Array.Empty<object>(), tracks = new { total = 5 }, owner = new { display_name = "Me" } } },
            next = "https://api.spotify.com/v1/me/playlists?offset=50&limit=50"
        });

        var page2 = JsonSerializer.Serialize(new
        {
            items = new[] { new { id = "p2", name = "Playlist 2", description = "", images = Array.Empty<object>(), tracks = new { total = 10 }, owner = new { display_name = "Me" } } },
            next = (string?)null
        });

        var handler = new SequentialResponseHandler(new[] { (HttpStatusCode.OK, page1), (HttpStatusCode.OK, page2) });
        var service = CreateService(db, handler);

        var result = await service.GetPlaylistsAsync();

        Assert.Equal(2, result.Items.Count);
        Assert.Equal("p1", result.Items[0].SpotifyId);
        Assert.Equal("p2", result.Items[1].SpotifyId);
    }

    [Fact]
    public async Task GetPlaylists_WhenNotConnected_ThrowsSpotifyNotConnectedException()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        await Assert.ThrowsAsync<SpotifyNotConnectedException>(
            () => service.GetPlaylistsAsync());
    }

    [Fact]
    public async Task GetPlaylistTracks_ReturnsParsedTracks()
    {
        await using var db = CreateDb();
        SeedConnectedSettings(db);

        var spotifyResponse = new
        {
            total = 1,
            items = new[]
            {
                new
                {
                    added_at = "2024-03-01T12:00:00Z",
                    track = new
                    {
                        id = "ptrack1",
                        name = "Playlist Track",
                        duration_ms = 200000,
                        artists = new[] { new { name = "Playlist Artist" } },
                        album = new
                        {
                            name = "Playlist Album",
                            images = new[] { new { url = "https://img.spotify.com/pa.jpg" } }
                        }
                    }
                }
            }
        };

        var handler = new FakeHttpHandler(HttpStatusCode.OK, JsonSerializer.Serialize(spotifyResponse));
        var service = CreateService(db, handler);

        var result = await service.GetPlaylistTracksAsync("playlist123", 0, 50);

        Assert.Equal(1, result.Total);
        Assert.Equal(0, result.Offset);
        Assert.Equal(50, result.Limit);
        Assert.Single(result.Items);
        Assert.Equal("ptrack1", result.Items[0].SpotifyId);
        Assert.Equal("Playlist Track", result.Items[0].Title);
        Assert.Equal("Playlist Artist", result.Items[0].Artist);
    }

    [Fact]
    public async Task GetPlaylistTracks_WhenNotConnected_ThrowsSpotifyNotConnectedException()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        await Assert.ThrowsAsync<SpotifyNotConnectedException>(
            () => service.GetPlaylistTracksAsync("playlist123"));
    }

    [Fact]
    public async Task GetPlaylistTracks_SkipsNullTracks()
    {
        await using var db = CreateDb();
        SeedConnectedSettings(db);

        var json = """
        {
            "total": 2,
            "items": [
                {
                    "added_at": "2024-01-01T00:00:00Z",
                    "track": null
                },
                {
                    "added_at": "2024-01-02T00:00:00Z",
                    "track": {
                        "id": "valid",
                        "name": "Valid Track",
                        "duration_ms": 100000,
                        "artists": [{"name": "Artist"}],
                        "album": {"name": "Album", "images": []}
                    }
                }
            ]
        }
        """;

        var handler = new FakeHttpHandler(HttpStatusCode.OK, json);
        var service = CreateService(db, handler);

        var result = await service.GetPlaylistTracksAsync("pl1");

        Assert.Single(result.Items);
        Assert.Equal("valid", result.Items[0].SpotifyId);
    }

    [Fact]
    public async Task SendAuthenticatedRequest_Handles429WithRetry()
    {
        await using var db = CreateDb();
        SeedConnectedSettings(db);

        var successResponse = JsonSerializer.Serialize(new { total = 0, items = Array.Empty<object>() });
        var responses = new[]
        {
            (HttpStatusCode.TooManyRequests, "{}", TimeSpan.FromMilliseconds(100)),
            (HttpStatusCode.OK, successResponse, TimeSpan.Zero),
        };

        var handler = new RateLimitHandler(responses);
        var service = CreateService(db, handler);

        var result = await service.GetLikedSongsAsync();
        Assert.Equal(0, result.Total);
    }

    [Fact]
    public async Task SendAuthenticatedRequest_RetriesTransient5xx()
    {
        await using var db = CreateDb();
        SeedConnectedSettings(db);

        var success = JsonSerializer.Serialize(new { total = 0, items = Array.Empty<object>() });
        // A single 502 (Spotify gateway hiccup) must not abort the request — it retries and succeeds.
        var handler = new SequentialResponseHandler(new[]
        {
            (HttpStatusCode.BadGateway, "{}"),
            (HttpStatusCode.OK, success),
        });
        var service = CreateService(db, handler);

        var result = await service.GetLikedSongsAsync();
        Assert.Equal(0, result.Total);
    }

    [Fact]
    public async Task SendAuthenticatedRequest_Handles401WithTokenRefresh()
    {
        await using var db = CreateDb();
        SeedConnectedSettings(db);

        var successResponse = JsonSerializer.Serialize(new { total = 0, items = Array.Empty<object>() });

        var tokenResponse = JsonSerializer.Serialize(new
        {
            access_token = "refreshed-token",
            token_type = "Bearer",
            expires_in = 3600,
        });

        var oauthHandler = new FakeHttpHandler(HttpStatusCode.OK, tokenResponse);
        var apiResponses = new[]
        {
            (HttpStatusCode.Unauthorized, "{}"),
            (HttpStatusCode.OK, successResponse),
        };
        var apiHandler = new SequentialResponseHandler(apiResponses);

        var service = CreateService(db, apiHandler, oauthHandler);

        var result = await service.GetLikedSongsAsync();
        Assert.Equal(0, result.Total);
    }

    private static MusicHoarderDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private static void SeedConnectedSettings(MusicHoarderDbContext db)
    {
        db.SpotifySettings.Add(new SpotifySettings
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            ClientId = "test-client-id",
            ClientSecret = "test-client-secret",
            AccessToken = "test-access-token",
            RefreshToken = "test-refresh-token",
            TokenExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            ConnectedAtUtc = DateTime.UtcNow.AddHours(-1),
        });
        db.SaveChanges();
    }

    private static SpotifyApiService CreateService(
        MusicHoarderDbContext db,
        HttpMessageHandler? apiHandler = null,
        HttpMessageHandler? oauthHandler = null)
    {
        var scopeFactory = new SpotifyScopeFactory(db);
        var oauthHttpClient = new HttpClient(oauthHandler ?? new FakeHttpHandler(HttpStatusCode.OK, "{}"));
        var oauthLogger = NullLogger<SpotifyOAuthService>.Instance;
        var oauthOpts = Microsoft.Extensions.Options.Options.Create(new SpotifyOptions());
        var ownerLookup = new TestOwnerLookupService();
        var oauthService = new SpotifyOAuthService(scopeFactory, oauthHttpClient, ownerLookup, oauthOpts, oauthLogger);

        var apiHttpClient = new HttpClient(apiHandler ?? new FakeHttpHandler(HttpStatusCode.OK, "{}"));
        var cache = new MemoryCache(new MemoryCacheOptions());
        var logger = NullLogger<SpotifyApiService>.Instance;
        return new SpotifyApiService(scopeFactory, oauthService, apiHttpClient, cache, ownerLookup, logger);
    }

    private sealed class FakeHttpHandler(
        HttpStatusCode statusCode,
        string responseBody,
        Action? onSend = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onSend?.Invoke();
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RequestCapturingHandler(
        HttpStatusCode statusCode,
        string responseBody) : HttpMessageHandler
    {
        public string? LastRequestUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUrl = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class SequentialResponseHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses;

        public SequentialResponseHandler(IEnumerable<(HttpStatusCode, string)> responses)
        {
            _responses = new Queue<(HttpStatusCode, string)>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = _responses.Count > 0
                ? _responses.Dequeue()
                : (HttpStatusCode.OK, "{}");

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RateLimitHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body, TimeSpan RetryAfter)> _responses;

        public RateLimitHandler(IEnumerable<(HttpStatusCode, string, TimeSpan)> responses)
        {
            _responses = new Queue<(HttpStatusCode, string, TimeSpan)>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body, retryAfter) = _responses.Count > 0
                ? _responses.Dequeue()
                : (HttpStatusCode.OK, "{}", TimeSpan.Zero);

            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };

            if (status == HttpStatusCode.TooManyRequests && retryAfter > TimeSpan.Zero)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class SpotifyScopeFactory(MusicHoarderDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new SimpleScope(new SimpleServiceProvider(db));
    }

    private sealed class SimpleScope(IServiceProvider provider) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = provider;
        public void Dispose() { }
    }

    private sealed class SimpleServiceProvider(MusicHoarderDbContext db) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(MusicHoarderDbContext)) return db;
            return null;
        }
    }
}
