using System.Collections;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>
/// Provenance shown in the library's Source column. There is no stored origin column: the path root
/// says how the file arrived and the wishlist link says why, so these cover both halves plus the
/// Spotify save date the "Spotify only" filter orders on.
/// </summary>
public class SongOriginTests
{
    private const string DownloadDir = "/data/downloads";
    private const string SyncedDir = "/data/synced";

    [Theory]
    [InlineData("/music/Artist/track.flac", nameof(SongOriginKind.Scanned))]
    [InlineData("/data/downloads/track.opus", nameof(SongOriginKind.Downloaded))]
    [InlineData("/data/synced/Artist/track.flac", nameof(SongOriginKind.Synced))]
    // A sibling directory that merely shares a prefix is not "under" the root.
    [InlineData("/data/downloads-old/track.opus", nameof(SongOriginKind.Scanned))]
    public void Resolve_ClassifiesByPathRoot(string sourcePath, string expected)
    {
        var origin = SongOriginResolver.Resolve(sourcePath, link: null, DownloadDir, SyncedDir);

        Assert.Equal(expected, origin.Kind.ToString());
        Assert.Equal(SongOriginSource.None, origin.Source);
    }

    [Fact]
    public void Resolve_NamesTheCollectionThatAskedForTheTrack()
    {
        var saved = new DateTime(2025, 4, 1, 12, 0, 0, DateTimeKind.Utc);
        var link = new WishlistLink(WishlistSourceType.Playlist, "Late Night", SourceUrl: null, saved);

        var origin = SongOriginResolver.Resolve($"{DownloadDir}/x.opus", link, DownloadDir, SyncedDir);

        Assert.Equal(SongOriginKind.Downloaded, origin.Kind);
        Assert.Equal(SongOriginSource.SpotifyPlaylist, origin.Source);
        Assert.Equal("Late Night", origin.Detail);
        Assert.Equal(saved, origin.SpotifyAddedAtUtc);
    }

    [Fact]
    public void Resolve_TreatsASourcelessItemAsADirectUrlAdd()
    {
        var link = new WishlistLink(SourceType: null, SourceName: null, "https://www.youtube.com/watch?v=abc", null);

        var origin = SongOriginResolver.Resolve($"{DownloadDir}/x.opus", link, DownloadDir, SyncedDir);

        Assert.Equal(SongOriginSource.DirectUrl, origin.Source);
        Assert.Equal("youtube.com", origin.Detail);
    }

    [Fact]
    public void Best_PrefersLikedSongsOverAPlaylistForTheSaveDate()
    {
        var liked = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var playlisted = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var best = SongOriginResolver.Best([
            new WishlistLink(WishlistSourceType.Playlist, "Gym", null, playlisted),
            new WishlistLink(WishlistSourceType.LikedSongs, "Liked Songs", null, liked),
        ]);

        Assert.Equal(WishlistSourceType.LikedSongs, best.SourceType);
        Assert.Equal(liked, best.SpotifyAddedAtUtc);
    }

    [Fact]
    public void Resolve_AlbumCompletionItem_ReportsTheAlbumItWasFillingIn()
    {
        var link = new WishlistLink(
            SourceType: null, SourceName: null, SourceUrl: null, SpotifyAddedAtUtc: null,
            Origin: WishlistItemOrigin.AlbumCompletion, Album: "Discovery");

        var origin = SongOriginResolver.Resolve($"{DownloadDir}/03 Aerodynamic.opus", link, DownloadDir, null);

        Assert.Equal(SongOriginKind.Downloaded, origin.Kind);
        Assert.Equal(SongOriginSource.AlbumCompletion, origin.Source);
        Assert.Equal("Discovery", origin.Detail);
    }

    [Fact]
    public void Best_RanksAlbumCompletionBelowEverythingElse()
    {
        // If anything asked for this track by name, that is the more interesting answer to "where did
        // this come from" than "it came along with an album".
        var liked = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var best = SongOriginResolver.Best([
            new WishlistLink(null, null, null, null, WishlistItemOrigin.AlbumCompletion, "Discovery"),
            new WishlistLink(WishlistSourceType.LikedSongs, "Liked Songs", null, liked),
        ]);

        Assert.Equal(WishlistSourceType.LikedSongs, best.SourceType);
    }

    [Fact]
    public void Best_AlbumCompletionStillWinsWhenItIsTheOnlyLink()
    {
        var best = SongOriginResolver.Best([
            new WishlistLink(null, null, null, null, WishlistItemOrigin.AlbumCompletion, "Discovery"),
        ]);

        Assert.Equal(WishlistItemOrigin.AlbumCompletion, best.Origin);
    }

    [Fact]
    public async Task ListSongs_ExposesOriginAndSpotifySaveDate()
    {
        var saved = new DateTime(2024, 9, 9, 8, 7, 6, DateTimeKind.Utc);

        await using var db = NewContext();
        var downloaded = NewSong($"{DownloadDir}/01 Gorgeous.opus");
        var owned = NewSong("/music/Artist/02 Otis.flac");
        db.Songs.AddRange(downloaded, owned);
        await db.SaveChangesAsync();

        db.WishlistSources.Add(new WishlistSource
        {
            Id = 1,
            OwnerUserId = WellKnownUsers.OwnerId,
            SourceType = WishlistSourceType.LikedSongs,
            Name = "Liked Songs",
        });
        db.WishlistItems.Add(new WishlistItem
        {
            OwnerUserId = WellKnownUsers.OwnerId,
            WishlistSourceId = 1,
            Title = "Gorgeous",
            Artist = "Kanye West",
            SpotifyAddedAtUtc = saved,
            Status = WishlistItemStatus.Downloaded,
            DownloadedSongId = downloaded.Id,
        });
        await db.SaveChangesAsync();

        var result = await ListSongsCaller.Invoke(db, DownloadDir, SyncedDir);
        var rows = Songs(result).ToList();

        var fromSpotify = rows.Single(r => GetProperty<int>(r, "Id") == downloaded.Id);
        Assert.Equal("Downloaded", GetProperty<string>(fromSpotify, "OriginKind"));
        Assert.Equal("SpotifyLiked", GetProperty<string>(fromSpotify, "OriginSource"));
        Assert.Equal(saved, GetProperty<DateTime?>(fromSpotify, "SpotifyAddedAtUtc"));

        var scanned = rows.Single(r => GetProperty<int>(r, "Id") == owned.Id);
        Assert.Equal("Scanned", GetProperty<string>(scanned, "OriginKind"));
        Assert.Equal("None", GetProperty<string>(scanned, "OriginSource"));
        Assert.Null(GetProperty<DateTime?>(scanned, "SpotifyAddedAtUtc"));
    }

    [Fact]
    public async Task ListSongs_ReportsTheSpotifyDateForATrackTheWishlistFoundAlreadyOwned()
    {
        // SkippedOwned items still link the song — that's how a track that was never downloaded still
        // answers "you liked this on Spotify, on this date".
        var saved = new DateTime(2022, 2, 2, 0, 0, 0, DateTimeKind.Utc);

        await using var db = NewContext();
        var owned = NewSong("/music/Artist/track.flac");
        db.Songs.Add(owned);
        await db.SaveChangesAsync();

        db.WishlistSources.Add(new WishlistSource
        {
            Id = 1,
            OwnerUserId = WellKnownUsers.OwnerId,
            SourceType = WishlistSourceType.LikedSongs,
            Name = "Liked Songs",
        });
        db.WishlistItems.Add(new WishlistItem
        {
            OwnerUserId = WellKnownUsers.OwnerId,
            WishlistSourceId = 1,
            Title = "Track",
            Artist = "Artist",
            SpotifyAddedAtUtc = saved,
            Status = WishlistItemStatus.SkippedOwned,
            DownloadedSongId = owned.Id,
        });
        await db.SaveChangesAsync();

        var result = await ListSongsCaller.Invoke(db, DownloadDir, SyncedDir);
        var row = Songs(result).Single();

        Assert.Equal("Scanned", GetProperty<string>(row, "OriginKind"));   // the file was always here
        Assert.Equal("SpotifyLiked", GetProperty<string>(row, "OriginSource"));
        Assert.Equal(saved, GetProperty<DateTime?>(row, "SpotifyAddedAtUtc"));
    }

    [Fact]
    public async Task ListSongs_FallsBackToTheLikedSyncMatchCacheForTheSaveDate()
    {
        // The auto-like sweep hearts library songs the wishlist never touched (already-owned files,
        // or a like that matched a different file than the wishlist's download). Those songs have no
        // wishlist link, but the liked_sync match cache still knows Spotify's real save date — without
        // it the liked sort would fall back to LikedAtUtc, which is the sweep time, not the like.
        var saved = new DateTime(2024, 4, 10, 11, 15, 12, DateTimeKind.Utc);

        await using var db = NewContext();
        var owned = NewSong("/music/Artist/track.flac");
        owned.LikedAtUtc = DateTime.UtcNow;
        db.Songs.Add(owned);
        await db.SaveChangesAsync();

        db.SpotifyTrackLibraryMatches.Add(NewMatch("sp-1", owned.Id, saved, SpotifyLibraryComparisonService.SourceLikedSync));
        await db.SaveChangesAsync();

        var result = await ListSongsCaller.Invoke(db, DownloadDir, SyncedDir);
        var row = Songs(result).Single();

        Assert.Equal(saved, GetProperty<DateTime?>(row, "SpotifyAddedAtUtc"));
    }

    [Fact]
    public async Task ListSongs_TakesTheEarliestOfWishlistAndMatchCacheSaveDates()
    {
        var wishlistDate = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var likedDate = new DateTime(2023, 3, 3, 0, 0, 0, DateTimeKind.Utc);

        await using var db = NewContext();
        var song = NewSong($"{DownloadDir}/track.opus");
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        db.WishlistSources.Add(new WishlistSource
        {
            Id = 1,
            OwnerUserId = WellKnownUsers.OwnerId,
            SourceType = WishlistSourceType.LikedSongs,
            Name = "Liked Songs",
        });
        db.WishlistItems.Add(new WishlistItem
        {
            OwnerUserId = WellKnownUsers.OwnerId,
            WishlistSourceId = 1,
            Title = "Track",
            Artist = "Artist",
            SpotifyAddedAtUtc = wishlistDate,
            Status = WishlistItemStatus.Downloaded,
            DownloadedSongId = song.Id,
        });
        db.SpotifyTrackLibraryMatches.Add(NewMatch("sp-2", song.Id, likedDate, SpotifyLibraryComparisonService.SourceLikedSync));
        await db.SaveChangesAsync();

        var result = await ListSongsCaller.Invoke(db, DownloadDir, SyncedDir);
        var row = Songs(result).Single();

        Assert.Equal(likedDate, GetProperty<DateTime?>(row, "SpotifyAddedAtUtc"));
    }

    [Fact]
    public async Task ListSongs_MatchesTheSaveDateBySpotifyIdWhenTheCachePointsAtAnotherCopy()
    {
        // Duplicate copies of one track: sweeps re-point MatchedSongId between them, so the copy that
        // was auto-liked earlier can lose its row. The song's enriched SpotifyId still identifies the
        // liked track, so the save date sticks to every copy.
        var saved = new DateTime(2023, 7, 7, 0, 0, 0, DateTimeKind.Utc);

        await using var db = NewContext();
        var likedCopy = NewSong("/music/Artist/track.flac");
        likedCopy.SpotifyId = "sp-track";
        likedCopy.LikedAtUtc = DateTime.UtcNow;
        var otherCopy = NewSong("/music/Artist/track (1).flac");
        db.Songs.AddRange(likedCopy, otherCopy);
        await db.SaveChangesAsync();

        // The cache row points at the OTHER copy — the liked one only matches via its SpotifyId.
        db.SpotifyTrackLibraryMatches.Add(NewMatch("sp-track", otherCopy.Id, saved, SpotifyLibraryComparisonService.SourceLikedSync));
        await db.SaveChangesAsync();

        var result = await ListSongsCaller.Invoke(db, DownloadDir, SyncedDir);
        var rows = Songs(result).ToList();

        var liked = rows.Single(r => GetProperty<int>(r, "Id") == likedCopy.Id);
        Assert.Equal(saved, GetProperty<DateTime?>(liked, "SpotifyAddedAtUtc"));
    }

    [Fact]
    public async Task ListSongs_IgnoresNonLikedSyncMatchRowsForTheSaveDate()
    {
        // api_page rows are written from playlist views; their added-at is a playlist date, not a like.
        await using var db = NewContext();
        var song = NewSong("/music/Artist/track.flac");
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        db.SpotifyTrackLibraryMatches.Add(NewMatch(
            "sp-3", song.Id, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), SpotifyLibraryComparisonService.SourceApiPage));
        await db.SaveChangesAsync();

        var result = await ListSongsCaller.Invoke(db, DownloadDir, SyncedDir);
        var row = Songs(result).Single();

        Assert.Null(GetProperty<DateTime?>(row, "SpotifyAddedAtUtc"));
    }

    private static SpotifyTrackLibraryMatch NewMatch(string spotifyId, int songId, DateTime addedAt, string source) => new()
    {
        OwnerUserId = WellKnownUsers.OwnerId,
        SpotifyTrackId = spotifyId,
        MatchStatus = 0,
        MatchedSongId = songId,
        SpotifyAddedAtUtc = addedAt,
        Source = source,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    private static IEnumerable<object> Songs(IResult result)
    {
        var value = result.GetType().GetProperty("Value")!.GetValue(result)!;
        var songs = (IEnumerable)value.GetType().GetProperty("Songs")!.GetValue(value)!;
        return songs.Cast<object>();
    }

    private static T GetProperty<T>(object obj, string name)
    {
        var prop = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Property '{name}' not found on {obj.GetType()}");
        return (T)prop.GetValue(obj)!;
    }

    private static SongMetadata NewSong(string sourcePath) => new()
    {
        OwnerUserId = WellKnownUsers.OwnerId,
        SourcePath = sourcePath,
        FileName = Path.GetFileName(sourcePath),
        Extension = Path.GetExtension(sourcePath),
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
    };

    private static MusicHoarderDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }
}
