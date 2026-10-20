using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Tests.Spotify;

/// <summary>
/// The catalog body readers on their own, from raw JSON — no transport, cache or options.
/// The field-by-field mapping is pinned through the service in
/// <see cref="SpotifyCatalogResponseParsingTests"/>; these cover what only shows with the
/// readers side by side.
/// </summary>
public class SpotifyCatalogJsonTests
{
    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"tracks":""")]
    public void InvalidJson_ReadsAsEmptyOrNull_InEveryReader(string body)
    {
        Assert.Empty(SpotifyCatalogJson.ParseSearchResponse(body));
        Assert.Null(SpotifyCatalogJson.ParseTrackDetail(body));
        Assert.Null(SpotifyCatalogJson.ParseTrackAlbumId(body));
        Assert.Null(SpotifyCatalogJson.ParseFirstAlbumId(body));
        Assert.Empty(SpotifyCatalogJson.ParseAlbumCandidates(body));
        Assert.Empty(SpotifyCatalogJson.ParseArtistCandidates(body));
        Assert.Null(SpotifyCatalogJson.ParseFirstTrackId(body));
        Assert.Null(SpotifyCatalogJson.ParseAlbum(body));
    }

    [Fact]
    public void EachSearchReader_ReadsOnlyItsOwnList()
    {
        // One body carrying all three result lists, as a multi-type search would return.
        const string body =
            """
            {
              "tracks":  {"items": [{"id": "track-1", "name": "Song"}]},
              "albums":  {"items": [{"id": "album-1", "name": "Album"}]},
              "artists": {"items": [{"name": "Artist"}]}
            }
            """;

        Assert.Equal(["track-1"], SpotifyCatalogJson.ParseSearchResponse(body).Select(t => t.Id));
        Assert.Equal("track-1", SpotifyCatalogJson.ParseFirstTrackId(body));
        Assert.Equal("album-1", SpotifyCatalogJson.ParseFirstAlbumId(body));
        Assert.Equal([new SpotifyAlbumCandidate("album-1", "Album", null)], SpotifyCatalogJson.ParseAlbumCandidates(body));
        Assert.Equal([new SpotifyArtistCandidate("Artist", null)], SpotifyCatalogJson.ParseArtistCandidates(body));
    }

    [Fact]
    public void ATrackDetail_ReadsLikeTheSameObjectAsASearchHit()
    {
        const string item =
            """{"id":"t1","name":"Song","duration_ms":1000,"track_number":2,"artists":[{"name":"A"},{"name":"B"}],"album":{"id":"al1","name":"Al","release_date":"2020","images":[{"url":"https://img"}]},"external_ids":{"isrc":"X"}}""";

        var detail = SpotifyCatalogJson.ParseTrackDetail(item);

        Assert.NotNull(detail);
        Assert.Equal(Assert.Single(SpotifyCatalogJson.ParseSearchResponse("""{"tracks":{"items":[""" + item + "]}}")), detail);
        Assert.Equal("al1", SpotifyCatalogJson.ParseTrackAlbumId(item));
        Assert.Equal("al1", detail!.AlbumId);
    }

    [Fact]
    public void FirstAlbumId_SkipsTheObjectRowsTheCandidatesSkip()
    {
        const string body = """{"albums":{"items":[{"id":5},{"name":"no id"},{"id":"a2"},{"id":"a3"}]}}""";

        Assert.Equal("a2", SpotifyCatalogJson.ParseFirstAlbumId(body));
        Assert.Equal(["a2", "a3"], SpotifyCatalogJson.ParseAlbumCandidates(body).Select(c => c.Id));
    }

    [Theory]
    [InlineData("""[{"type":"P","text":"℗ p"},{"type":"C","text":"© c"}]""", "© c")]
    [InlineData("""[{"type":"c","text":" © lower "}]""", "© lower")]
    [InlineData("""[{"type":"P","text":"℗ p"}]""", "℗ p")]
    [InlineData("""[{"type":"C","text":"  "},{"type":"P","text":"℗ p"}]""", "℗ p")]
    [InlineData("""[{"text":"untyped"},{"type":"P","text":"℗ p"}]""", "untyped")]
    [InlineData("""[{"type":"C","text":"© first"},{"type":"C","text":"© second"}]""", "© first")]
    [InlineData("""[]""", null)]
    [InlineData("""{"type":"C","text":"© not a list"}""", null)]
    public void AlbumCopyright_PrefersTheCopyrightLine_ElseTheFirstNonBlankLine(string copyrights, string? expected)
    {
        var album = SpotifyCatalogJson.ParseAlbum($$"""{"id":"a","copyrights":{{copyrights}}}""");

        Assert.Equal(expected, album!.Copyright);
    }

    [Fact]
    public void Album_WithoutATracksObject_HasAnEmptyTracklist()
    {
        var album = SpotifyCatalogJson.ParseAlbum("""{"id":"a","name":"N"}""");

        Assert.NotNull(album);
        Assert.Empty(album!.Tracks);
    }

    [Fact]
    public void WrongShapedBodies_ThrowToday_InsteadOfReadingAsEmpty()
    {
        // Only JsonException is caught, so a valid body of the wrong shape escapes to the caller.
        Assert.Throws<InvalidOperationException>(() => SpotifyCatalogJson.ParseTrackAlbumId("[]"));
        Assert.Throws<InvalidOperationException>(() => SpotifyCatalogJson.ParseFirstAlbumId("[]"));
        Assert.Throws<InvalidOperationException>(() => SpotifyCatalogJson.ParseAlbumCandidates("[]"));
        Assert.Throws<InvalidOperationException>(() => SpotifyCatalogJson.ParseArtistCandidates("[]"));
        Assert.Throws<InvalidOperationException>(() => SpotifyCatalogJson.ParseFirstTrackId("[]"));
        Assert.Throws<InvalidOperationException>(() => SpotifyCatalogJson.ParseFirstTrackId("""{"tracks":{"items":["x"]}}"""));
        Assert.Throws<InvalidOperationException>(() => SpotifyCatalogJson.ParseTrackDetail("""{"id":"t1","duration_ms":"1"}"""));
    }
}
