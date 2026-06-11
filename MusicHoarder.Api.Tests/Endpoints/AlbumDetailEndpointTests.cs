using System.Collections;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;

namespace MusicHoarder.Api.Tests.Endpoints;

public class AlbumDetailEndpointTests
{
    private const string CurrentModel = "openai/gpt-4o-mini";

    [Fact]
    public async Task GetAlbumDetail_Linked_ReturnsTracklistAndGrade()
    {
        await using var db = NewContext();

        db.CanonicalAlbums.Add(new CanonicalAlbum
        {
            Id = 1,
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
        db.CanonicalAlbumQualityGrades.Add(Grade(1, SongQualityVerdict.Questionable, 55));
        await db.SaveChangesAsync();

        var result = await AlbumsEndpoints.GetAlbumDetail("Daft Punk", "Discovery", db, EnrichOpts(), GradeOpts(), CancellationToken.None);

        var value = Value(result);
        Assert.Equal("linked", GetProperty<string>(value, "status"));

        var tracklist = GetProperty<object>(value, "tracklist")!;
        Assert.Equal(3, GetProperty<int>(tracklist, "ownedCount"));
        Assert.Equal(4, GetProperty<int>(tracklist, "totalCount"));
        Assert.True(GetProperty<bool>(tracklist, "trackCountContested"));

        var tracks = ((IEnumerable)GetProperty<object>(tracklist, "tracks")!).Cast<object>().ToList();
        Assert.NotNull(GetProperty<int?>(tracks[0], "ownedSongId"));
        Assert.NotNull(GetProperty<int?>(tracks[1], "ownedSongId"));
        Assert.NotNull(GetProperty<int?>(tracks[2], "ownedSongId"));
        Assert.Null(GetProperty<int?>(tracks[3], "ownedSongId"));
        Assert.True(GetProperty<bool>(tracks[3], "isContested"));

        var sources = ((IEnumerable)GetProperty<object>(tracklist, "sources")!).Cast<object>().ToList();
        Assert.Equal(2, sources.Count);
        Assert.Equal("MusicBrainzWeb", GetProperty<string>(sources[0], "provider"));

        var grade = GetProperty<object>(value, "grade")!;
        Assert.True(GetProperty<bool>(grade, "graded"));
        Assert.Equal("Questionable", GetProperty<string>(grade, "verdict"));
        Assert.False(GetProperty<bool>(grade, "isOutdated"));
    }

    [Fact]
    public async Task GetAlbumDetail_LinkedButUngraded_ReturnsTracklistAndGradedFalse()
    {
        await using var db = NewContext();
        db.CanonicalAlbums.Add(new CanonicalAlbum
        {
            Id = 1,
            ArtistKey = "daft punk",
            AlbumKey = "discovery",
            DisplayTitle = "Discovery",
            DisplayArtist = "Daft Punk",
            Status = CanonicalAlbumStatus.Fetched,
            Tracks = [new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = 1, Title = "One More Time" }],
        });
        await db.SaveChangesAsync();

        var result = await AlbumsEndpoints.GetAlbumDetail("Daft Punk", "Discovery", db, EnrichOpts(), GradeOpts(), CancellationToken.None);

        var value = Value(result);
        Assert.Equal("linked", GetProperty<string>(value, "status"));
        Assert.NotNull(GetProperty<object>(value, "tracklist"));
        Assert.False(GetProperty<bool>(GetProperty<object>(value, "grade")!, "graded"));
    }

    [Fact]
    public async Task GetAlbumDetail_OutdatedGrade_FlagsIsOutdated()
    {
        await using var db = NewContext();
        db.CanonicalAlbums.Add(new CanonicalAlbum
        {
            Id = 1,
            ArtistKey = "daft punk",
            AlbumKey = "discovery",
            DisplayTitle = "Discovery",
            DisplayArtist = "Daft Punk",
            Status = CanonicalAlbumStatus.Fetched,
            Tracks = [new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = 1, Title = "One More Time" }],
        });
        db.CanonicalAlbumQualityGrades.Add(Grade(1, SongQualityVerdict.Good, 80, model: "old/model"));
        await db.SaveChangesAsync();

        var result = await AlbumsEndpoints.GetAlbumDetail("Daft Punk", "Discovery", db, EnrichOpts(), GradeOpts(), CancellationToken.None);

        var grade = GetProperty<object>(Value(result), "grade")!;
        Assert.True(GetProperty<bool>(grade, "graded"));
        Assert.True(GetProperty<bool>(grade, "isOutdated"));
    }

    [Fact]
    public async Task GetAlbumDetail_NoRow_ReturnsPending_NullTracklist_Ungraded()
    {
        await using var db = NewContext();
        var result = await AlbumsEndpoints.GetAlbumDetail("Nobody", "Nothing", db, EnrichOpts(), GradeOpts(), CancellationToken.None);

        var value = Value(result);
        Assert.Equal("pending", GetProperty<string>(value, "status"));
        Assert.Null(GetProperty<object>(value, "tracklist"));
        Assert.False(GetProperty<bool>(GetProperty<object>(value, "grade")!, "graded"));
    }

    [Fact]
    public async Task GetAlbumDetail_NotFoundRow_ReturnsLocalOnly_NullTracklist()
    {
        await using var db = NewContext();
        db.CanonicalAlbums.Add(new CanonicalAlbum
        {
            ArtistKey = "nobody", AlbumKey = "phantom album", Status = CanonicalAlbumStatus.NotFound,
        });
        await db.SaveChangesAsync();

        var result = await AlbumsEndpoints.GetAlbumDetail("Nobody", "Phantom Album", db, EnrichOpts(), GradeOpts(), CancellationToken.None);

        var value = Value(result);
        Assert.Equal("localOnly", GetProperty<string>(value, "status"));
        Assert.Null(GetProperty<object>(value, "tracklist"));
        Assert.False(GetProperty<bool>(GetProperty<object>(value, "grade")!, "graded"));
    }

    [Fact]
    public async Task GetAlbumDetail_BlankParams_Returns404()
    {
        await using var db = NewContext();
        var result = await AlbumsEndpoints.GetAlbumDetail("", "", db, EnrichOpts(), GradeOpts(), CancellationToken.None);
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

    private static IOptions<MusicEnricherOptions> EnrichOpts() => Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
    {
        SourceDirectory = "/source",
        DestinationDirectory = "/dest",
    });

    private static IOptionsMonitor<QualityGradingOptions> GradeOpts(string model = CurrentModel) =>
        new TestOptionsMonitor(new QualityGradingOptions { Model = model });

    private sealed class TestOptionsMonitor(QualityGradingOptions value) : IOptionsMonitor<QualityGradingOptions>
    {
        public QualityGradingOptions CurrentValue { get; } = value;
        public QualityGradingOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<QualityGradingOptions, string?> listener) => null;
    }

    private static CanonicalAlbumQualityGrade Grade(
        int albumId, SongQualityVerdict verdict, int score, DateTime? at = null,
        int? promptVersion = null, string? model = CurrentModel) => new()
    {
        CanonicalAlbumId = albumId,
        OwnerUserId = WellKnownUsers.OwnerId,
        Verdict = verdict,
        Score = score,
        PromptVersion = promptVersion ?? AlbumGradingPrompt.Version,
        Model = model,
        GradedAtUtc = at ?? DateTime.UtcNow,
    };

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
