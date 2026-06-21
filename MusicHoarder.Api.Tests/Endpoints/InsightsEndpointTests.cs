using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Endpoints;

public class InsightsEndpointTests
{
    private static readonly Guid OwnerA = Api.Auth.WellKnownUsers.OwnerId;
    private static readonly Guid OwnerB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static JsonElement Json(IResult result)
    {
        var value = ((IValueHttpResult)result).Value!;
        return JsonSerializer.SerializeToElement(value, value.GetType());
    }

    private static int Int(JsonElement el, params string[] path)
    {
        foreach (var p in path)
            el = el.GetProperty(p);
        return el.GetInt32();
    }

    private static SongMetadata NewSong(int n, Guid owner = default) => new()
    {
        OwnerUserId = owner == default ? OwnerA : owner,
        SourcePath = $"/src/{n}.mp3",
        FileName = $"{n}.mp3",
        Extension = ".mp3",
        FileSizeBytes = 5_000_000,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
    };

    [Fact]
    public async Task GetInsights_ComputesCoreStoryStats()
    {
        using var db = NewContext();

        // Two built tracks of one album; #1 fully enriched (cover + lyrics), #2 bare.
        var built1 = NewSong(1);
        built1.LibraryBuildStatus = LibraryBuildStatus.Done;
        built1.DestinationPath = "/dest/Artist A/Album 1/1.mp3";
        built1.HasCoverArt = true;
        built1.LyricsStatus = LyricsStatus.Fetched;
        built1.EnrichmentStatus = EnrichmentStatus.Matched;
        built1.MatchConfidence = 0.95;
        built1.Fingerprint = "fp1";
        built1.Artist = "Artist A";
        built1.AlbumArtist = "Artist A";
        built1.Album = "Album 1";
        built1.DurationSeconds = 200;

        var built2 = NewSong(2);
        built2.LibraryBuildStatus = LibraryBuildStatus.Done;
        built2.DestinationPath = "/dest/Artist A/Album 1/2.mp3";
        built2.HasCoverArt = false;
        built2.LyricsStatus = LyricsStatus.NotFetched;
        built2.EnrichmentStatus = EnrichmentStatus.Matched;
        built2.MatchConfidence = 0.80;
        built2.Fingerprint = "fp2";
        built2.Artist = "Artist A";
        built2.AlbumArtist = "Artist A";
        built2.Album = "Album 1";
        built2.DurationSeconds = 160;

        // Indexed but not built.
        var review = NewSong(3);
        review.EnrichmentStatus = EnrichmentStatus.NeedsReview;
        review.Fingerprint = "fp3";

        var failed = NewSong(4);
        failed.EnrichmentStatus = EnrichmentStatus.Failed;

        db.Songs.AddRange(built1, built2, review, failed);
        db.SaveChanges();

        // One album-cover write into the destination library.
        db.LibraryWriteEvents.Add(new LibraryWriteEvent
        {
            OwnerUserId = OwnerA,
            Kind = LibraryWriteEventKind.AlbumCoverWritten,
            WrittenAtUtc = DateTime.UtcNow,
            AlbumFolder = "/dest/Artist A/Album 1",
            Album = "Album 1",
            AlbumArtist = "Artist A",
            FieldName = "Cover",
            NewValue = "written",
        });

        // Spotify Liked Songs → wishlist: one already in the library, one pending, one already owned.
        var source = new WishlistSource { OwnerUserId = OwnerA, SourceType = WishlistSourceType.LikedSongs, Name = "Liked Songs" };
        db.WishlistSources.Add(source);
        db.SaveChanges();

        db.WishlistItems.AddRange(
            new WishlistItem
            {
                OwnerUserId = OwnerA,
                WishlistSource = source,
                SpotifyTrackId = "t1",
                Title = "Track 1",
                Artist = "Artist A",
                Status = WishlistItemStatus.Downloaded,
                DownloadedSong = built1,
            },
            new WishlistItem
            {
                OwnerUserId = OwnerA,
                WishlistSource = source,
                SpotifyTrackId = "t2",
                Title = "Track 2",
                Artist = "Artist A",
                Status = WishlistItemStatus.Pending,
            },
            new WishlistItem
            {
                OwnerUserId = OwnerA,
                WishlistSource = source,
                SpotifyTrackId = "t3",
                Title = "Track 3",
                Artist = "Artist A",
                Status = WishlistItemStatus.SkippedOwned,
            });
        db.SaveChanges();

        var json = Json(await DashboardEndpoints.GetInsights(db));

        // Stat 1 — source → library.
        Assert.Equal(4, Int(json, "source", "indexed"));
        Assert.Equal(2, Int(json, "source", "inLibrary"));

        // Stat 2 — covers.
        Assert.Equal(1, Int(json, "covers", "albumCoversAdded"));
        Assert.Equal(1, Int(json, "covers", "builtWithCover"));

        // Stat 3 — lyrics.
        Assert.Equal(1, Int(json, "lyrics", "added"));
        Assert.Equal(1, Int(json, "lyrics", "builtWithLyrics"));

        // Stats 4 & 5 — Spotify liked → wishlist → library.
        Assert.Equal(3, Int(json, "wishlist", "liked", "total"));
        Assert.Equal(1, Int(json, "wishlist", "liked", "downloaded"));
        Assert.Equal(1, Int(json, "wishlist", "liked", "inLibrary"));
        Assert.Equal(1, Int(json, "wishlist", "liked", "skippedOwned"));

        // Pipeline funnel: Indexed 4 → Fingerprinted 3 → Matched 2 → In library 2.
        var funnel = json.GetProperty("funnel");
        Assert.Equal(4, funnel[0].GetProperty("count").GetInt32());
        Assert.Equal(3, funnel[1].GetProperty("count").GetInt32());
        Assert.Equal(2, funnel[2].GetProperty("count").GetInt32());
        Assert.Equal(2, funnel[3].GetProperty("count").GetInt32());

        // Enrichment quality distribution.
        var enrichment = json.GetProperty("quality").GetProperty("enrichment");
        Assert.Equal(2, enrichment[0].GetProperty("count").GetInt32()); // Matched
        Assert.Equal(1, enrichment[1].GetProperty("count").GetInt32()); // Needs review
        Assert.Equal(1, enrichment[2].GetProperty("count").GetInt32()); // Failed

        // Top artists / albums + distinct counts over the built library.
        Assert.Equal(1, Int(json, "totals", "distinctArtists"));
        Assert.Equal(1, Int(json, "totals", "distinctAlbums"));
        var topArtist = json.GetProperty("top").GetProperty("artists")[0];
        Assert.Equal("Artist A", topArtist.GetProperty("name").GetString());
        Assert.Equal(2, topArtist.GetProperty("tracks").GetInt32());
    }

    [Fact]
    public async Task GetInsights_ScopesToOwner()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;

        using (var seed = new MusicHoarderDbContext(options))
        {
            var a = NewSong(1, OwnerA);
            a.LibraryBuildStatus = LibraryBuildStatus.Done;
            a.DestinationPath = "/dest/a.mp3";
            seed.Songs.Add(a);
            seed.SaveChanges();
        }

        using var dbB = new MusicHoarderDbContext(options, new StubCurrentUser(OwnerB));
        var json = Json(await DashboardEndpoints.GetInsights(dbB));

        Assert.Equal(0, Int(json, "source", "indexed"));
        Assert.Equal(0, Int(json, "source", "inLibrary"));
    }

    private sealed class StubCurrentUser(Guid userId) : Api.Auth.ICurrentUserAccessor
    {
        public Api.Auth.CurrentUser? User { get; } = new(userId, "owner@test", Api.Auth.UserRole.Owner, "Owner");
        public Guid UserId => userId;
    }
}
