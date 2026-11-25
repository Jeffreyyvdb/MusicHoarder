using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;
using MusicHoarder.Api.Tests.Library;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>
/// <c>POST /api/library/albums/merge</c>: the merge-side spellings take the kept title, each rewrite
/// is audited as one "album-merge" batch the dedup history lists and reverts, and only the caller's
/// own rows move.
/// </summary>
public class AlbumMergeEndpointTests
{
    [Fact]
    public async Task Merge_RewritesTheMergeSide_AndAuditsItAsOneAppliedBatch()
    {
        var options = NewOptions();
        await using var db = OwnerContext(options);
        db.Songs.AddRange(
            Song("/a/1.mp3", "Blueprint 3", LibraryBuildStatus.Done),
            Song("/a/2.mp3", "Blueprint 3"),
            Song("/a/3.mp3", "The Blueprint 3", LibraryBuildStatus.Done));
        await db.SaveChangesAsync();

        var result = await AlbumDedupEndpoints.Merge(
            new AlbumMergeRequest("JAY-Z", "The Blueprint 3", "Blueprint 3"), db, new JobManager(), default);

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.All(songs, s => Assert.Equal("The Blueprint 3", s.Album));
        Assert.Equal(
            [LibraryBuildStatus.Pending, LibraryBuildStatus.Pending, LibraryBuildStatus.Done],
            songs.Select(s => s.LibraryBuildStatus));
        Assert.Equal("Blueprint 3", songs[0].OriginalAlbum);

        var changes = await db.SongMetadataChanges.OrderBy(c => c.SongId).ToListAsync();
        Assert.Equal([songs[0].Id, songs[1].Id], changes.Select(c => c.SongId));
        Assert.All(changes, c => Assert.Equal(
            ("album-merge", nameof(SongMetadata.Album), "Blueprint 3", "The Blueprint 3"),
            (c.Source, c.FieldName, c.OldValue, c.NewValue)));
        AuditBatchAssert.OneAppliedBatch(changes, songs);
    }

    [Fact]
    public async Task Merge_IsListedAndRevertedByTheDedupHistory()
    {
        var options = NewOptions();
        await using var db = OwnerContext(options);
        db.Songs.AddRange(Song("/a/1.mp3", "Blueprint 3"), Song("/a/2.mp3", "The Blueprint 3"));
        await db.SaveChangesAsync();

        await AlbumDedupEndpoints.Merge(
            new AlbumMergeRequest("JAY-Z", "The Blueprint 3", "Blueprint 3"), db, new JobManager(), default);

        var history = new DedupActionHistoryService(db, NullLogger<DedupActionHistoryService>.Instance);
        var action = Assert.Single(await history.ListAsync());
        Assert.Equal(("album-merge", 1, true), (action.Source, action.ChangeCount, action.Revertible));

        var revert = await history.RevertAsync(TestUsers.OwnerId, action.Source, action.BatchTicks);

        Assert.Equal(1, revert.ChangesReverted);
        Assert.Equal("Blueprint 3", (await db.Songs.OrderBy(s => s.Id).FirstAsync()).Album);
    }

    [Fact]
    public async Task Merge_LeavesOtherAccountsAndTheDemoLibraryAlone()
    {
        var options = NewOptions();
        await using (var seed = new MusicHoarderDbContext(options))
        {
            seed.Songs.AddRange(
                Song("/a/1.mp3", "Blueprint 3"),
                Song("/a/2.mp3", "Blueprint 3", owner: TestUsers.FriendId),
                Song("/a/3.mp3", "Blueprint 3", owner: TestUsers.DemoId));
            await seed.SaveChangesAsync();
        }

        await using (var db = OwnerContext(options))
        {
            await AlbumDedupEndpoints.Merge(
                new AlbumMergeRequest("JAY-Z", "The Blueprint 3", "Blueprint 3"), db, new JobManager(), default);
        }

        await using var check = new MusicHoarderDbContext(options);
        var songs = await check.Songs.IgnoreQueryFilters().OrderBy(s => s.Id).ToListAsync();
        Assert.Equal(["The Blueprint 3", "Blueprint 3", "Blueprint 3"], songs.Select(s => s.Album));
        var change = Assert.Single(await check.SongMetadataChanges.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(songs[0].Id, change.SongId);
    }

    private static DbContextOptions<MusicHoarderDbContext> NewOptions() =>
        new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

    private static MusicHoarderDbContext OwnerContext(DbContextOptions<MusicHoarderDbContext> options) =>
        new(options, new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser));

    private static SongMetadata Song(
        string sourcePath, string album, LibraryBuildStatus buildStatus = LibraryBuildStatus.Pending,
        Guid? owner = null) => new()
    {
        OwnerUserId = owner ?? TestUsers.OwnerId,
        SourcePath = sourcePath,
        FileName = Path.GetFileName(sourcePath),
        Extension = Path.GetExtension(sourcePath),
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = "JAY-Z",
        AlbumArtist = "JAY-Z",
        Album = album,
        Title = Path.GetFileNameWithoutExtension(sourcePath),
        LibraryBuildStatus = buildStatus,
        DestinationPath = buildStatus == LibraryBuildStatus.Done ? $"/dest/JAY-Z/{album}/{sourcePath[^5..]}" : null,
    };
}
