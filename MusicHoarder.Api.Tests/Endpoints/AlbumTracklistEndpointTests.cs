using System.Collections;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Endpoints;

public class AlbumTracklistEndpointTests
{
    [Fact]
    public async Task GetAlbumTracklist_MatchesOwned_GreysMissing_AndReturnsSources()
    {
        await using var db = NewContext();

        db.CanonicalAlbums.Add(new CanonicalAlbum
        {
            ArtistKey = "daft punk",
            AlbumKey = "discovery",
            DisplayTitle = "Discovery",
            DisplayArtist = "Daft Punk",
            Year = 2001,
            Status = CanonicalAlbumStatus.Fetched,
            ResolvedTrackCount = 4,
            TrackCountContested = true,
            SourcesJson = """[{"Provider":2,"AlbumId":"rel-1","TrackCount":4,"InWinningCluster":true},{"Provider":4,"AlbumId":"dz-1","TrackCount":4,"InWinningCluster":true}]""",
            Tracks =
            [
                new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = 1, Title = "One More Time", MusicBrainzRecordingId = "rec-1", CorroborationCount = 2, CorroboratingProviders = "MusicBrainzWeb,Deezer" },
                new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = 2, Title = "Aerodynamic", CorroborationCount = 2 },
                new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = 3, Title = "Nightvision", CorroborationCount = 2 },
                new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = 4, Title = "Hidden Bonus", CorroborationCount = 1, IsContested = true },
            ],
        });

        // Owned: t1 by recording MBID, t2 by position, t3 by fuzzy title; t4 missing.
        db.Songs.Add(OwnedSong("/1.mp3", "Daft Punk", "Discovery", mbid: "rec-1", trackNumber: 99, title: "Different"));
        db.Songs.Add(OwnedSong("/2.mp3", "Daft Punk", "Discovery", trackNumber: 2, title: "Aerodynamic"));
        db.Songs.Add(OwnedSong("/3.mp3", "Daft Punk", "Discovery", title: "Night Vision"));
        await db.SaveChangesAsync();

        var result = await AlbumsEndpoints.GetAlbumTracklist("Daft Punk", "Discovery", db, Opts(), CancellationToken.None);

        var value = Value(result);
        Assert.Equal("linked", GetProperty<string>(value, "status"));
        Assert.Equal(3, GetProperty<int>(value, "ownedCount"));
        Assert.Equal(4, GetProperty<int>(value, "totalCount"));
        Assert.True(GetProperty<bool>(value, "trackCountContested"));

        var tracks = ((IEnumerable)GetProperty<object>(value, "tracks")!).Cast<object>().ToList();
        Assert.NotNull(GetProperty<int?>(tracks[0], "ownedSongId"));
        Assert.NotNull(GetProperty<int?>(tracks[1], "ownedSongId"));
        Assert.NotNull(GetProperty<int?>(tracks[2], "ownedSongId"));
        Assert.Null(GetProperty<int?>(tracks[3], "ownedSongId"));
        Assert.True(GetProperty<bool>(tracks[3], "isContested"));

        var sources = ((IEnumerable)GetProperty<object>(value, "sources")!).Cast<object>().ToList();
        Assert.Equal(2, sources.Count);
        Assert.Equal("MusicBrainzWeb", GetProperty<string>(sources[0], "provider"));
    }

    [Fact]
    public async Task GetAlbumTracklist_NoRow_ReturnsPendingStatus()
    {
        await using var db = NewContext();
        var result = await AlbumsEndpoints.GetAlbumTracklist("Nobody", "Nothing", db, Opts(), CancellationToken.None);
        Assert.Equal("pending", GetProperty<string>(Value(result), "status"));
    }

    [Fact]
    public async Task GetAlbumTracklist_NotFoundRow_ReturnsLocalOnlyStatus()
    {
        await using var db = NewContext();
        db.CanonicalAlbums.Add(new CanonicalAlbum
        {
            ArtistKey = "nobody", AlbumKey = "phantom album", Status = CanonicalAlbumStatus.NotFound,
        });
        await db.SaveChangesAsync();

        var result = await AlbumsEndpoints.GetAlbumTracklist("Nobody", "Phantom Album", db, Opts(), CancellationToken.None);
        Assert.Equal("localOnly", GetProperty<string>(Value(result), "status"));
    }

    [Fact]
    public async Task GetAlbumTracklist_BlankParams_Returns404()
    {
        await using var db = NewContext();
        var result = await AlbumsEndpoints.GetAlbumTracklist("", "", db, Opts(), CancellationToken.None);
        Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task GetCanonicalStatuses_ReturnsPerAlbumStatus_WithProviders()
    {
        await using var db = NewContext();
        db.CanonicalAlbums.Add(new CanonicalAlbum
        {
            ArtistKey = "daft punk", AlbumKey = "discovery", Status = CanonicalAlbumStatus.Fetched,
            SourcesJson = """[{"Provider":2,"AlbumId":"r","TrackCount":4,"InWinningCluster":true},{"Provider":4,"AlbumId":"d","TrackCount":4,"InWinningCluster":false}]""",
        });
        db.CanonicalAlbums.Add(new CanonicalAlbum
        {
            ArtistKey = "nobody", AlbumKey = "phantom album", Status = CanonicalAlbumStatus.NotFound,
        });
        await db.SaveChangesAsync();

        var request = new AlbumsEndpoints.CanonicalStatusRequest(
        [
            new AlbumsEndpoints.AlbumIdentity("Daft Punk", "Discovery"),
            new AlbumsEndpoints.AlbumIdentity("Nobody", "Phantom Album"),
            new AlbumsEndpoints.AlbumIdentity("Brand", "New Album"),
        ]);

        var result = await AlbumsEndpoints.GetCanonicalStatuses(request, db, CancellationToken.None);

        var items = ((IEnumerable)Value(result)).Cast<object>().ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal("linked", GetProperty<string>(items[0], "status"));
        Assert.Equal("localOnly", GetProperty<string>(items[1], "status"));
        Assert.Equal("pending", GetProperty<string>(items[2], "status"));
        // Only the winning-cluster provider is surfaced for the linked album.
        var providers = (string[])GetProperty<object>(items[0], "providers")!;
        Assert.Equal(["MusicBrainzWeb"], providers);
        Assert.Empty((string[])GetProperty<object>(items[1], "providers")!);
    }

    private static object Value(IResult result)
        => result.GetType().GetProperty("Value")!.GetValue(result)!;

    private static T GetProperty<T>(object obj, string name)
    {
        var prop = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Property '{name}' not found on {obj.GetType()}");
        return (T)prop.GetValue(obj)!;
    }

    private static IOptions<MusicEnricherOptions> Opts() => Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
    {
        SourceDirectory = "/source",
        DestinationDirectory = "/dest",
    });

    private static SongMetadata OwnedSong(
        string sourcePath, string albumArtist, string album, string? mbid = null, int? trackNumber = null, string? title = null) => new()
    {
        OwnerUserId = WellKnownUsers.OwnerId,
        SourcePath = sourcePath,
        FileName = Path.GetFileName(sourcePath),
        Extension = Path.GetExtension(sourcePath),
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        EnrichmentStatus = EnrichmentStatus.Matched,
        AlbumArtist = albumArtist,
        Artist = albumArtist,
        Album = album,
        MusicBrainzId = mbid,
        TrackNumber = trackNumber,
        Title = title,
    };

    private static MusicHoarderDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }
}
