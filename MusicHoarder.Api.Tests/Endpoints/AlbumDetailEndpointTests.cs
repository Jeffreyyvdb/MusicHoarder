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

        var result = await AlbumsEndpoints.GetAlbumDetail("Daft Punk", "Discovery", year: null, folder: null, db, EnrichOpts(), GradeOpts(), CancellationToken.None);

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

        var result = await AlbumsEndpoints.GetAlbumDetail("Daft Punk", "Discovery", year: null, folder: null, db, EnrichOpts(), GradeOpts(), CancellationToken.None);

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

        var result = await AlbumsEndpoints.GetAlbumDetail("Daft Punk", "Discovery", year: null, folder: null, db, EnrichOpts(), GradeOpts(), CancellationToken.None);

        var grade = GetProperty<object>(Value(result), "grade")!;
        Assert.True(GetProperty<bool>(grade, "graded"));
        Assert.True(GetProperty<bool>(grade, "isOutdated"));
    }

    [Fact]
    public async Task GetAlbumDetail_NoRow_ReturnsPending_NullTracklist_Ungraded()
    {
        await using var db = NewContext();
        var result = await AlbumsEndpoints.GetAlbumDetail("Nobody", "Nothing", year: null, folder: null, db, EnrichOpts(), GradeOpts(), CancellationToken.None);

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

        var result = await AlbumsEndpoints.GetAlbumDetail("Nobody", "Phantom Album", year: null, folder: null, db, EnrichOpts(), GradeOpts(), CancellationToken.None);

        var value = Value(result);
        Assert.Equal("localOnly", GetProperty<string>(value, "status"));
        Assert.Null(GetProperty<object>(value, "tracklist"));
        Assert.False(GetProperty<bool>(GetProperty<object>(value, "grade")!, "graded"));
    }

    [Fact]
    public async Task GetAlbumDetail_YearScoped_OnlyCountsThatFoldersOwnedTracks()
    {
        await using var db = NewContext();
        db.CanonicalAlbums.Add(new CanonicalAlbum
        {
            Id = 1,
            ArtistKey = "kanye west",
            AlbumKey = "my beautiful dark twisted fantasy",
            DisplayTitle = "My Beautiful Dark Twisted Fantasy",
            DisplayArtist = "Kanye West",
            Year = 2010,
            Status = CanonicalAlbumStatus.Fetched,
            Tracks =
            [
                new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = 1, Title = "Dark Fantasy" },
                new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = 2, Title = "Gorgeous" },
            ],
        });
        // The real 2010 release folder owns both canonical tracks.
        db.Songs.Add(OwnedSong("/a.mp3", "Kanye West", "My Beautiful Dark Twisted Fantasy", year: 2010, trackNumber: 1, title: "Dark Fantasy"));
        db.Songs.Add(OwnedSong("/b.mp3", "Kanye West", "My Beautiful Dark Twisted Fantasy", year: 2010, trackNumber: 2, title: "Gorgeous"));
        // A different-release folder (same album name, different year) owns only an off-album bootleg.
        db.Songs.Add(OwnedSong("/c.mp3", "Kanye West", "My Beautiful Dark Twisted Fantasy", year: 2013, trackNumber: 1, title: "Mama's Boy"));
        await db.SaveChangesAsync();

        var result = await AlbumsEndpoints.GetAlbumDetail("Kanye West", "My Beautiful Dark Twisted Fantasy", year: 2010, folder: null, db, EnrichOpts(), GradeOpts(), CancellationToken.None);

        var value = Value(result);
        Assert.Equal("linked", GetProperty<string>(value, "status"));
        // Scoped to 2010: both canonical tracks owned — the 2013 bootleg doesn't inflate the count.
        Assert.Equal(2, GetProperty<int>(GetProperty<object>(value, "tracklist")!, "ownedCount"));
    }

    [Fact]
    public async Task GetAlbumDetail_YearScopedToOffAlbumRelease_ReturnsLocalOnly()
    {
        await using var db = NewContext();
        db.CanonicalAlbums.Add(new CanonicalAlbum
        {
            Id = 1,
            ArtistKey = "kanye west",
            AlbumKey = "my beautiful dark twisted fantasy",
            DisplayTitle = "My Beautiful Dark Twisted Fantasy",
            DisplayArtist = "Kanye West",
            Year = 2010,
            Status = CanonicalAlbumStatus.Fetched,
            Tracks =
            [
                new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = 1, Title = "Dark Fantasy" },
                new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = 2, Title = "Gorgeous" },
            ],
        });
        db.Songs.Add(OwnedSong("/a.mp3", "Kanye West", "My Beautiful Dark Twisted Fantasy", year: 2010, trackNumber: 1, title: "Dark Fantasy"));
        // The split-off bootleg folder: a track that's on neither by title nor position (track 14 of a
        // 2-track canonical) — so it matches nothing on the canonical album.
        db.Songs.Add(OwnedSong("/c.mp3", "Kanye West", "My Beautiful Dark Twisted Fantasy", year: 2013, trackNumber: 14, title: "Mama's Boy"));
        await db.SaveChangesAsync();

        var result = await AlbumsEndpoints.GetAlbumDetail("Kanye West", "My Beautiful Dark Twisted Fantasy", year: 2013, folder: null, db, EnrichOpts(), GradeOpts(), CancellationToken.None);

        var value = Value(result);
        Assert.Equal("localOnly", GetProperty<string>(value, "status"));
        Assert.Null(GetProperty<object>(value, "tracklist"));
        Assert.False(GetProperty<bool>(GetProperty<object>(value, "grade")!, "graded"));
    }

    [Fact]
    public async Task GetAlbumDetail_FolderScoped_MatchesTheBuiltCopy_NotAnUnbuiltDuplicateWithTheMbid()
    {
        await using var db = NewContext();
        db.CanonicalAlbums.Add(new CanonicalAlbum
        {
            Id = 1,
            ArtistKey = "joey bada",
            AlbumKey = "1999",
            DisplayTitle = "1999",
            DisplayArtist = "Joey Bada$$",
            Status = CanonicalAlbumStatus.Fetched,
            Tracks =
            [
                new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = 7, Title = "World Domination", MusicBrainzRecordingId = "rec-wd" },
            ],
        });
        // The copy the album page shows: built into the folder, matched via a provider that left no MBID.
        var built = OwnedSong("/src/07.flac", "Joey Bada$$", "1999", trackNumber: 7, title: "World Domination",
            destinationPath: "/dest/Joey Bada$$/2012 - 1999/07 - World Domination.flac");
        // A re-downloaded twin carrying the canonical recording MBID that was never built — the page
        // doesn't show it, so the matcher must not consume it (that used to render a false MISSING row).
        var unbuiltDuplicate = OwnedSong("/src/slskd/07.flac", "Joey Bada$$", "1999", mbid: "rec-wd", trackNumber: 7, title: "World Domination");
        db.Songs.AddRange(built, unbuiltDuplicate);
        await db.SaveChangesAsync();

        var result = await AlbumsEndpoints.GetAlbumDetail(
            "Joey Bada$$", "1999", year: null, folder: "/dest/Joey Bada$$/2012 - 1999", db, EnrichOpts(), GradeOpts(), CancellationToken.None);

        var tracklist = GetProperty<object>(Value(result), "tracklist")!;
        Assert.Equal(1, GetProperty<int>(tracklist, "ownedCount"));
        var tracks = ((IEnumerable)GetProperty<object>(tracklist, "tracks")!).Cast<object>().ToList();
        Assert.Equal(built.Id, GetProperty<int?>(tracks[0], "ownedSongId"));
    }

    [Fact]
    public async Task GetAlbumDetail_FolderScoped_IgnoresSiblingPrefixAndNestedFolders()
    {
        await using var db = NewContext();
        db.CanonicalAlbums.Add(new CanonicalAlbum
        {
            Id = 1,
            ArtistKey = "joey bada",
            AlbumKey = "1999",
            DisplayTitle = "1999",
            DisplayArtist = "Joey Bada$$",
            Status = CanonicalAlbumStatus.Fetched,
            Tracks =
            [
                new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = 1, Title = "Summer Knights" },
                new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = 2, Title = "Waves" },
            ],
        });
        var inFolder = OwnedSong("/src/01.flac", "Joey Bada$$", "1999", trackNumber: 1, title: "Summer Knights",
            destinationPath: "/dest/Joey Bada$$/2012 - 1999/01 - Summer Knights.flac");
        // Same name prefix but a different folder — must not count as owned here.
        var siblingFolder = OwnedSong("/src/02a.flac", "Joey Bada$$", "1999", trackNumber: 2, title: "Waves",
            destinationPath: "/dest/Joey Bada$$/2012 - 1999 deluxe/02 - Waves.flac");
        // Nested one level below the album folder — its own card, not a direct child.
        var nestedFolder = OwnedSong("/src/02b.flac", "Joey Bada$$", "1999", trackNumber: 2, title: "Waves",
            destinationPath: "/dest/Joey Bada$$/2012 - 1999/cd2/02 - Waves.flac");
        db.Songs.AddRange(inFolder, siblingFolder, nestedFolder);
        await db.SaveChangesAsync();

        var result = await AlbumsEndpoints.GetAlbumDetail(
            "Joey Bada$$", "1999", year: null, folder: "/dest/Joey Bada$$/2012 - 1999", db, EnrichOpts(), GradeOpts(), CancellationToken.None);

        var tracklist = GetProperty<object>(Value(result), "tracklist")!;
        Assert.Equal(1, GetProperty<int>(tracklist, "ownedCount"));
        var tracks = ((IEnumerable)GetProperty<object>(tracklist, "tracks")!).Cast<object>().ToList();
        Assert.Equal(inFolder.Id, GetProperty<int?>(tracks[0], "ownedSongId"));
        Assert.Null(GetProperty<int?>(tracks[1], "ownedSongId"));
    }

    [Fact]
    public async Task GetAlbumDetail_FolderScopedToUnrelatedRelease_ReturnsLocalOnly()
    {
        await using var db = NewContext();
        db.CanonicalAlbums.Add(new CanonicalAlbum
        {
            Id = 1,
            ArtistKey = "joey bada",
            AlbumKey = "1999",
            DisplayTitle = "1999",
            DisplayArtist = "Joey Bada$$",
            Status = CanonicalAlbumStatus.Fetched,
            Tracks = [new CanonicalAlbumTrack { DiscNumber = 1, TrackNumber = 1, Title = "Summer Knights" }],
        });
        // The folder holds only a track that matches nothing on the canonical album (a bootleg
        // sharing the album name) — the endpoint must report local-only, not "missing everything".
        db.Songs.Add(OwnedSong("/src/14.flac", "Joey Bada$$", "1999", trackNumber: 14, title: "Freestyle Bootleg",
            destinationPath: "/dest/Joey Bada$$/1999 bootleg/14 - Freestyle Bootleg.flac"));
        await db.SaveChangesAsync();

        var result = await AlbumsEndpoints.GetAlbumDetail(
            "Joey Bada$$", "1999", year: null, folder: "/dest/Joey Bada$$/1999 bootleg", db, EnrichOpts(), GradeOpts(), CancellationToken.None);

        var value = Value(result);
        Assert.Equal("localOnly", GetProperty<string>(value, "status"));
        Assert.Null(GetProperty<object>(value, "tracklist"));
    }

    [Fact]
    public async Task GetAlbumDetail_BlankParams_Returns404()
    {
        await using var db = NewContext();
        var result = await AlbumsEndpoints.GetAlbumDetail("", "", year: null, folder: null, db, EnrichOpts(), GradeOpts(), CancellationToken.None);
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
        string sourcePath, string albumArtist, string album, string? mbid = null, int? trackNumber = null,
        string? title = null, int? year = null, string? destinationPath = null) => new()
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
        Year = year,
        MusicBrainzId = mbid,
        TrackNumber = trackNumber,
        Title = title,
        DestinationPath = destinationPath,
        LibraryBuildStatus = destinationPath is null ? LibraryBuildStatus.Pending : LibraryBuildStatus.Done,
    };

    private static MusicHoarderDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }
}
