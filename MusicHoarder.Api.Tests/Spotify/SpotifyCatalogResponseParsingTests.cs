using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Tests.Spotify;

/// <summary>
/// Pins how each catalog lookup reads the Web API's JSON body, observed through the public
/// service surface over a scripted transport: which fields map where, what a missing or
/// wrong-typed field defaults to, which rows are skipped, and the shapes that still throw.
/// The transport itself (token, 429, 401) is pinned in <see cref="SpotifyCatalogSearchTransportTests"/>.
/// </summary>
public class SpotifyCatalogResponseParsingTests
{
    // ── track search ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchTracks_MapsEveryField()
    {
        var tracks = await SearchTracks(
            """
            {"tracks":{"items":[{
              "id": "t1", "name": "Song", "duration_ms": 200000,
              "track_number": 4, "disc_number": 2, "explicit": true,
              "artists": [{"name": "One"}, {"name": 7}, {"name": " Two "}],
              "album": {
                "id": "al1", "name": "Album", "release_date": "2019-03-15",
                "album_type": "COMPILATION", "total_tracks": 12,
                "images": [{"url": 5}, {"url": "https://img/640"}, {"url": "https://img/300"}]
              },
              "external_ids": {"isrc": "USRC17607839"}
            }]}}
            """);

        var t = Assert.Single(tracks);
        Assert.Equal(new SpotifyCatalogTrack(
            "t1", "Song", "One,  Two ", "Album", 2019, 4, 200000, "USRC17607839",
            Artists: "One; Two", DiscNumber: 2, AlbumType: "compilation", TotalTracks: 12,
            AlbumId: "al1", AlbumArtUrl: "https://img/640", Explicit: true), t);
    }

    [Fact]
    public async Task SearchTracks_MissingFieldsDefault()
    {
        var tracks = await SearchTracks("""{"tracks":{"items":[{"id":"t1"}]}}""");

        Assert.Equal(new SpotifyCatalogTrack(
            "t1", "", "", "", null, null, 0, null,
            Artists: null, DiscNumber: null, AlbumType: null, TotalTracks: null,
            AlbumId: null, AlbumArtUrl: null, Explicit: false), Assert.Single(tracks));
    }

    [Fact]
    public async Task SearchTracks_WrongTypedFieldsDefault()
    {
        var tracks = await SearchTracks(
            """
            {"tracks":{"items":[{
              "id": "t1", "name": 3, "track_number": "4", "disc_number": "1", "explicit": "true",
              "artists": [], "album": {"name": 1, "release_date": 2019, "album_type": 2,
              "total_tracks": "12", "id": 9, "images": {}},
              "external_ids": {"isrc": 1}
            }]}}
            """);

        var t = Assert.Single(tracks);
        Assert.Equal(new SpotifyCatalogTrack(
            "t1", "", "", "", null, null, 0, null,
            Artists: null, DiscNumber: null, AlbumType: null, TotalTracks: null,
            AlbumId: null, AlbumArtUrl: null, Explicit: false), t);
    }

    [Fact]
    public async Task SearchTracks_AlbumThatIsNotAnObject_IsIgnored()
    {
        var tracks = await SearchTracks("""{"tracks":{"items":[{"id":"t1","album":"Album","external_ids":[]}]}}""");

        var t = Assert.Single(tracks);
        Assert.Equal("", t.AlbumName);
        Assert.Null(t.AlbumId);
        Assert.Null(t.Isrc);
    }

    [Fact]
    public async Task SearchTracks_SkipsRowsWithoutAUsableId_AndNonObjectRows()
    {
        var tracks = await SearchTracks(
            """{"tracks":{"items":[{"name":"no id"},{"id":""},{"id":5},"text",null,{"id":"keep"}]}}""");

        Assert.Equal(["keep"], tracks.Select(t => t.Id));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"tracks":{}}""")]
    [InlineData("""{"tracks":{"items":{}}}""")]
    [InlineData("""not json""")]
    public async Task SearchTracks_BodyWithoutATrackList_IsEmpty(string body)
    {
        Assert.Empty(await SearchTracks(body));
    }

    [Fact]
    public async Task SearchTracks_RequestsTheConfiguredLimitAndMarket()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken();
        handler.EnqueueJson("""{"tracks":{"items":[]}}""");

        await CreateService(handler, market: " NL ").SearchTracksAsync("cid", "sec", "a b&c", CancellationToken.None);

        Assert.Equal("https://api.spotify.com/v1/search?q=a b%26c&type=track&limit=10&market=NL", handler.Requests[1].Url);
    }

    [Fact]
    public async Task SearchTracks_NonStringDuration_ThrowsToday()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SearchTracks("""{"tracks":{"items":[{"id":"t1","duration_ms":"200"}]}}"""));
    }

    [Fact]
    public async Task SearchTracks_NonObjectArtist_ThrowsToday()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SearchTracks("""{"tracks":{"items":[{"id":"t1","artists":["One"]}]}}"""));
    }

    // ── single track ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTrack_ReadsTheSameShapeAsASearchHit()
    {
        const string item = """{"id":"t1","name":"Song","duration_ms":1000,"artists":[{"name":"A"}],"album":{"name":"Al","release_date":"2020","id":"al1"}}""";

        var fromSearch = Assert.Single(await SearchTracks("""{"tracks":{"items":[""" + item + "]}}"));
        var (track, handler) = await Lookup(item, s => s.GetTrackAsync("cid", "sec", "t 1", CancellationToken.None));

        Assert.Equal(fromSearch, track);
        Assert.Equal("https://api.spotify.com/v1/tracks/t 1", handler.Requests[1].Url);
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{"name":"no id"}""")]
    [InlineData("""not json""")]
    public async Task GetTrack_BodyThatIsNotATrack_IsNull(string body)
    {
        var (track, _) = await Lookup(body, s => s.GetTrackAsync("cid", "sec", "t1", CancellationToken.None));
        Assert.Null(track);
    }

    [Theory]
    [InlineData("""{"album":{"id":"al1"}}""", "al1")]
    [InlineData("""{"album":{"id":1}}""", null)]
    [InlineData("""{"album":"al1"}""", null)]
    [InlineData("""{"id":"t1"}""", null)]
    [InlineData("""not json""", null)]
    public async Task GetTrackAlbumId_ReadsTheAlbumObjectsId(string body, string? expected)
    {
        var (albumId, handler) = await Lookup(body, s => s.GetTrackAlbumIdAsync("cid", "sec", "t1", CancellationToken.None));

        Assert.Equal(expected, albumId);
        Assert.Equal("https://api.spotify.com/v1/tracks/t1", handler.Requests[1].Url);
    }

    // ── album search ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAlbumId_ReturnsTheFirstHitWithAStringId()
    {
        var (id, handler) = await Lookup(
            """{"albums":{"items":[{"name":"no id"},{"id":5},{"id":"second"},{"id":"third"}]}}""",
            s => s.SearchAlbumIdAsync("cid", "sec", "Daft Punk", "Discovery", CancellationToken.None));

        Assert.Equal("second", id);
        Assert.Equal("https://api.spotify.com/v1/search?q=album%3ADiscovery artist%3ADaft Punk&type=album&limit=5", handler.Requests[1].Url);
    }

    [Theory]
    [InlineData("""{"albums":{"items":[]}}""")]
    [InlineData("""{"albums":{"items":{}}}""")]
    [InlineData("""{}""")]
    [InlineData("""not json""")]
    public async Task SearchAlbumId_NoUsableHit_IsNull(string body)
    {
        var (id, _) = await Lookup(body, s => s.SearchAlbumIdAsync("cid", "sec", "A", "B", CancellationToken.None));
        Assert.Null(id);
    }

    [Fact]
    public async Task SearchAlbumId_ArrayWhereAnObjectIsExpected_ThrowsToday()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Lookup("""[]""", s => s.SearchAlbumIdAsync("cid", "sec", "A", "B", CancellationToken.None)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Lookup("""{"albums":{"items":["x"]}}""", s => s.SearchAlbumIdAsync("cid", "sec", "A", "B", CancellationToken.None)));
    }

    [Fact]
    public async Task SearchAlbumCandidates_ReadsIdNameAndTheFirstNamedArtist()
    {
        var (candidates, handler) = await Lookup(
            """
            {"albums":{"items":[
              {"id":"a1","name":"Discovery","artists":["x",{"name":3},{"name":"Daft Punk"},{"name":"Other"}]},
              {"id":"a2","name":7},
              {"id":5,"name":"numeric id"},
              {"name":"no id"},
              "text"
            ]}}
            """,
            s => s.SearchAlbumCandidatesAsync("cid", "sec", "", "Discovery", CancellationToken.None));

        Assert.Equal(
            [new SpotifyAlbumCandidate("a1", "Discovery", "Daft Punk"), new SpotifyAlbumCandidate("a2", null, null)],
            candidates);
        Assert.Equal("https://api.spotify.com/v1/search?q=album%3ADiscovery artist%3A&type=album&limit=5", handler.Requests[1].Url);
    }

    [Theory]
    [InlineData("""{"albums":{"items":{}}}""")]
    [InlineData("""{"albums":{}}""")]
    [InlineData("""{}""")]
    [InlineData("""not json""")]
    public async Task SearchAlbumCandidates_NoList_IsEmpty(string body)
    {
        var (candidates, _) = await Lookup(body, s => s.SearchAlbumCandidatesAsync("cid", "sec", "A", "B", CancellationToken.None));
        Assert.Empty(candidates);
    }

    [Fact]
    public async Task AlbumLookups_BlankAlbum_SkipHttp()
    {
        var handler = new ScriptedHttpHandler();
        var service = CreateService(handler);

        Assert.Null(await service.SearchAlbumIdAsync("cid", "sec", "A", " ", CancellationToken.None));
        Assert.Empty(await service.SearchAlbumCandidatesAsync("cid", "sec", "A", "", CancellationToken.None));
        Assert.Null(await service.GetAlbumAsync("cid", "sec", " ", CancellationToken.None));
        Assert.Equal(0, handler.SendCount);
    }

    // ── artist search ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchArtistCandidates_KeepsNamelessRows_AndTakesTheFirstImageUrl()
    {
        var (candidates, handler) = await Lookup(
            """
            {"artists":{"items":[
              {"name":"Daft Punk","images":["x",{"url":1},{"url":"https://img/big"},{"url":"https://img/small"}]},
              {"name":5,"images":[]},
              {"name":"No images"},
              7
            ]}}
            """,
            s => s.SearchArtistCandidatesAsync("cid", "sec", "Daft Punk", CancellationToken.None));

        Assert.Equal(
            [
                new SpotifyArtistCandidate("Daft Punk", "https://img/big"),
                new SpotifyArtistCandidate(null, null),
                new SpotifyArtistCandidate("No images", null)
            ],
            candidates);
        Assert.Equal("https://api.spotify.com/v1/search?q=artist%3ADaft Punk&type=artist&limit=5", handler.Requests[1].Url);
    }

    [Theory]
    [InlineData("""{"artists":{"items":{}}}""")]
    [InlineData("""{}""")]
    [InlineData("""not json""")]
    public async Task SearchArtistCandidates_NoList_IsEmpty(string body)
    {
        var (candidates, _) = await Lookup(body, s => s.SearchArtistCandidatesAsync("cid", "sec", "A", CancellationToken.None));
        Assert.Empty(candidates);
    }

    // ── ISRC search ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchTrackIdByIsrc_ReturnsTheFirstStringId()
    {
        var (id, handler) = await Lookup(
            """{"tracks":{"items":[{"id":1},{"id":"t2"},{"id":"t3"}]}}""",
            s => s.SearchTrackIdByIsrcAsync("cid", "sec", "USRC17607839", CancellationToken.None));

        Assert.Equal("t2", id);
        Assert.Equal("https://api.spotify.com/v1/search?q=isrc%3AUSRC17607839&type=track&limit=1", handler.Requests[1].Url);
    }

    [Theory]
    [InlineData("""{"tracks":{"items":[]}}""")]
    [InlineData("""{"tracks":{"items":{}}}""")]
    [InlineData("""{}""")]
    [InlineData("""not json""")]
    public async Task SearchTrackIdByIsrc_NoUsableHit_IsNull(string body)
    {
        var (id, _) = await Lookup(body, s => s.SearchTrackIdByIsrcAsync("cid", "sec", "X", CancellationToken.None));
        Assert.Null(id);
    }

    // ── album detail ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAlbum_MapsEveryField()
    {
        var (album, handler) = await Lookup(
            """
            {
              "id": "alb-1", "name": "Discovery", "release_date": "2001-03-12T00:00:00Z",
              "label": "  Virgin  ", "external_ids": {"upc": " 724384960650 "},
              "copyrights": [
                {"type": "P", "text": "℗ 2001 Virgin"},
                {"type": "c", "text": "  © 2001 Daft Life  "},
                {"type": "C", "text": "© second"}
              ],
              "artists": [{"name": "Daft Punk"}, {"name": 1}, {"name": "Romanthony"}],
              "images": [{"url": "https://img/640"}, {"url": "https://img/300"}],
              "tracks": {"items": [
                {"id": "t1", "name": "One More Time", "disc_number": 1, "track_number": 1, "duration_ms": 320000},
                "skip",
                {"id": 2, "name": 3, "disc_number": "2", "track_number": "2", "duration_ms": "1"}
              ]}
            }
            """,
            s => s.GetAlbumAsync("cid", "sec", "alb 1", CancellationToken.None));

        Assert.NotNull(album);
        Assert.Equal("alb-1", album!.Id);
        Assert.Equal("Discovery", album.Name);
        Assert.Equal("Daft Punk, Romanthony", album.Artist);
        Assert.Equal(2001, album.Year);
        Assert.Equal("https://img/640", album.ImageUrl);
        Assert.Equal("2001-03-12", album.ReleaseDate);
        Assert.Equal("Virgin", album.Label);
        Assert.Equal("724384960650", album.Upc);
        Assert.Equal("© 2001 Daft Life", album.Copyright);
        Assert.Equal(
            [new SpotifyAlbumTrackItem(1, 1, "One More Time", 320000, "t1"), new SpotifyAlbumTrackItem(1, 0, null, 0, null)],
            album.Tracks);
        Assert.Equal("https://api.spotify.com/v1/albums/alb 1", handler.Requests[1].Url);
    }

    [Fact]
    public async Task GetAlbum_WithoutACopyrightLine_FallsBackToTheFirstNonBlankLine()
    {
        var (album, _) = await Lookup(
            """{"id":"a","copyrights":[{"type":"P","text":"  "},"x",{"text":5},{"type":"P","text":" ℗ first "},{"type":"P","text":"℗ second"}]}""",
            s => s.GetAlbumAsync("cid", "sec", "a", CancellationToken.None));

        Assert.Equal("℗ first", album!.Copyright);
    }

    [Fact]
    public async Task GetAlbum_MissingAndBlankFieldsAreNull()
    {
        var (album, _) = await Lookup(
            """{"id":"a","label":"  ","external_ids":{"upc":" "},"release_date":"soon","artists":[{"name":"  "}],"copyrights":[]}""",
            s => s.GetAlbumAsync("cid", "sec", "a", CancellationToken.None));

        Assert.Equal(new SpotifyAlbumDetail("a", null, null, null, null, album!.Tracks), album);
        Assert.Empty(album.Tracks);
    }

    [Fact]
    public async Task GetAlbum_EmptyArtistsArray_IsNullArtist()
    {
        var (album, _) = await Lookup("""{"id":"a","artists":[]}""", s => s.GetAlbumAsync("cid", "sec", "a", CancellationToken.None));
        Assert.Null(album!.Artist);
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{"name":"no id"}""")]
    [InlineData("""{"id":""}""")]
    [InlineData("""{"id":5}""")]
    [InlineData("""not json""")]
    public async Task GetAlbum_BodyThatIsNotAnAlbum_IsNull(string body)
    {
        var (album, _) = await Lookup(body, s => s.GetAlbumAsync("cid", "sec", "a", CancellationToken.None));
        Assert.Null(album);
    }

    [Fact]
    public async Task GetAlbum_TracksArrayWhereAnObjectIsExpected_ThrowsToday()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Lookup("""{"id":"a","tracks":[]}""", s => s.GetAlbumAsync("cid", "sec", "a", CancellationToken.None)));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<SpotifyCatalogTrack>> SearchTracks(string body)
    {
        var (tracks, _) = await Lookup(body, s => s.SearchTracksAsync("cid", "sec", "q", CancellationToken.None));
        return tracks;
    }

    private static async Task<(T Result, ScriptedHttpHandler Handler)> Lookup<T>(
        string body, Func<SpotifyCatalogSearchService, Task<T>> call)
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueToken();
        handler.EnqueueJson(body);
        var result = await call(CreateService(handler));
        return (result, handler);
    }

    private static SpotifyCatalogSearchService CreateService(ScriptedHttpHandler handler, string market = "")
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var opts = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/s",
            DestinationDirectory = "/d",
            SpotifyApiRequestsPerSecond = 20,
            SpotifyApiSearchLimit = 10,
            SpotifyApiSearchCacheMinutes = 60,
            SpotifyApiMarket = market
        });
        var api = new SpotifyClientCredentialsClient(httpClient, cache, opts, NullLogger<SpotifyClientCredentialsClient>.Instance);
        return new SpotifyCatalogSearchService(api, cache, opts);
    }
}
