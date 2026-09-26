using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Library;

/// <summary>
/// "Why is this in my library": the reason each track gets, and the chain from an album fill back to
/// the owned track that started it — the one link nothing stores.
/// </summary>
public class SongProvenanceTests
{
    private const string DownloadDir = "/data/downloads";
    private const string SyncedDir = "/data/synced";

    [Theory]
    [InlineData(SongOriginKind.Scanned, SongOriginSource.None, ProvenanceReason.LocalFile)]
    // Liked on Spotify but already on disk: the like found the file, it did not bring it.
    [InlineData(SongOriginKind.Scanned, SongOriginSource.SpotifyLiked, ProvenanceReason.LocalFile)]
    [InlineData(SongOriginKind.Synced, SongOriginSource.None, ProvenanceReason.Synced)]
    [InlineData(SongOriginKind.Downloaded, SongOriginSource.SpotifyLiked, ProvenanceReason.SpotifyLiked)]
    [InlineData(SongOriginKind.Downloaded, SongOriginSource.SpotifyPlaylist, ProvenanceReason.SpotifyPlaylist)]
    [InlineData(SongOriginKind.Downloaded, SongOriginSource.DeezerPlaylist, ProvenanceReason.DeezerPlaylist)]
    [InlineData(SongOriginKind.Downloaded, SongOriginSource.YouTubePlaylist, ProvenanceReason.YouTubePlaylist)]
    [InlineData(SongOriginKind.Downloaded, SongOriginSource.DirectUrl, ProvenanceReason.Link)]
    [InlineData(SongOriginKind.Downloaded, SongOriginSource.AlbumCompletion, ProvenanceReason.AlbumFill)]
    [InlineData(SongOriginKind.Downloaded, SongOriginSource.None, ProvenanceReason.Downloaded)]
    public void ReasonFor_ExplicitSongs(SongOriginKind kind, SongOriginSource source, ProvenanceReason expected)
    {
        var reason = SongProvenanceService.ReasonFor(
            SongAcquisitionIntent.Explicit, new SongOrigin(kind, source, null, null));

        Assert.Equal(expected, reason);
    }

    [Fact]
    public void ReasonFor_AlbumFillIntentWinsEvenWhenTheWishlistLinkIsGone()
    {
        var reason = SongProvenanceService.ReasonFor(
            SongAcquisitionIntent.AlbumFill,
            new SongOrigin(SongOriginKind.Downloaded, SongOriginSource.None, null, null));

        Assert.Equal(ProvenanceReason.AlbumFill, reason);
    }

    [Fact]
    public async Task Build_GroupsByCollectionAndNamesThePlaylist()
    {
        var addedToPlaylist = new DateTime(2025, 3, 3, 0, 0, 0, DateTimeKind.Utc);
        await using var db = NewContext();
        var local = Song("/music/Daft Punk/Discovery/01 One More Time.flac", "One More Time");
        var fromPlaylist = Song($"{DownloadDir}/02 Aerodynamic.opus", "Aerodynamic");
        db.Songs.AddRange(local, fromPlaylist);
        await db.SaveChangesAsync();
        AddPlaylistItem(db, fromPlaylist.Id, "Late Night", addedToPlaylist);
        await db.SaveChangesAsync();

        var result = await Build(db, local.Id, fromPlaylist.Id);

        Assert.Equal(2, result.Groups.Count);
        var playlist = result.Groups.Single(g => g.Reason == nameof(ProvenanceReason.SpotifyPlaylist));
        Assert.Equal("From Spotify playlist “Late Night”", playlist.Label);
        var track = Assert.Single(playlist.Tracks);
        Assert.Equal(addedToPlaylist, track.AtUtc);
        Assert.Equal("Added to the playlist", track.AtLabel);

        var folder = result.Groups.Single(g => g.Reason == nameof(ProvenanceReason.LocalFile));
        Assert.Equal("Found", Assert.Single(folder.Tracks).AtLabel);
        Assert.Equal("From your music folder + 1 more", result.Summary);
    }

    [Fact]
    public async Task Build_AlbumFill_NamesTheOwnedTrackItStartedFrom()
    {
        await using var db = NewContext();
        var seed = Song($"{DownloadDir}/01 One More Time.opus", "One More Time");
        var filledA = Song($"{DownloadDir}/02 Aerodynamic.opus", "Aerodynamic", SongAcquisitionIntent.AlbumFill);
        var filledB = Song($"{DownloadDir}/03 Digital Love.opus", "Digital Love", SongAcquisitionIntent.AlbumFill);
        db.Songs.AddRange(seed, filledA, filledB);
        await db.SaveChangesAsync();

        AddPlaylistItem(db, seed.Id, "Late Night", DateTime.UtcNow);
        var album = AddCanonicalAlbum(db);
        await db.SaveChangesAsync();
        var queuedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        AddFillItem(db, filledA, album.Id, queuedAt);
        AddFillItem(db, filledB, album.Id, queuedAt.AddMinutes(1));
        await db.SaveChangesAsync();

        var result = await Build(db, seed.Id, filledA.Id, filledB.Id);

        Assert.Equal("From Spotify playlist “Late Night” · 2 filled in", result.Summary);
        Assert.Equal(nameof(ProvenanceReason.SpotifyPlaylist), result.PrimaryReason);

        var fill = result.Groups.Single(g => g.Reason == nameof(ProvenanceReason.AlbumFill));
        Assert.Equal(2, fill.Tracks.Count);
        Assert.NotNull(fill.Fill);
        Assert.Equal("Discovery", fill.Fill!.Album);
        Assert.Equal(queuedAt, fill.Fill.QueuedAtUtc);
        var started = Assert.Single(fill.Fill.Seeds);
        Assert.Equal(seed.Id, started.SongId);
        Assert.Equal("From Spotify playlist “Late Night”", started.Label);
        Assert.Contains("You had “One More Time” from “Discovery”", fill.Explanation);
    }

    [Fact]
    public async Task Build_AlbumFill_FindsTheSeedWhenOnlyTheFilledTracksWereAskedAbout()
    {
        // The fill tracks can land under a different album name than the one completion was filling in
        // (enrichment matched them to another release), so the page listing them may not hold the seed.
        await using var db = NewContext();
        var seed = Song("/music/Daft Punk/Discovery/01 One More Time.flac", "One More Time");
        var filled = Song($"{DownloadDir}/02 Aerodynamic.opus", "Aerodynamic", SongAcquisitionIntent.AlbumFill);
        filled.Album = "Discovery (Deluxe)";
        db.Songs.AddRange(seed, filled);
        await db.SaveChangesAsync();
        var album = AddCanonicalAlbum(db);
        await db.SaveChangesAsync();
        AddFillItem(db, filled, album.Id, DateTime.UtcNow);
        await db.SaveChangesAsync();

        var result = await Build(db, filled.Id);

        Assert.Equal("Filled in by album completion", result.Summary);
        var fill = Assert.Single(result.Groups);
        var started = Assert.Single(fill.Fill!.Seeds);
        Assert.Equal(seed.Id, started.SongId);
        Assert.Equal("From your music folder", started.Label);
    }

    [Fact]
    public async Task Build_AlbumFill_KeepsADeletedSeedButPrefersALiveOne()
    {
        await using var db = NewContext();
        var gone = Song("/music/Daft Punk/Discovery/01 One More Time.flac", "One More Time");
        gone.AcquiredAtUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        gone.SoftDelete();
        var live = Song("/music/Daft Punk/Discovery/04 Harder Better.flac", "Harder, Better, Faster, Stronger");
        live.AcquiredAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var filled = Song($"{DownloadDir}/02 Aerodynamic.opus", "Aerodynamic", SongAcquisitionIntent.AlbumFill);
        db.Songs.AddRange(gone, live, filled);
        await db.SaveChangesAsync();
        var album = AddCanonicalAlbum(db);
        await db.SaveChangesAsync();
        AddFillItem(db, filled, album.Id, DateTime.UtcNow);
        await db.SaveChangesAsync();

        var result = await Build(db, filled.Id);

        var seeds = Assert.Single(result.Groups).Fill!.Seeds;
        Assert.Equal([live.Id, gone.Id], seeds.Select(s => s.SongId));
        Assert.False(seeds[0].IsDeleted);
        Assert.True(seeds[1].IsDeleted);
    }

    [Fact]
    public async Task Build_AlbumFill_SaysSoWhenTheSeedCannotBeFound()
    {
        await using var db = NewContext();
        var filled = Song($"{DownloadDir}/02 Aerodynamic.opus", "Aerodynamic", SongAcquisitionIntent.AlbumFill);
        // Another fill track under the same key is not a seed: nobody asked for it either.
        var alsoFilled = Song($"{DownloadDir}/03 Digital Love.opus", "Digital Love", SongAcquisitionIntent.AlbumFill);
        db.Songs.AddRange(filled, alsoFilled);
        await db.SaveChangesAsync();
        var album = AddCanonicalAlbum(db);
        await db.SaveChangesAsync();
        AddFillItem(db, filled, album.Id, DateTime.UtcNow);
        await db.SaveChangesAsync();

        var result = await Build(db, filled.Id);

        var fill = Assert.Single(result.Groups);
        Assert.Empty(fill.Fill!.Seeds);
        Assert.Contains("no longer in your library", fill.Explanation);
    }

    [Fact]
    public async Task Build_AlbumFillWithoutAWishlistLink_StillExplainsItself()
    {
        await using var db = NewContext();
        var filled = Song($"{DownloadDir}/02 Aerodynamic.opus", "Aerodynamic", SongAcquisitionIntent.AlbumFill);
        db.Songs.Add(filled);
        await db.SaveChangesAsync();

        var result = await Build(db, filled.Id);

        var fill = Assert.Single(result.Groups);
        Assert.Equal(nameof(ProvenanceReason.AlbumFill), fill.Reason);
        Assert.Null(fill.Fill);
    }

    [Fact]
    public async Task Build_IgnoresIdsThatMatchNothing()
    {
        await using var db = NewContext();

        var result = await Build(db, 404);

        Assert.Null(result.Summary);
        Assert.Empty(result.Groups);
    }

    private static Task<SongProvenanceResponse> Build(MusicHoarderDbContext db, params int[] ids) =>
        SongProvenanceService.BuildAsync(db, ids, DownloadDir, SyncedDir, CancellationToken.None);

    private static CanonicalAlbum AddCanonicalAlbum(MusicHoarderDbContext db)
    {
        var album = new CanonicalAlbum
        {
            ArtistKey = "daft punk",
            AlbumKey = "discovery",
            DisplayArtist = "Daft Punk",
            DisplayTitle = "Discovery",
            Status = CanonicalAlbumStatus.Fetched,
        };
        db.CanonicalAlbums.Add(album);
        return album;
    }

    private static void AddPlaylistItem(MusicHoarderDbContext db, int songId, string playlist, DateTime addedAt)
    {
        var source = new WishlistSource
        {
            OwnerUserId = WellKnownUsers.OwnerId,
            SourceType = WishlistSourceType.Playlist,
            Name = playlist,
            SpotifyPlaylistId = $"pl-{songId}",
        };
        db.WishlistSources.Add(source);
        db.WishlistItems.Add(new WishlistItem
        {
            OwnerUserId = WellKnownUsers.OwnerId,
            WishlistSource = source,
            Title = "x",
            Artist = "Daft Punk",
            SpotifyAddedAtUtc = addedAt,
            Status = WishlistItemStatus.Downloaded,
            DownloadedSongId = songId,
        });
    }

    private static void AddFillItem(MusicHoarderDbContext db, SongMetadata song, int canonicalAlbumId, DateTime createdAt) =>
        db.WishlistItems.Add(new WishlistItem
        {
            OwnerUserId = WellKnownUsers.OwnerId,
            Origin = WishlistItemOrigin.AlbumCompletion,
            CanonicalAlbumId = canonicalAlbumId,
            Title = song.Title ?? "",
            Artist = "Daft Punk",
            Album = "Discovery",
            Status = WishlistItemStatus.Downloaded,
            DownloadedSongId = song.Id,
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = createdAt,
        });

    private static SongMetadata Song(
        string sourcePath, string title, SongAcquisitionIntent intent = SongAcquisitionIntent.Explicit) => new()
    {
        OwnerUserId = WellKnownUsers.OwnerId,
        SourcePath = sourcePath,
        FileName = Path.GetFileName(sourcePath),
        Extension = Path.GetExtension(sourcePath),
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Title = title,
        Artist = "Daft Punk",
        AlbumArtist = "Daft Punk",
        Album = "Discovery",
        AcquisitionIntent = intent,
    };

    private static MusicHoarderDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }
}
