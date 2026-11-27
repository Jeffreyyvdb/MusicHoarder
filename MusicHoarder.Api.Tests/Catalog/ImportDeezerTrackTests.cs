using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;
using MusicHoarder.Api.Tests.Deezer;

namespace MusicHoarder.Api.Tests.Catalog;

public class ImportDeezerTrackTests
{
    private static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static FakeDeezerCatalogService DeezerWith(string id = "3135556", string? isrc = "GBDUW0000059")
    {
        var deezer = new FakeDeezerCatalogService();
        deezer.TracksById[id] = new DeezerCatalogTrack(id, "Harder, Better, Faster, Stronger", "Daft Punk", "Discovery", 2001, 4, 224_000, isrc,
            CoverUrl: "https://cdn-images.dzcdn.net/discovery.jpg");
        return deezer;
    }

    private static Task<IResult> Import(ImportTrackRequest body, MusicHoarderDbContext db, FakeDeezerCatalogService deezer, bool downloads = true) =>
        ImportEndpoints.ImportTrack(
            body, db, new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser), new JobManager(), deezer,
            Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions { EnableWishlistDownloads = downloads, DownloadDirectory = "/downloads" }), default);

    private static ImportTrackRequest Body(string? deezerId, string? title = null, string? artist = null, string? album = null,
        string? isrc = null, int? durationMs = null, string? coverUrl = null, string? spotify = null) =>
        new(Source: null, Title: title, Artist: artist, Album: album, DurationMs: durationMs, CoverUrl: coverUrl,
            SpotifyTrackId: spotify, Isrc: isrc, SourceUrl: null, DeezerTrackId: deezerId);

    [Fact]
    public async Task ADeezerIdAlone_FillsTheItemFromTheCatalogLookup()
    {
        await using var db = NewContext();

        var result = await Import(Body("3135556"), db, DeezerWith());

        Assert.Equal(StatusCodes.Status202Accepted, ((IStatusCodeHttpResult)result).StatusCode);
        var item = await db.WishlistItems.SingleAsync();
        Assert.Equal("3135556", item.DeezerTrackId);
        Assert.Equal("GBDUW0000059", item.Isrc);
        Assert.Equal("Harder, Better, Faster, Stronger", item.Title);
        Assert.Equal("Daft Punk", item.Artist);
        Assert.Equal("Discovery", item.Album);
        Assert.Equal(224_000, item.DurationMs);
        Assert.Equal("https://cdn-images.dzcdn.net/discovery.jpg", item.AlbumArt);
        Assert.Null(item.SpotifyTrackId);
        Assert.Equal(WishlistItemStatus.Pending, item.Status);
        Assert.Equal(WishlistItemOrigin.UserRequested, item.Origin);
    }

    [Fact]
    public async Task WhatTheBodyFills_WinsOverTheLookup()
    {
        await using var db = NewContext();

        await Import(Body("3135556", title: "My Title", artist: "My Artist", album: "My Album", durationMs: 1000, coverUrl: "https://x/c.jpg"),
            db, DeezerWith());

        var item = await db.WishlistItems.SingleAsync();
        Assert.Equal(("My Title", "My Artist", "My Album", 1000, "https://x/c.jpg"),
            (item.Title, item.Artist, item.Album, item.DurationMs, item.AlbumArt));
        Assert.Equal("GBDUW0000059", item.Isrc);
    }

    [Fact]
    public async Task ATrackDeezerDoesNotHave_Answers404NotFound_AndQueuesNothing()
    {
        await using var db = NewContext();

        var result = await Import(Body("999"), db, DeezerWith());

        Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Empty(db.WishlistItems);
    }

    [Theory]
    [InlineData("12ab")]
    [InlineData("1.5")]
    [InlineData("../1")]
    public async Task ANonNumericDeezerId_Answers400(string id)
    {
        await using var db = NewContext();

        var result = await Import(Body(id), db, DeezerWith());

        Assert.Equal(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Empty(db.WishlistItems);
    }

    [Fact]
    public async Task ImportingTheSameDeezerTrackAgain_RequeuesTheExistingRow()
    {
        await using var db = NewContext();
        var deezer = DeezerWith();
        await Import(Body("3135556"), db, deezer);
        var first = await db.WishlistItems.SingleAsync();
        first.Status = WishlistItemStatus.Failed;
        await db.SaveChangesAsync();

        await Import(Body("3135556"), db, deezer);

        var only = await db.WishlistItems.SingleAsync();
        Assert.Equal(first.Id, only.Id);
        Assert.Equal(WishlistItemStatus.Pending, only.Status);
    }

    [Fact]
    public async Task ATrackAlreadyOnTheWishlistByIsrc_IsRequeued_AndGainsTheDeezerId()
    {
        await using var db = NewContext();
        db.WishlistItems.Add(new WishlistItem
        {
            OwnerUserId = WellKnownUsers.OwnerId, SpotifyTrackId = "spotify1", Isrc = "GBDUW0000059",
            Title = "Harder", Artist = "Daft Punk", Status = WishlistItemStatus.NotFound,
        });
        await db.SaveChangesAsync();

        await Import(Body("3135556"), db, DeezerWith());

        var only = await db.WishlistItems.SingleAsync();
        Assert.Equal("3135556", only.DeezerTrackId);
        Assert.Equal("spotify1", only.SpotifyTrackId);
        Assert.Equal(WishlistItemStatus.Pending, only.Status);
    }

    [Fact]
    public async Task AnIsrcTheClientTyped_IsNotADedupeKey()
    {
        await using var db = NewContext();
        db.WishlistItems.Add(new WishlistItem
        {
            OwnerUserId = WellKnownUsers.OwnerId, SpotifyTrackId = "spotify1", Isrc = "TYPED0000001", Title = "Other", Artist = "Other",
        });
        await db.SaveChangesAsync();

        await Import(Body("3135556", isrc: "TYPED0000001"), db, DeezerWith());

        Assert.Equal(2, await db.WishlistItems.CountAsync());
    }

    [Fact]
    public async Task ADeezerTrackWithoutAnIsrc_StillImports_AndDedupesOnItsId()
    {
        await using var db = NewContext();
        var deezer = DeezerWith(isrc: null);

        await Import(Body("3135556"), db, deezer);
        await Import(Body("3135556"), db, deezer);

        var only = await db.WishlistItems.SingleAsync();
        Assert.Null(only.Isrc);
    }

    [Fact]
    public async Task ADeezerIdCountsAsASource()
    {
        await using var db = NewContext();
        var withoutAnySource = await Import(Body(null, title: "T", artist: "A"), db, DeezerWith());
        Assert.Equal(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)withoutAnySource).StatusCode);

        var withDeezer = await Import(Body("3135556", title: "T", artist: "A"), db, DeezerWith());
        Assert.Equal(StatusCodes.Status202Accepted, ((IStatusCodeHttpResult)withDeezer).StatusCode);
    }

    [Fact]
    public async Task SpotifyImportsStillWorkWithoutADeezerId()
    {
        await using var db = NewContext();

        var result = await Import(Body(null, title: "T", artist: "A", spotify: "sp1"), db, DeezerWith());

        Assert.Equal(StatusCodes.Status202Accepted, ((IStatusCodeHttpResult)result).StatusCode);
        var item = await db.WishlistItems.SingleAsync();
        Assert.Equal("sp1", item.SpotifyTrackId);
        Assert.Null(item.DeezerTrackId);
    }

    [Fact]
    public async Task DisabledDownloads_AnswerBeforeAnyLookup()
    {
        await using var db = NewContext();

        var result = await Import(Body("3135556"), db, DeezerWith(), downloads: false);

        Assert.Equal(StatusCodes.Status409Conflict, ((IStatusCodeHttpResult)result).StatusCode);
    }
}
