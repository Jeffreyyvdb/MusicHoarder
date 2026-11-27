using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Catalog;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Deezer;

namespace MusicHoarder.Api.Tests.Catalog;

public class CatalogSearchTests
{
    private const string SecretPreviewUrl = "https://cdnt-preview.dzcdn.net/stream/c-abc-1.mp3?hdnea=exp=1~hmac=secret";

    private static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static DeezerCatalogTrack Hit(string id, string title = "Song", string? preview = SecretPreviewUrl,
        string? cover = "https://cdn-images.dzcdn.net/cover.jpg", bool isExplicit = false, string album = "Album") =>
        new(id, title, "Artist", album, null, null, 200_000, null, CoverUrl: cover, PreviewUrl: preview, Explicit: isExplicit);

    private static Task<IResult> Search(string? q, FakeDeezerCatalogService deezer, MusicHoarderDbContext db) =>
        CatalogEndpoints.Search(q, deezer, db, NullLogger<CatalogTrack>.Instance, default);

    [Fact]
    public async Task MapsEachHit_AndNeverCarriesThePreviewUrl()
    {
        await using var db = NewContext();
        var deezer = new FakeDeezerCatalogService();
        deezer.SearchResults.Add(Hit("1", isExplicit: true));
        deezer.SearchResults.Add(Hit("2", preview: null, cover: null, album: ""));

        var ok = Assert.IsType<Ok<CatalogSearchResponse>>(await Search("artist song", deezer, db));
        var tracks = ok.Value!.Tracks;

        Assert.Equal(new CatalogTrack("1", "Song", "Artist", "Album", 200_000, "https://cdn-images.dzcdn.net/cover.jpg", true, true, null), tracks[0]);
        Assert.Equal(new CatalogTrack("2", "Song", "Artist", null, 200_000, null, false, false, null), tracks[1]);

        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("hdnea", json);
        Assert.DoesNotContain("preview", json.Replace("hasPreview", ""), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"deezerId\":\"1\"", json);
        Assert.Contains("\"durationMs\":200000", json);
    }

    [Fact]
    public async Task JoinsTheNewestWishlistRowStatus_PerDeezerId()
    {
        await using var db = NewContext();
        var owner = WellKnownUsers.OwnerId;
        db.WishlistItems.AddRange(
            new WishlistItem { OwnerUserId = owner, DeezerTrackId = "1", Title = "t", Artist = "a", Status = WishlistItemStatus.Failed, UpdatedAtUtc = DateTime.UtcNow.AddDays(-2) },
            new WishlistItem { OwnerUserId = owner, DeezerTrackId = "1", Title = "t", Artist = "a", Status = WishlistItemStatus.Downloaded, UpdatedAtUtc = DateTime.UtcNow },
            new WishlistItem { OwnerUserId = owner, DeezerTrackId = "3", Title = "t", Artist = "a", Status = WishlistItemStatus.Pending, UpdatedAtUtc = DateTime.UtcNow },
            new WishlistItem { OwnerUserId = owner, DeezerTrackId = null, Title = "t", Artist = "a", Status = WishlistItemStatus.Pending });
        await db.SaveChangesAsync();
        var deezer = new FakeDeezerCatalogService();
        deezer.SearchResults.AddRange([Hit("1"), Hit("2")]);

        var ok = Assert.IsType<Ok<CatalogSearchResponse>>(await Search("song", deezer, db));

        Assert.Equal("Downloaded", ok.Value!.Tracks[0].WishlistStatus);
        Assert.Null(ok.Value.Tracks[1].WishlistStatus);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]
    [InlineData(" a ")]
    public async Task ABlankOrOneCharacterQuery_AnswersEmptyWithoutAskingDeezer(string? q)
    {
        await using var db = NewContext();
        var deezer = new FakeDeezerCatalogService { SearchFailure = new InvalidOperationException("must not be called") };

        var ok = Assert.IsType<Ok<CatalogSearchResponse>>(await Search(q, deezer, db));

        Assert.Empty(ok.Value!.Tracks);
    }

    [Fact]
    public async Task AnswersAtMostTheResultCap()
    {
        await using var db = NewContext();
        var deezer = new FakeDeezerCatalogService();
        deezer.SearchResults.AddRange(Enumerable.Range(1, 40).Select(i => Hit(i.ToString())));

        var ok = Assert.IsType<Ok<CatalogSearchResponse>>(await Search("song", deezer, db));

        Assert.Equal(CatalogEndpoints.MaxSearchResults, ok.Value!.Tracks.Count);
    }

    [Fact]
    public async Task DeezerBeingUnreachable_Answers502_NotAnEmptyList()
    {
        await using var db = NewContext();
        var deezer = new FakeDeezerCatalogService { SearchFailure = new HttpRequestException("no route") };

        var result = await Search("song", deezer, db);

        Assert.Equal(StatusCodes.Status502BadGateway, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Contains("catalog_unavailable", JsonSerializer.Serialize(((IValueHttpResult)result).Value));
    }

    [Fact]
    public async Task ARateLimitedDeezer_Answers502()
    {
        await using var db = NewContext();
        var deezer = new FakeDeezerCatalogService { SearchFailure = new MusicHoarder.Api.Enrichment.ProviderRateLimitedException(TimeSpan.FromSeconds(5)) };

        var result = await Search("song", deezer, db);

        Assert.Equal(StatusCodes.Status502BadGateway, ((IStatusCodeHttpResult)result).StatusCode);
    }
}
