using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Deezer;
using static MusicHoarder.Api.Tests.Artists.ArtistTestKit;

namespace MusicHoarder.Api.Tests.Artists;

public class ArtistReleaseQueuerTests
{
    [Fact]
    public async Task Queue_QueuesTheTracksTheOwnerIsMissing()
    {
        await using var db = NewContext();
        db.Songs.Add(Song("One More Time", "Discovery", track: 1));
        await db.SaveChangesAsync();
        var deezer = new FakeDeezerCatalogService();
        deezer.Albums["d"] = Album("d", "Discovery", "Daft Punk", "One More Time", "Aerodynamic", "Digital Love");

        var result = await Queuer(db, deezer).QueueAsync(
            Owner, "Daft Punk", ["d"], ArtistReleaseKinds.Album, WishlistItemOrigin.ArtistRequest, 100, true, default);

        Assert.Null(result.Error);
        Assert.Equal((2, 1, 0), (result.Queued, result.AlreadyOwned, result.AlreadyQueued));
        var items = await db.WishlistItems.IgnoreQueryFilters().OrderBy(w => w.Title).ToListAsync();
        Assert.Equal(["Aerodynamic", "Digital Love"], items.Select(i => i.Title));
        Assert.All(items, i =>
        {
            Assert.Equal(WishlistItemOrigin.ArtistRequest, i.Origin);
            Assert.Equal(result.CanonicalAlbumId, i.CanonicalAlbumId);
            Assert.Equal("Discovery", i.Album);
            Assert.Equal(WishlistItemStatus.Pending, i.Status);
            Assert.NotNull(i.DeezerTrackId);
        });

        // The tracklist is now the album's canonical one, under the key the library groups on.
        var canonical = await db.CanonicalAlbums.Include(a => a.Tracks).SingleAsync();
        Assert.Equal(("daft punk", "discovery", CanonicalAlbumStatus.Fetched, 3),
            (canonical.ArtistKey, canonical.AlbumKey, canonical.Status, canonical.Tracks.Count));
    }

    [Fact]
    public async Task Queue_TakesTheEditionWithTheMostTracks()
    {
        await using var db = NewContext();
        var deezer = new FakeDeezerCatalogService();
        deezer.Albums["std"] = Album("std", "Discovery", "Daft Punk", "One", "Two");
        deezer.Albums["dlx"] = Album("dlx", "Discovery (Deluxe)", "Daft Punk", "One", "Two", "Bonus");

        var result = await Queuer(db, deezer).QueueAsync(
            Owner, "Daft Punk", ["std", "dlx"], ArtistReleaseKinds.Album, WishlistItemOrigin.ArtistRequest, 100, true, default);

        Assert.Equal(3, result.Queued);
    }

    [Fact]
    public async Task Queue_ACompilationTrackKeepsItsOwnArtist()
    {
        // A canonical track has no artist of its own; searching every track under the album artist is
        // what album completion refuses compilations for. A release asked for by id carries per-track artists.
        await using var db = NewContext();
        var deezer = new FakeDeezerCatalogService();
        deezer.Albums["c"] = new DeezerAlbumDetail("c", "Club Hits", "Various Artists", 2020, null,
        [
            new DeezerAlbumTrackItem(1, 1, "Song A", 180_000, "c1", "Artist A"),
            new DeezerAlbumTrackItem(1, 2, "Song B", 180_000, "c2", "Artist B"),
        ]);

        await Queuer(db, deezer).QueueAsync(
            Owner, "Artist A", ["c"], ArtistReleaseKinds.Compilation, WishlistItemOrigin.ArtistRequest, 100, true, default);

        var items = await db.WishlistItems.IgnoreQueryFilters().OrderBy(w => w.Title).ToListAsync();
        Assert.Equal(["Artist A", "Artist B"], items.Select(i => i.Artist));
    }

    [Fact]
    public async Task Queue_ASingleIsOwnedWhenItsSongIsOnAnotherAlbum()
    {
        await using var db = NewContext();
        db.Songs.Add(Song("Get Lucky", "Random Access Memories"));
        await db.SaveChangesAsync();
        var deezer = new FakeDeezerCatalogService();
        deezer.Albums["s"] = Album("s", "Get Lucky", "Daft Punk", "Get Lucky", "Get Lucky (Radio Edit)");

        var result = await Queuer(db, deezer).QueueAsync(
            Owner, "Daft Punk", ["s"], ArtistReleaseKinds.Single, WishlistItemOrigin.ArtistMonitor, 100, false, default);

        // The radio edit normalizes to the same title — both are the song the owner already has.
        Assert.Equal((0, 2), (result.Queued, result.AlreadyOwned));
    }

    [Fact]
    public async Task Queue_NeverQueuesTheSameRecordingTwice()
    {
        await using var db = NewContext();
        db.WishlistItems.Add(new WishlistItem
        {
            OwnerUserId = Owner, Title = "Aerodynamic", Artist = "Daft Punk", Status = WishlistItemStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var deezer = new FakeDeezerCatalogService();
        deezer.Albums["d"] = Album("d", "Discovery", "Daft Punk", "Aerodynamic", "Digital Love");
        var queuer = Queuer(db, deezer);

        var first = await queuer.QueueAsync(
            Owner, "Daft Punk", ["d"], ArtistReleaseKinds.Album, WishlistItemOrigin.ArtistRequest, 100, true, default);
        var again = await queuer.QueueAsync(
            Owner, "Daft Punk", ["d"], ArtistReleaseKinds.Album, WishlistItemOrigin.ArtistRequest, 100, true, default);

        Assert.Equal((1, 1), (first.Queued, first.AlreadyQueued));
        Assert.Equal((0, 2), (again.Queued, again.AlreadyQueued));
        Assert.Equal(2, await db.WishlistItems.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Queue_AnExplicitRequestRetriesTheReleasesFailedTracks()
    {
        await using var db = NewContext();
        var deezer = new FakeDeezerCatalogService();
        deezer.Albums["d"] = Album("d", "Discovery", "Daft Punk", "Aerodynamic");
        var queuer = Queuer(db, deezer);
        await queuer.QueueAsync(Owner, "Daft Punk", ["d"], ArtistReleaseKinds.Album, WishlistItemOrigin.ArtistRequest, 100, true, default);
        var item = await db.WishlistItems.IgnoreQueryFilters().SingleAsync();
        item.Status = WishlistItemStatus.NotFound;
        await db.SaveChangesAsync();

        var monitorPass = await queuer.QueueAsync(
            Owner, "Daft Punk", ["d"], ArtistReleaseKinds.Album, WishlistItemOrigin.ArtistMonitor, 100, false, default);
        Assert.Equal(0, monitorPass.Requeued);

        var request = await queuer.QueueAsync(
            Owner, "Daft Punk", ["d"], ArtistReleaseKinds.Album, WishlistItemOrigin.ArtistRequest, 100, true, default);
        Assert.Equal(1, request.Requeued);
        Assert.Equal(WishlistItemStatus.Pending, (await db.WishlistItems.IgnoreQueryFilters().SingleAsync()).Status);
    }

    [Fact]
    public async Task Queue_StopsAtTheTrackBudget()
    {
        await using var db = NewContext();
        var deezer = new FakeDeezerCatalogService();
        deezer.Albums["d"] = Album("d", "Discovery", "Daft Punk", "One", "Two", "Three", "Four");

        var result = await Queuer(db, deezer).QueueAsync(
            Owner, "Daft Punk", ["d"], ArtistReleaseKinds.Album, WishlistItemOrigin.ArtistRequest, 3, true, default);

        Assert.Equal((3, 1), (result.Queued, result.Capped));
    }

    [Fact]
    public async Task Queue_KeepsABiggerReconciledTracklist()
    {
        await using var db = NewContext();
        var existing = new CanonicalAlbum
        {
            ArtistKey = "daft punk", AlbumKey = "discovery", DisplayTitle = "Discovery", DisplayArtist = "Daft Punk",
            Status = CanonicalAlbumStatus.Fetched, ResolvedTrackCount = 3,
        };
        foreach (var (title, n) in new[] { ("One", 1), ("Two", 2), ("Three", 3) })
            existing.Tracks.Add(new CanonicalAlbumTrack { TrackNumber = n, Title = title });
        db.CanonicalAlbums.Add(existing);
        await db.SaveChangesAsync();
        var deezer = new FakeDeezerCatalogService();
        deezer.Albums["d"] = Album("d", "Discovery", "Daft Punk", "One", "Two");

        var result = await Queuer(db, deezer).QueueAsync(
            Owner, "Daft Punk", ["d"], ArtistReleaseKinds.Album, WishlistItemOrigin.ArtistRequest, 100, true, default);

        Assert.Equal(existing.Id, result.CanonicalAlbumId);
        Assert.Equal(3, result.Queued);
    }

    [Fact]
    public async Task Queue_AnUnreadableReleaseIsAnError()
    {
        await using var db = NewContext();
        var result = await Queuer(db, new FakeDeezerCatalogService()).QueueAsync(
            Owner, "Daft Punk", ["missing"], ArtistReleaseKinds.Album, WishlistItemOrigin.ArtistRequest, 100, true, default);

        Assert.NotNull(result.Error);
        Assert.Empty(await db.WishlistItems.IgnoreQueryFilters().ToListAsync());
    }
}
