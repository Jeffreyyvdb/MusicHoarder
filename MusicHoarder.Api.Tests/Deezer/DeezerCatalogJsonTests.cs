using MusicHoarder.Api.Deezer;

namespace MusicHoarder.Api.Tests.Deezer;

/// <summary>
/// <see cref="DeezerCatalogJson"/> straight from response bodies, no HTTP. The end-to-end field
/// rules are pinned through the service in <see cref="DeezerCatalogResponseParsingTests"/>; these
/// cover what the mapper decides on its own: the quota-error shape, the malformed-body contract of
/// every reader, the id number/string rule and the search-vs-detail split.
/// </summary>
public class DeezerCatalogJsonTests
{
    [Theory]
    [InlineData("""{ "error": { "code": 4 } }""", true)]
    [InlineData("""{ "error": { "type": "Exception", "message": "Quota limit exceeded", "code": 4 } }""", true)]
    [InlineData("""{ "error": { "code": 800 } }""", false)]
    [InlineData("""{ "error": { "code": "4" } }""", false)]
    [InlineData("""{ "error": 4 }""", false)]
    [InlineData("""{ "code": 4 }""", false)]
    [InlineData("""[ { "error": { "code": 4 } } ]""", false)]
    [InlineData("""{ "id": 1, "title": "a track" }""", false)]
    [InlineData("not json", false)]
    [InlineData("", false)]
    public void IsQuotaError_MatchesOnlyTheCode4ErrorObject(string body, bool expected)
    {
        Assert.Equal(expected, DeezerCatalogJson.IsQuotaError(body));
    }

    [Fact]
    public void EveryReader_TurnsAMalformedBodyIntoEmptyOrNull()
    {
        const string broken = "{ \"data\": [";

        Assert.Empty(DeezerCatalogJson.ParseSearchResponse(broken));
        Assert.Null(DeezerCatalogJson.ParseTrackDetail(broken));
        Assert.Null(DeezerCatalogJson.ParseFirstAlbumId(broken));
        Assert.Empty(DeezerCatalogJson.ParseAlbumCandidates(broken));
        Assert.Null(DeezerCatalogJson.ParseAlbum(broken));
        Assert.Empty(DeezerCatalogJson.ParseArtistCandidates(broken));
    }

    [Theory]
    [InlineData("""{ "id": 3135556, "title": "t" }""", "3135556")]
    [InlineData("""{ "id": 9007199254740993, "title": "t" }""", "9007199254740993")]
    [InlineData("""{ "id": "abc", "title": "t" }""", "abc")]
    public void Ids_AreReadFromANumberOrAString(string body, string expected)
    {
        Assert.Equal(expected, DeezerCatalogJson.ParseTrackDetail(body)?.Id);
        Assert.Equal(expected, DeezerCatalogJson.ParseAlbum(body)?.Id);
    }

    [Theory]
    [InlineData("""{ "id": null }""")]
    [InlineData("""{ "id": "" }""")]
    [InlineData("""{ "id": [] }""")]
    public void TrackDetail_WithoutAUsableId_IsNull(string body)
    {
        Assert.Null(DeezerCatalogJson.ParseTrackDetail(body));
    }

    [Fact]
    public void NonIntegralNumericId_ThrowsToday()
    {
        // GetInt64 throws FormatException on a number it cannot hold, and the readers only catch
        // JsonException. Pinned as it stands; see the PR's "Found while refactoring".
        Assert.Throws<FormatException>(() => DeezerCatalogJson.ParseTrackDetail("""{ "id": 1.5 }"""));
    }

    [Fact]
    public void SearchHit_AndFullDetail_ReadTheSamePayloadDifferently()
    {
        const string payload = """
            { "id": 1, "title": "T", "duration": 30, "isrc": "X", "release_date": "1999-12-31",
              "track_position": 2, "artist": { "name": "A" }, "album": { "title": "B" },
              "contributors": [ { "name": "A" }, { "name": "C" } ] }
            """;

        var hit = Assert.Single(DeezerCatalogJson.ParseSearchResponse($$"""{ "data": [ {{payload}} ] }"""));
        var detail = DeezerCatalogJson.ParseTrackDetail(payload);

        Assert.Equal(new DeezerCatalogTrack("1", "T", "A", "B", null, null, 30_000, null, Artists: null), hit);
        Assert.Equal(new DeezerCatalogTrack("1", "T", "A", "B", 1999, 2, 30_000, "X", Artists: "A; C"), detail);
    }

    [Fact]
    public void FirstAlbumId_AndAlbumCandidates_ReadTheSameSearchBody()
    {
        const string body = """{ "data": [ { "title": "no id" }, { "id": "42", "title": "Hit", "artist": { "name": "Art" } } ] }""";

        Assert.Equal("42", DeezerCatalogJson.ParseFirstAlbumId(body));
        Assert.Equal([new DeezerAlbumCandidate("42", "Hit", "Art")], DeezerCatalogJson.ParseAlbumCandidates(body));
    }
}
