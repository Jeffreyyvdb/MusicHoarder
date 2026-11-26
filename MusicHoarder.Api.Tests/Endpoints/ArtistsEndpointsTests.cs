using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>The portrait endpoints' contracts: the bulk map's cap and validator, and how long a miss is cached.</summary>
public class ArtistsEndpointsTests
{
    private sealed class FakeArtistImages(Dictionary<string, ArtistImage?> rows, ArtistImageLookup lookup = default)
        : IArtistImageService
    {
        public Task<string?> GetImageUrlAsync(string name, bool large = false, CancellationToken ct = default) =>
            Task.FromResult(lookup.Url);

        public Task<ArtistImageLookup> LookupAsync(string name, bool large = false, CancellationToken ct = default) =>
            Task.FromResult(lookup);

        public Task<IReadOnlyDictionary<string, ArtistImage?>> GetImagesMapAsync(
            IReadOnlyList<string> names, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, ArtistImage?>>(
                names.ToDictionary(n => n, n => rows.GetValueOrDefault(n)));

        public Task RefreshAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static readonly Dictionary<string, ArtistImage?> Rows = new()
    {
        ["Daft Punk"] = new ArtistImage
        {
            NormalizedName = "daft punk", DisplayName = "Daft Punk",
            ImageUrl = "https://cdn.example/dp-500.jpg", ImageUrlLarge = "https://cdn.example/dp-1000.jpg",
            FetchedAtUtc = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
        },
    };

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 200;

    [Fact]
    public async Task Bulk_rejects_more_names_than_the_cap()
    {
        var names = string.Join('\n', Enumerable.Range(0, ArtistsEndpoints.MaxBulkNames + 1).Select(i => $"Artist {i}"));

        var result = await ArtistsEndpoints.GetArtistImages(names, new FakeArtistImages(Rows), new DefaultHttpContext(), default);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
    }

    [Fact]
    public async Task Bulk_answers_a_repeat_with_304_while_nothing_changed()
    {
        var service = new FakeArtistImages(Rows);
        var first = new DefaultHttpContext();
        var body = await ArtistsEndpoints.GetArtistImages("Daft Punk\nUnknown", service, first, default);
        Assert.Equal(StatusCodes.Status200OK, StatusOf(body));
        var etag = first.Response.Headers.ETag.ToString();
        Assert.False(string.IsNullOrEmpty(etag));
        Assert.Equal("private, max-age=60", first.Response.Headers.CacheControl.ToString());

        var repeat = new DefaultHttpContext();
        repeat.Request.Headers.IfNoneMatch = etag;
        var result = await ArtistsEndpoints.GetArtistImages("Daft Punk\nUnknown", service, repeat, default);

        Assert.Equal(StatusCodes.Status304NotModified, StatusOf(result));
    }

    [Theory]
    [InlineData(true, "private, max-age=60")]
    [InlineData(false, "private, max-age=3600")]
    public async Task A_miss_is_cached_briefly_while_a_lookup_is_pending(bool pending, string expected)
    {
        var http = new DefaultHttpContext();

        var result = await ArtistsEndpoints.GetArtistImage(
            "Newly Followed", new FakeArtistImages(Rows, new ArtistImageLookup(null, pending)), http);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
        Assert.Equal(expected, http.Response.Headers.CacheControl.ToString());
    }
}
