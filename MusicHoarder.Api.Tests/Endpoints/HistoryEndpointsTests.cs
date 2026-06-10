using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Endpoints;

public class HistoryEndpointsTests
{
    private static readonly Guid OwnerA = Api.Auth.WellKnownUsers.OwnerId;
    private static readonly Guid OwnerB = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static HistoryFeedResponse Value(IResult result)
        => (HistoryFeedResponse)((IValueHttpResult)result).Value!;

    private static SongMetadata AddSong(MusicHoarderDbContext db, int n, string title)
    {
        var song = new SongMetadata
        {
            OwnerUserId = OwnerA,
            SourcePath = $"/src/{n}.mp3",
            FileName = $"{n}.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1000,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Title = title,
            Artist = "Artist",
        };
        db.Songs.Add(song);
        db.SaveChanges();
        return song;
    }

    private static void AddEvent(
        MusicHoarderDbContext db, Guid owner, int? songId, string field, string? oldValue, string? newValue,
        DateTime writtenAt, bool isAlbumIdentity = false, string album = "Album", string albumArtist = "Artist",
        LibraryWriteEventKind kind = LibraryWriteEventKind.TrackTagsWritten)
    {
        db.LibraryWriteEvents.Add(new LibraryWriteEvent
        {
            OwnerUserId = owner,
            SongId = songId,
            Kind = kind,
            WrittenAtUtc = writtenAt,
            AlbumFolder = $"/dest/{albumArtist}/{album}",
            Album = album,
            AlbumArtist = albumArtist,
            FieldName = field,
            OldValue = oldValue,
            NewValue = newValue,
            IsAlbumIdentityField = isAlbumIdentity,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task GetHistory_FiltersByDateWindow()
    {
        using var db = NewContext();
        var s = AddSong(db, 1, "Track");
        var now = DateTime.UtcNow;
        AddEvent(db, OwnerA, s.Id, "Title", "Old", "New", now.AddDays(-1));
        AddEvent(db, OwnerA, s.Id, "Title", "Older", "New", now.AddDays(-10));

        var result = await HistoryEndpoints.GetHistory(
            db, from: now.AddDays(-3), to: now, artist: null, album: null, cursor: null, take: null);

        var feed = Value(result);
        Assert.Equal(1, feed.TotalEventsInWindow);
        var summary = Assert.Single(feed.Summaries);
        Assert.Equal("tags", summary.Kind);
    }

    [Fact]
    public async Task GetHistory_RollsConsolidationIntoOneSummary()
    {
        using var db = NewContext();
        var s1 = AddSong(db, 1, "T1");
        var s2 = AddSong(db, 2, "T2");
        var now = DateTime.UtcNow;
        // Two tracks of one album moved off divergent releases onto the same one.
        AddEvent(db, OwnerA, s1.Id, "MusicBrainzReleaseId", "rel-a", "rel-keep", now, isAlbumIdentity: true);
        AddEvent(db, OwnerA, s2.Id, "MusicBrainzReleaseId", "rel-b", "rel-keep", now, isAlbumIdentity: true);

        var result = await HistoryEndpoints.GetHistory(
            db, from: now.AddDays(-1), to: now.AddDays(1), artist: null, album: null, cursor: null, take: null);

        var feed = Value(result);
        var summary = Assert.Single(feed.Summaries, x => x.Kind == "consolidation");
        Assert.Equal(2, summary.TrackCount);
        Assert.Contains("2 releases", summary.Headline);
        Assert.Equal(2, summary.Changes.Count);
        Assert.Contains(summary.Changes, c => c.TrackTitle == "T1");
    }

    [Fact]
    public async Task GetHistory_GroupsArtistRenameByOldNew()
    {
        using var db = NewContext();
        var s1 = AddSong(db, 1, "T1");
        var s2 = AddSong(db, 2, "T2");
        var now = DateTime.UtcNow;
        AddEvent(db, OwnerA, s1.Id, "Artist", "Kanye West", "Ye", now);
        AddEvent(db, OwnerA, s2.Id, "Artist", "Kanye West", "Ye", now);

        var result = await HistoryEndpoints.GetHistory(
            db, from: now.AddDays(-1), to: now.AddDays(1), artist: null, album: null, cursor: null, take: null);

        var summary = Assert.Single(Value(result).Summaries, x => x.Kind == "artist-rename");
        Assert.Equal(2, summary.TrackCount);
        Assert.Contains("Kanye West", summary.Headline);
        Assert.Contains("Ye", summary.Headline);
    }

    [Fact]
    public async Task GetHistory_ScopesToOwner()
    {
        // The endpoint relies on the EF global query filter; construct a context bound to owner B.
        var dbName = Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>().UseInMemoryDatabase(dbName).Options;
        var now = DateTime.UtcNow;
        using (var seed = new MusicHoarderDbContext(options))
        {
            // Owner A event only.
            seed.LibraryWriteEvents.Add(new LibraryWriteEvent
            {
                OwnerUserId = OwnerA,
                Kind = LibraryWriteEventKind.TrackTagsWritten,
                WrittenAtUtc = now,
                Album = "Album",
                AlbumArtist = "Artist",
                FieldName = "Title",
                NewValue = "X",
            });
            seed.SaveChanges();
        }

        using var dbB = new MusicHoarderDbContext(options, new StubCurrentUser(OwnerB));
        var result = await HistoryEndpoints.GetHistory(
            dbB, from: now.AddDays(-1), to: now.AddDays(1), artist: null, album: null, cursor: null, take: null);

        Assert.Empty(Value(result).Summaries);
    }

    private sealed class StubCurrentUser(Guid userId) : Api.Auth.ICurrentUserAccessor
    {
        public Api.Auth.CurrentUser? User { get; } = new(userId, "owner@test", Api.Auth.UserRole.Owner, "Owner");
        public Guid UserId => userId;
    }
}
