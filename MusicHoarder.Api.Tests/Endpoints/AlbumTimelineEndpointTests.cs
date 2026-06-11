using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Endpoints;

public class AlbumTimelineEndpointTests
{
    private static readonly Guid OwnerB = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task NoMemberSongs_Returns404()
    {
        await using var db = NewContext();
        var result = await AlbumsEndpoints.GetAlbumTimeline("Nobody", "Nothing", db, CancellationToken.None);
        Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task BlankParams_Returns404()
    {
        await using var db = NewContext();
        var result = await AlbumsEndpoints.GetAlbumTimeline("", "", db, CancellationToken.None);
        Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task DiscoveredEvent_UsesMinIndexedAt_AndTrackCount()
    {
        await using var db = NewContext();
        db.Songs.Add(Song("/1.mp3", indexedAt: T0.AddMinutes(5)));
        db.Songs.Add(Song("/2.mp3", indexedAt: T0));
        await db.SaveChangesAsync();

        var timeline = await Timeline(db);

        var discovered = Assert.Single(timeline.Events, e => e.Key == "discovered");
        Assert.Equal(T0, discovered.TimeUtc);
        Assert.Equal("SCAN", discovered.Stage);
        Assert.Equal(2, timeline.TrackCount);
        Assert.Contains("2 tracks", discovered.Description);
    }

    [Fact]
    public async Task ProviderRollup_ReportsMatchedXofY_WithSpan_AndPartialIsWarn()
    {
        await using var db = NewContext();
        var s1 = Song("/1.mp3");
        var s2 = Song("/2.mp3");
        db.Songs.AddRange(s1, s2);
        await db.SaveChangesAsync();
        db.SongProviderAttempts.AddRange(
            Attempt(s1.Id, EnrichmentProvider.MusicBrainzWeb, ProviderAttemptStatus.Matched, T0.AddMinutes(1)),
            Attempt(s2.Id, EnrichmentProvider.MusicBrainzWeb, ProviderAttemptStatus.NoMatch, T0.AddMinutes(3)),
            Attempt(s1.Id, EnrichmentProvider.Deezer, ProviderAttemptStatus.Matched, T0.AddMinutes(2)),
            Attempt(s2.Id, EnrichmentProvider.Deezer, ProviderAttemptStatus.Matched, T0.AddMinutes(4)));
        await db.SaveChangesAsync();

        var timeline = await Timeline(db);

        var mb = Assert.Single(timeline.Events, e => e.Key == "provider:MusicBrainzWeb");
        Assert.Equal("METADATA", mb.Stage);
        Assert.Equal("warn", mb.Tint);
        Assert.Equal(1, mb.MatchedCount);
        Assert.Equal(2, mb.TotalCount);
        Assert.Equal(T0.AddMinutes(1), mb.FirstAtUtc);
        Assert.Equal(T0.AddMinutes(3), mb.LastAtUtc);
        Assert.Equal(T0.AddMinutes(3), mb.TimeUtc);

        var dz = Assert.Single(timeline.Events, e => e.Key == "provider:Deezer");
        Assert.Equal("ok", dz.Tint);
        Assert.Equal(2, dz.MatchedCount);
    }

    [Fact]
    public async Task CanonicalFetched_IncludesWinningSources_AndContestedIsWarn()
    {
        await using var db = NewContext();
        db.Songs.Add(Song("/1.mp3"));
        db.CanonicalAlbums.Add(new CanonicalAlbum
        {
            ArtistKey = "daft punk",
            AlbumKey = "discovery",
            Status = CanonicalAlbumStatus.Fetched,
            FetchedAtUtc = T0.AddMinutes(10),
            ResolvedTrackCount = 14,
            TrackCountContested = true,
            SourcesJson = """[{"Provider":2,"AlbumId":"r","TrackCount":14,"InWinningCluster":true},{"Provider":4,"AlbumId":"d","TrackCount":13,"InWinningCluster":false}]""",
        });
        await db.SaveChangesAsync();

        var timeline = await Timeline(db);

        var canonical = Assert.Single(timeline.Events, e => e.Key == "canonical");
        Assert.Equal("CANONICAL", canonical.Stage);
        Assert.Equal("warn", canonical.Tint);
        Assert.Equal(T0.AddMinutes(10), canonical.TimeUtc);
        Assert.Contains("14 tracks", canonical.Description);
        Assert.Contains("MusicBrainzWeb", canonical.Description);
        Assert.DoesNotContain("Deezer", canonical.Description);
        Assert.Contains("contested", canonical.Description);
    }

    [Fact]
    public async Task CanonicalNotFound_YieldsNeutralEvent()
    {
        await using var db = NewContext();
        db.Songs.Add(Song("/1.mp3"));
        db.CanonicalAlbums.Add(new CanonicalAlbum
        {
            ArtistKey = "daft punk",
            AlbumKey = "discovery",
            Status = CanonicalAlbumStatus.NotFound,
            FetchedAtUtc = T0.AddMinutes(10),
        });
        await db.SaveChangesAsync();

        var timeline = await Timeline(db);

        var canonical = Assert.Single(timeline.Events, e => e.Key == "canonical");
        Assert.Equal("neutral", canonical.Tint);
        Assert.Contains("No matching album", canonical.Description);
    }

    [Fact]
    public async Task EachGradeRow_IsOneEvent_WithVerdictTint()
    {
        await using var db = NewContext();
        db.Songs.Add(Song("/1.mp3"));
        var canonical = new CanonicalAlbum
        {
            ArtistKey = "daft punk",
            AlbumKey = "discovery",
            Status = CanonicalAlbumStatus.Fetched,
            FetchedAtUtc = T0.AddMinutes(10),
        };
        db.CanonicalAlbums.Add(canonical);
        await db.SaveChangesAsync();
        db.CanonicalAlbumQualityGrades.AddRange(
            new CanonicalAlbumQualityGrade
            {
                CanonicalAlbumId = canonical.Id,
                OwnerUserId = WellKnownUsers.OwnerId,
                Score = 35,
                Verdict = SongQualityVerdict.Questionable,
                Summary = "Edition mismatch",
                Model = "claude-haiku-4-5",
                GradedAtUtc = T0.AddMinutes(20),
            },
            new CanonicalAlbumQualityGrade
            {
                CanonicalAlbumId = canonical.Id,
                OwnerUserId = WellKnownUsers.OwnerId,
                Score = 92,
                Verdict = SongQualityVerdict.Excellent,
                Model = "claude-haiku-4-5",
                GradedAtUtc = T0.AddMinutes(30),
            });
        await db.SaveChangesAsync();

        var timeline = await Timeline(db);

        var grades = timeline.Events.Where(e => e.Stage == "AI GRADE").ToList();
        Assert.Equal(2, grades.Count);
        Assert.Equal("warn", grades[0].Tint);
        Assert.Equal(35, grades[0].Pct);
        Assert.Contains("Edition mismatch", grades[0].Description);
        Assert.Equal("ok", grades[1].Tint);
        Assert.Equal("claude-haiku-4-5", grades[1].Provider);
    }

    [Fact]
    public async Task WriteEvents_RollUpViaHistoryRollup_IntoConsolidateEvent()
    {
        await using var db = NewContext();
        var s1 = Song("/1.mp3", destinationPath: "/dest/Daft Punk/Discovery/1.mp3", builtAt: T0.AddHours(1));
        var s2 = Song("/2.mp3", destinationPath: "/dest/Daft Punk/Discovery/2.mp3", builtAt: T0.AddHours(1));
        db.Songs.AddRange(s1, s2);
        await db.SaveChangesAsync();
        db.LibraryWriteEvents.AddRange(
            WriteEvent(s1.Id, "MusicBrainzReleaseId", "rel-a", "rel-keep", T0.AddHours(2)),
            WriteEvent(s2.Id, "MusicBrainzReleaseId", "rel-b", "rel-keep", T0.AddHours(2)));
        await db.SaveChangesAsync();

        var timeline = await Timeline(db);

        var consolidate = Assert.Single(timeline.Events, e => e.Stage == "CONSOLIDATE");
        Assert.Equal(2, consolidate.MatchedCount);
        Assert.Contains("2 releases", consolidate.Description);
        Assert.Equal(T0.AddHours(2), consolidate.TimeUtc);

        // The synthetic first-build event coexists with the write rollups.
        var built = Assert.Single(timeline.Events, e => e.Key == "built");
        Assert.Equal(T0.AddHours(1), built.TimeUtc);
        Assert.Contains("2 of 2 tracks", built.Description);
    }

    [Fact]
    public async Task CoverWrite_MatchedByDestinationFolder_NotSongId()
    {
        await using var db = NewContext();
        db.Songs.Add(Song("/1.mp3", destinationPath: "/dest/Daft Punk/Discovery/1.mp3", builtAt: T0.AddHours(1)));
        await db.SaveChangesAsync();
        db.LibraryWriteEvents.Add(new LibraryWriteEvent
        {
            OwnerUserId = WellKnownUsers.OwnerId,
            Kind = LibraryWriteEventKind.AlbumCoverWritten,
            WrittenAtUtc = T0.AddHours(3),
            AlbumFolder = "/dest/Daft Punk/Discovery",
            Album = "Discovery",
            AlbumArtist = "Daft Punk",
            FieldName = "Cover",
            NewValue = "written",
        });
        await db.SaveChangesAsync();

        var timeline = await Timeline(db);

        var cover = Assert.Single(timeline.Events, e => e.Stage == "COVER");
        Assert.Equal("info", cover.Tint);
        Assert.Equal(T0.AddHours(3), cover.TimeUtc);
    }

    [Fact]
    public async Task ManualApprovals_AggregateIntoOneEvent()
    {
        await using var db = NewContext();
        db.Songs.Add(Song("/1.mp3", approvedAt: T0.AddMinutes(40)));
        db.Songs.Add(Song("/2.mp3", approvedAt: T0.AddMinutes(50)));
        db.Songs.Add(Song("/3.mp3"));
        await db.SaveChangesAsync();

        var timeline = await Timeline(db);

        var approved = Assert.Single(timeline.Events, e => e.Key == "approved");
        Assert.Equal(T0.AddMinutes(50), approved.TimeUtc);
        Assert.Equal(2, approved.MatchedCount);
        Assert.Equal(3, approved.TotalCount);
        Assert.Equal(T0.AddMinutes(40), approved.FirstAtUtc);
    }

    [Fact]
    public async Task CoverFetchFailure_IsWarnEvent()
    {
        await using var db = NewContext();
        db.Songs.Add(Song("/1.mp3", destinationPath: "/dest/Daft Punk/Discovery/1.mp3", builtAt: T0.AddHours(1)));
        db.AlbumCoverFetchAttempts.Add(new AlbumCoverFetchAttempt
        {
            AlbumFolder = "/dest/Daft Punk/Discovery",
            Status = AlbumCoverFetchStatus.NotFound,
            AttemptCount = 3,
            LastAttemptAtUtc = T0.AddHours(4),
        });
        await db.SaveChangesAsync();

        var timeline = await Timeline(db);

        var failure = Assert.Single(timeline.Events, e => e.Key.StartsWith("cover-fetch:"));
        Assert.Equal("warn", failure.Tint);
        Assert.Contains("3 attempts", failure.Description);
    }

    [Fact]
    public async Task Events_AreChronologicallyOrdered()
    {
        await using var db = NewContext();
        var song = Song("/1.mp3", approvedAt: T0.AddMinutes(30), destinationPath: "/dest/Daft Punk/Discovery/1.mp3", builtAt: T0.AddHours(1));
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        db.SongProviderAttempts.Add(Attempt(song.Id, EnrichmentProvider.MusicBrainzWeb, ProviderAttemptStatus.Matched, T0.AddMinutes(5)));
        await db.SaveChangesAsync();

        var timeline = await Timeline(db);

        Assert.True(timeline.Events.Count >= 4);
        Assert.Equal(timeline.Events.OrderBy(e => e.TimeUtc).Select(e => e.Key), timeline.Events.Select(e => e.Key));
        Assert.Equal("discovered", timeline.Events[0].Key);
    }

    [Fact]
    public async Task OtherOwnersSongs_AreInvisible_404()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>().UseInMemoryDatabase(dbName).Options;
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Songs.Add(Song("/1.mp3"));
            await seed.SaveChangesAsync();
        }

        await using var dbB = new MusicHoarderDbContext(options, new StubCurrentUser(OwnerB));
        var result = await AlbumsEndpoints.GetAlbumTimeline("Daft Punk", "Discovery", dbB, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)result).StatusCode);
    }

    private static async Task<AlbumTimelineResponse> Timeline(MusicHoarderDbContext db)
    {
        var result = await AlbumsEndpoints.GetAlbumTimeline("Daft Punk", "Discovery", db, CancellationToken.None);
        return (AlbumTimelineResponse)((IValueHttpResult)result).Value!;
    }

    private static SongMetadata Song(
        string sourcePath,
        DateTime? indexedAt = null,
        DateTime? approvedAt = null,
        DateTime? builtAt = null,
        string? destinationPath = null) => new()
    {
        OwnerUserId = WellKnownUsers.OwnerId,
        SourcePath = sourcePath,
        FileName = Path.GetFileName(sourcePath),
        Extension = Path.GetExtension(sourcePath),
        FileSizeBytes = 1,
        LastModifiedUtc = T0,
        IndexedAtUtc = indexedAt ?? T0,
        Title = Path.GetFileNameWithoutExtension(sourcePath),
        Artist = "Daft Punk",
        AlbumArtist = "Daft Punk",
        Album = "Discovery",
        ManuallyApprovedAtUtc = approvedAt,
        LibraryBuiltAtUtc = builtAt,
        DestinationPath = destinationPath,
    };

    private static SongProviderAttempt Attempt(
        int songId, EnrichmentProvider provider, ProviderAttemptStatus status, DateTime attemptedAt) => new()
    {
        SongId = songId,
        Provider = provider,
        Status = status,
        AttemptedAtUtc = attemptedAt,
    };

    private static LibraryWriteEvent WriteEvent(
        int songId, string field, string? oldValue, string? newValue, DateTime writtenAt) => new()
    {
        OwnerUserId = WellKnownUsers.OwnerId,
        SongId = songId,
        Kind = LibraryWriteEventKind.TrackTagsWritten,
        WrittenAtUtc = writtenAt,
        AlbumFolder = "/dest/Daft Punk/Discovery",
        Album = "Discovery",
        AlbumArtist = "Daft Punk",
        FieldName = field,
        OldValue = oldValue,
        NewValue = newValue,
        IsAlbumIdentityField = true,
    };

    private static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class StubCurrentUser(Guid userId) : Api.Auth.ICurrentUserAccessor
    {
        public Api.Auth.CurrentUser? User { get; } = new(userId, "owner@test", Api.Auth.UserRole.Owner, "Owner");
        public Guid UserId => userId;
    }
}
