using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Deezer;

/// <summary>
/// Pins how <see cref="DeezerCatalogService"/> reads each Deezer response shape, end to end through
/// its public methods over a stubbed <see cref="HttpClient"/>: the id number/string rule, the field
/// defaults, the cover and picture fallback orders, the search-vs-detail split, playlist paging off
/// the <c>next</c> link, and the quota-error body.
/// </summary>
public class DeezerCatalogResponseParsingTests
{
    // --- tracks ---

    [Fact]
    public async Task SearchTracks_ReadsSearchHits_AndIgnoresDetailOnlyFields()
    {
        var stub = new RoutingStub(_ => Json("""
            { "data": [
                { "id": 10, "title": "One", "duration": 200,
                  "artist": { "name": "Alice" }, "album": { "title": "First" },
                  "isrc": "USAAA0000001", "release_date": "2001-01-01", "track_position": 3,
                  "contributors": [ { "name": "Alice" }, { "name": "Bob" } ] },
                { "id": "11", "title": "Two" },
                { "title": "No id" },
                { "id": true, "title": "Bool id" },
                "not an object",
                { "id": 12, "title": 5, "duration": "200", "artist": "Carol", "album": { "title": 7 } }
            ] }
            """));

        var tracks = await CreateService(stub).SearchTracksAsync("anything");

        Assert.Equal(
            [
                new DeezerCatalogTrack("10", "One", "Alice", "First", null, null, 200_000, null, Artists: null),
                new DeezerCatalogTrack("11", "Two", "", "", null, null, 0, null, Artists: null),
                new DeezerCatalogTrack("12", "", "", "", null, null, 0, null, Artists: null),
            ],
            tracks);
    }

    [Fact]
    public async Task SearchTracks_PayloadWithoutDataArray_OrMalformed_IsEmpty()
    {
        Assert.Empty(await CreateService(new RoutingStub(_ => Json("""{ "data": {} }"""))).SearchTracksAsync("a"));
        Assert.Empty(await CreateService(new RoutingStub(_ => Json("""{ "total": 0 }"""))).SearchTracksAsync("b"));
        Assert.Empty(await CreateService(new RoutingStub(_ => Json("{ not json"))).SearchTracksAsync("c"));
    }

    [Fact]
    public async Task LookupByIsrc_ReadsFullDetail()
    {
        var stub = new RoutingStub(_ => Json("""
            { "id": "3135556", "title": "Harder", "duration": 224,
              "isrc": "GBDUW0000059", "release_date": "2001-03-07", "track_position": 4,
              "artist": { "name": "Daft Punk" }, "album": { "title": "Discovery" },
              "contributors": [ { "name": " Daft Punk " }, { "name": "   " }, { "name": "DAFT PUNK" },
                                { "nickname": "x" }, "loose", { "name": "Romanthony" } ] }
            """));

        var track = await CreateService(stub).LookupByIsrcAsync("GBDUW0000059");

        Assert.Equal(
            new DeezerCatalogTrack("3135556", "Harder", "Daft Punk", "Discovery", 2001, 4, 224_000, "GBDUW0000059",
                Artists: "Daft Punk; Romanthony"),
            track);
        Assert.Contains("/track/isrc:GBDUW0000059", stub.Requests.Single());
    }

    [Fact]
    public async Task LookupById_DetailWithWrongTypedOptionalFields_LeavesThemNull()
    {
        var stub = new RoutingStub(_ => Json("""
            { "id": 5, "title": "T", "isrc": 123, "release_date": 2001, "track_position": "4", "contributors": {} }
            """));

        var track = await CreateService(stub).LookupByIdAsync("5");

        Assert.Equal(new DeezerCatalogTrack("5", "T", "", "", null, null, 0, null, Artists: null), track);
    }

    [Theory]
    [InlineData("""[ { "id": 1 } ]""")]
    [InlineData("""{ "title": "no id" }""")]
    [InlineData("""{ "id": "", "title": "empty id" }""")]
    [InlineData("{ broken")]
    public async Task LookupById_NonTrackBody_IsNull(string body)
    {
        var track = await CreateService(new RoutingStub(_ => Json(body))).LookupByIdAsync("1");
        Assert.Null(track);
    }

    // --- albums ---

    [Fact]
    public async Task SearchAlbumId_ReturnsFirstHitWithAUsableId()
    {
        var stub = new RoutingStub(_ => Json("""{ "data": [ { "title": "no id" }, { "id": null }, { "id": "abc" }, { "id": 9 } ] }"""));
        Assert.Equal("abc", await CreateService(stub).SearchAlbumIdAsync("Artist", "Album"));
    }

    [Theory]
    [InlineData("""{ "data": [] }""")]
    [InlineData("""{ "data": "nope" }""")]
    [InlineData("""{ "error": { "code": 800 } }""")]
    [InlineData("not json")]
    public async Task SearchAlbumId_NoUsableHit_IsNull(string body)
    {
        Assert.Null(await CreateService(new RoutingStub(_ => Json(body))).SearchAlbumIdAsync("Artist", "Album"));
    }

    [Fact]
    public async Task SearchAlbumCandidates_ReadsIdTitleAndArtist()
    {
        var stub = new RoutingStub(_ => Json("""
            { "data": [
                { "id": 1, "title": "Discovery", "artist": { "name": "Daft Punk" } },
                { "id": "2", "artist": "flat string" },
                { "title": "no id" },
                3,
                { "id": 4, "title": 4, "artist": { "name": 4 } }
            ] }
            """));

        var candidates = await CreateService(stub).SearchAlbumCandidatesAsync("Daft Punk", "Discovery");

        Assert.Equal(
            [
                new DeezerAlbumCandidate("1", "Discovery", "Daft Punk"),
                new DeezerAlbumCandidate("2", null, null),
                new DeezerAlbumCandidate("4", null, null),
            ],
            candidates);
        Assert.Contains("/search/album?q=Daft%20Punk%20Discovery&limit=5", stub.Requests.Single());
    }

    [Theory]
    [InlineData("""{ "data": {} }""")]
    [InlineData("{{")]
    public async Task SearchAlbumCandidates_NoDataArray_IsEmpty(string body)
    {
        Assert.Empty(await CreateService(new RoutingStub(_ => Json(body))).SearchAlbumCandidatesAsync("A", "B"));
    }

    [Fact]
    public async Task GetAlbum_DefaultsDiscAndPositionAndFallsBackToTheBigCover()
    {
        var stub = new RoutingStub(_ => Json("""
            { "id": "77", "title": "Album", "release_date": "not a date",
              "cover_xl": 5, "cover_big": "https://c/big.jpg",
              "artist": { "name": 1 },
              "tracks": { "data": [
                  { "id": "a", "title": "First", "duration": 61 },
                  "skipped and not counted",
                  { "id": 2, "title": "Second", "duration": 62, "disk_number": 2, "track_position": 9 },
                  { "title": "Third", "disk_number": "2", "track_position": "3", "duration": "63" }
              ] } }
            """));

        var album = await CreateService(stub).GetAlbumAsync("77");

        Assert.NotNull(album);
        Assert.Equal(("77", "Album", (string?)null, (int?)null, "https://c/big.jpg"),
            (album!.Id, album.Title, album.Artist, album.Year, album.CoverUrl));
        Assert.Equal(
            [
                new DeezerAlbumTrackItem(1, 1, "First", 61_000, "a"),
                new DeezerAlbumTrackItem(2, 9, "Second", 62_000, "2"),
                new DeezerAlbumTrackItem(1, 3, "Third", 0, null),
            ],
            album.Tracks);
    }

    [Fact]
    public async Task GetAlbum_WithoutTracks_HasAnEmptyTracklist_AndPrefersTheXlCover()
    {
        var stub = new RoutingStub(_ => Json("""{ "id": 1, "cover_xl": "https://c/xl.jpg", "cover_big": "https://c/big.jpg", "tracks": {} }"""));

        var album = await CreateService(stub).GetAlbumAsync("1");

        Assert.NotNull(album);
        Assert.Equal(("1", (string?)null, "https://c/xl.jpg"), (album!.Id, album.Title, album.CoverUrl));
        Assert.Empty(album.Tracks);
    }

    [Theory]
    [InlineData("""{ "title": "no id" }""")]
    [InlineData("""{ "id": false }""")]
    [InlineData("""[ { "id": 1 } ]""")]
    [InlineData("<html>")]
    public async Task GetAlbum_NonAlbumBody_IsNull(string body)
    {
        Assert.Null(await CreateService(new RoutingStub(_ => Json(body))).GetAlbumAsync("1"));
    }

    // --- artists ---

    [Fact]
    public async Task SearchArtistCandidates_TakesTheLargestPicture()
    {
        var stub = new RoutingStub(_ => Json("""
            { "data": [
                { "name": "Xl", "picture_xl": "xl", "picture_big": "big", "picture_medium": "medium" },
                { "name": "Big", "picture_xl": 1, "picture_big": "big", "picture_medium": "medium" },
                { "name": "Medium", "picture_medium": "medium" },
                { "name": "EmptyXl", "picture_xl": "", "picture_big": "big" },
                { "picture_small": "small" },
                "skipped"
            ] }
            """));

        var artists = await CreateService(stub).SearchArtistCandidatesAsync("whoever");

        Assert.Equal(
            [
                new DeezerArtistCandidate("Xl", "xl"),
                new DeezerArtistCandidate("Big", "big"),
                new DeezerArtistCandidate("Medium", "medium"),
                new DeezerArtistCandidate("EmptyXl", ""),
                new DeezerArtistCandidate(null, null),
            ],
            artists);
    }

    [Fact]
    public async Task SearchArtistCandidates_Malformed_IsEmpty()
    {
        Assert.Empty(await CreateService(new RoutingStub(_ => Json("{ nope"))).SearchArtistCandidatesAsync("x"));
    }

    // --- discover ---

    [Fact]
    public async Task GetGenres_SkipsRowsWithoutANumericId_AndSkipsEmptyPictures()
    {
        var stub = new RoutingStub(_ => Json("""
            { "data": [
                { "id": 0, "name": "All", "picture_medium": "", "picture_big": "big", "picture": "plain" },
                { "id": "132", "name": "String id" },
                { "id": -1, "name": "Negative" },
                { "id": 7 },
                { "name": "No id" }
            ] }
            """));

        var genres = await CreateService(stub).GetGenresAsync();

        Assert.Equal([new DeezerGenre(0, "All", "big"), new DeezerGenre(7, "", null)], genres);
    }

    [Fact]
    public async Task GetPlaylist_ReadsTheSummary_WithCreatorAndDescriptionRules()
    {
        var stub = new RoutingStub(_ => Json("""
            { "id": 908622995, "title": "Rap Bangers", "description": "   ",
              "picture_xl": "", "picture_big": "https://p/big.jpg", "picture": "https://p/plain.jpg",
              "nb_tracks": 60, "checksum": "c0ffee",
              "user": { "id": 1 }, "creator": { "name": "Deezer Editor" } }
            """));

        var playlist = await CreateService(stub).GetPlaylistAsync("908622995");

        Assert.Equal(
            new DeezerPlaylistSummary("908622995", "Rap Bangers", null, "https://p/big.jpg", 60, "Deezer Editor", "c0ffee"),
            playlist);
    }

    [Fact]
    public async Task GetPlaylist_UserNameBeatsCreator_AndMissingFieldsDefault()
    {
        var stub = new RoutingStub(_ => Json("""
            { "id": "pl", "description": "Fresh", "user": { "name": "Owner" }, "creator": { "name": "Ignored" } }
            """));

        var playlist = await CreateService(stub).GetPlaylistAsync("pl");

        Assert.Equal(new DeezerPlaylistSummary("pl", "", "Fresh", null, 0, "Owner", null), playlist);
    }

    [Theory]
    [InlineData("""{ "title": "no id" }""")]
    [InlineData("""{ "id": {} }""")]
    [InlineData("[]")]
    [InlineData("{")]
    public async Task GetPlaylist_NonPlaylistBody_IsNull(string body)
    {
        Assert.Null(await CreateService(new RoutingStub(_ => Json(body))).GetPlaylistAsync("x"));
    }

    [Fact]
    public async Task SearchPlaylists_SkipsRowsWithoutAnId()
    {
        var stub = new RoutingStub(_ => Json("""
            { "data": [ { "id": 1, "title": "Kept" }, { "title": "Dropped" }, "dropped", { "id": "2", "title": "Also kept" } ] }
            """));

        var playlists = await CreateService(stub).SearchPlaylistsAsync("rap", 10);

        Assert.Equal(["1", "2"], playlists.Select(p => p.Id));
    }

    [Fact]
    public async Task GetPlaylistTracks_ReadsTrackRows()
    {
        var stub = new RoutingStub(_ => Json("""
            { "data": [
                { "id": 1, "title": "A", "duration": 100, "artist": { "name": "Ann" },
                  "album": { "title": "  ", "cover_medium": "", "cover_big": "big", "cover_xl": "xl" } },
                { "id": "2", "album": { "title": "Album", "cover": "plain" } },
                { "title": "no id" },
                { "id": 3, "title": 3, "duration": "3", "artist": "flat", "album": "flat" }
            ] }
            """));

        var result = await CreateService(stub).GetPlaylistTracksAsync("pl");

        Assert.True(result.IsComplete);
        Assert.Equal(
            [
                new DeezerPlaylistTrack("1", "A", "Ann", null, 100_000, "big"),
                new DeezerPlaylistTrack("2", "", "", "Album", 0, "plain"),
                new DeezerPlaylistTrack("3", "", "", null, 0, null),
            ],
            result.Tracks);
    }

    [Fact]
    public async Task GetPlaylistTracks_FollowsTheNextLink_UntilAShortPage()
    {
        var stub = new RoutingStub(req => req.RequestUri!.Query.Contains("index=0&")
            ? Json(TracksPage(1, 100, next: "https://api.deezer.com/playlist/pl/tracks?index=100"))
            : Json(TracksPage(101, 5, next: "https://api.deezer.com/playlist/pl/tracks?index=200")));

        var result = await CreateService(stub).GetPlaylistTracksAsync("pl");

        Assert.True(result.IsComplete);
        Assert.Equal(105, result.Tracks.Count);
        Assert.Equal(("1", "105"), (result.Tracks[0].Id, result.Tracks[^1].Id));
        Assert.Equal(2, stub.Requests.Count);
        Assert.Contains("index=100&limit=100", stub.Requests[1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task GetPlaylistTracks_FullPageWithoutANextLink_Stops(string? next)
    {
        var stub = new RoutingStub(_ => Json(TracksPage(1, 100, next)));

        var result = await CreateService(stub).GetPlaylistTracksAsync("pl");

        Assert.True(result.IsComplete);
        Assert.Equal(100, result.Tracks.Count);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task GetPlaylistTracks_MalformedPage_EndsComplete()
    {
        var stub = new RoutingStub(_ => Json("{ broken"));

        var result = await CreateService(stub).GetPlaylistTracksAsync("pl");

        Assert.True(result.IsComplete);
        Assert.Empty(result.Tracks);
    }

    [Fact]
    public async Task GetPlaylistTracks_FailedSecondPage_IsIncomplete()
    {
        var stub = new RoutingStub(req => req.RequestUri!.Query.Contains("index=0&")
            ? Json(TracksPage(1, 100, next: "more"))
            : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") });

        var result = await CreateService(stub).GetPlaylistTracksAsync("pl");

        Assert.False(result.IsComplete);
        Assert.Equal(100, result.Tracks.Count);
    }

    [Fact]
    public async Task GetPlaylistTracks_Capped_IsTrimmedAndIncomplete()
    {
        var stub = new RoutingStub(_ => Json(TracksPage(1, 100, next: "more")));

        var result = await CreateService(stub).GetPlaylistTracksAsync("pl", maxTracks: 30);

        Assert.False(result.IsComplete);
        Assert.Equal(30, result.Tracks.Count);
        Assert.Single(stub.Requests);
    }

    // A top-level array (or a non-object "tracks") is not a JsonException: JsonElement.TryGetProperty
    // throws InvalidOperationException on a non-object, and these readers only catch JsonException.
    // Pinned as it stands today; see the PR's "Found while refactoring".
    [Fact]
    public async Task ArrayWhereAnObjectIsExpected_ThrowsToday()
    {
        var array = new RoutingStub(_ => Json("[ ]"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(array).SearchTracksAsync("q"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(array).SearchAlbumIdAsync("a", "b"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(array).SearchAlbumCandidatesAsync("a", "b"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(array).GetGenresAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(array).GetPlaylistTracksAsync("pl"));

        var albumWithTracksArray = new RoutingStub(_ => Json("""{ "id": 1, "tracks": [] }"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(albumWithTracksArray).GetAlbumAsync("1"));

        var albumIdHitNotAnObject = new RoutingStub(_ => Json("""{ "data": [ 3, { "id": 4 } ] }"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(albumIdHitNotAnObject).SearchAlbumIdAsync("a", "b"));
    }

    // --- quota body ---

    [Fact]
    public async Task QuotaErrorBody_IsRetried_ThenTheNextBodyIsRead()
    {
        var calls = 0;
        var stub = new RoutingStub(_ => ++calls == 1
            ? Json("""{ "error": { "type": "Exception", "message": "Quota limit exceeded", "code": 4 } }""")
            : Json("""{ "id": 1, "title": "After quota" }"""));

        var track = await CreateService(stub).LookupByIdAsync("quota-retry");

        Assert.Equal("After quota", track?.Title);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Theory]
    [InlineData("""{ "error": { "type": "DataException", "message": "no data", "code": 800 } }""")]
    [InlineData("""{ "error": { "code": "4" } }""")]
    [InlineData("""{ "error": 4 }""")]
    public async Task OtherErrorBodies_AreNotRetried(string body)
    {
        var stub = new RoutingStub(_ => Json(body));

        var track = await CreateService(stub).LookupByIdAsync("not-quota");

        Assert.Null(track);
        Assert.Single(stub.Requests);
    }

    private static string TracksPage(int firstId, int count, string? next)
    {
        var rows = string.Join(",", Enumerable.Range(firstId, count)
            .Select(i => $$"""{ "id": {{i}}, "title": "T{{i}}", "duration": 1, "artist": { "name": "A" } }"""));
        var nextJson = next is null ? "" : $$""", "next": "{{next}}" """;
        return $$"""{ "data": [ {{rows}} ]{{nextJson}} }""";
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static DeezerCatalogService CreateService(RoutingStub stub)
    {
        var httpClient = new HttpClient(stub) { Timeout = TimeSpan.FromSeconds(30) };
        var opts = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/s",
            DestinationDirectory = "/d",
            DeezerApiRequestsPerSecond = 20,
        });
        return new DeezerCatalogService(httpClient, new MemoryCache(new MemoryCacheOptions()), opts, NullLogger<DeezerCatalogService>.Instance);
    }

    private sealed class RoutingStub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(respond(request));
        }
    }
}
