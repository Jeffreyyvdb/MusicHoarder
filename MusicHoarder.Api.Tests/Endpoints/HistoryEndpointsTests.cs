using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.History;
using MusicHoarder.Api.History.Sources;
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

    /// <summary>The destination-write source alone — what these tests are about.</summary>
    private static IEnumerable<IActivitySource> WriteSource(MusicHoarderDbContext db)
        => [new LibraryWriteActivitySource(db)];

    private static Task<IResult> Run(
        MusicHoarderDbContext db, DateTime from, DateTime to, string? category = null,
        string? cursor = null, int? take = null, bool? problems = null,
        IEnumerable<IActivitySource>? sources = null)
        => HistoryEndpoints.GetHistory(
            db, sources ?? WriteSource(db), from, to, artist: null, album: null,
            category: category, problems: problems, cursor: cursor, take: take, ct: default);

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

        var result = await Run(db, now.AddDays(-3), now);

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

        var result = await Run(db, now.AddDays(-1), now.AddDays(1));

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

        var result = await Run(db, now.AddDays(-1), now.AddDays(1));

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
        var result = await Run(dbB, now.AddDays(-1), now.AddDays(1), sources: WriteSource(dbB));

        Assert.Empty(Value(result).Summaries);
    }

    [Fact]
    public async Task GetHistory_CountsEveryCategoryEvenWhenFilteredToOne()
    {
        using var db = NewContext();
        var s = AddSong(db, 1, "Track");
        var now = DateTime.UtcNow;
        AddEvent(db, OwnerA, s.Id, "Title", "Old", "New", now);
        AddEvent(db, OwnerA, s.Id, "Cover", null, "fetched:spotify", now,
            kind: LibraryWriteEventKind.AlbumCoverWritten);

        var all = Value(await Run(db, now.AddDays(-1), now.AddDays(1)));
        Assert.Equal(1, all.CategoryCounts["written"]);
        Assert.Equal(1, all.CategoryCounts["artwork"]);

        // Narrowing to one category must not shrink the chips' numbers, or the other chips would
        // read as empty the moment you pressed this one.
        var artworkOnly = Value(await Run(db, now.AddDays(-1), now.AddDays(1), category: "artwork"));
        Assert.Equal(1, artworkOnly.CategoryCounts["written"]);
        Assert.Single(artworkOnly.Summaries);
        Assert.Equal("cover", artworkOnly.Summaries[0].Kind);
    }

    [Fact]
    public async Task GetHistory_IgnoresAnUnknownCategory()
    {
        using var db = NewContext();
        var s = AddSong(db, 1, "Track");
        var now = DateTime.UtcNow;
        AddEvent(db, OwnerA, s.Id, "Title", "Old", "New", now);

        // A junk value falls back to "everything" rather than silently returning nothing.
        var feed = Value(await Run(db, now.AddDays(-1), now.AddDays(1), category: "not-a-category"));
        Assert.Single(feed.Summaries);
    }

    [Fact]
    public async Task GetHistory_PagesPastEntriesThatShareOneTimestamp()
    {
        using var db = NewContext();
        var now = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
        // One sweep, three albums, one instant — the shape a timestamp-only cursor loses the tail of.
        for (var i = 1; i <= 3; i++)
        {
            var song = AddSong(db, i, $"T{i}");
            AddEvent(db, OwnerA, song.Id, "Title", "Old", "New", now, album: $"Album{i}", albumArtist: "Artist");
        }

        var seen = new List<string>();
        string? cursor = null;
        for (var page = 0; page < 5; page++)
        {
            var feed = Value(await Run(db, now.AddDays(-1), now.AddDays(1), cursor: cursor, take: 1));
            seen.AddRange(feed.Summaries.Select(x => x.Id));
            cursor = feed.NextCursor;
            if (cursor is null) break;
        }

        Assert.Equal(3, seen.Count);
        Assert.Equal(3, seen.Distinct().Count());
    }

    [Fact]
    public async Task GetHistory_ProblemsOnlyLeavesTheChipsCountingProblems()
    {
        using var db = NewContext();
        var song = AddSong(db, 1, "Track");
        var now = DateTime.UtcNow;
        AddEvent(db, OwnerA, song.Id, "Title", "Old", "New", now);
        song.LibraryBuildStatus = LibraryBuildStatus.Failed;
        song.LibraryBuildLastAttemptedAtUtc = now;
        song.LibraryBuildError = "destination unreachable";
        db.SaveChanges();

        var everything = Value(await Run(db, now.AddDays(-1), now.AddDays(1)));
        Assert.Equal(2, everything.CategoryCounts["written"]);

        var problems = Value(await Run(db, now.AddDays(-1), now.AddDays(1), problems: true));
        Assert.Equal(1, problems.CategoryCounts["written"]);
        Assert.Equal("build-failed", Assert.Single(problems.Summaries).Kind);
    }

    [Fact]
    public async Task GetHistory_KeepsAnotherSourcesCountWhenFilteredAwayFromIt()
    {
        using var db = NewContext();
        var song = AddSong(db, 1, "Track");
        var now = DateTime.UtcNow;
        AddEvent(db, OwnerA, song.Id, "Title", "Old", "New", now);
        song.LyricsStatus = LyricsStatus.Fetched;
        song.LyricsLastAttemptedAtUtc = now;
        db.SaveChanges();

        IEnumerable<IActivitySource> both = [new LibraryWriteActivitySource(db), new LyricsActivitySource(db)];

        // Selecting Lyrics must not zero the Library chip — otherwise the counts stop being a reason
        // to press a chip, which is the whole point of putting numbers on them.
        var feed = Value(await Run(db, now.AddDays(-1), now.AddDays(1), category: "lyrics", sources: both));
        Assert.Equal(1, feed.CategoryCounts["written"]);
        Assert.Equal(1, feed.CategoryCounts["lyrics"]);
        Assert.Equal("lyrics-added", Assert.Single(feed.Summaries).Kind);
    }

    private sealed class StubCurrentUser(Guid userId) : Api.Auth.ICurrentUserAccessor
    {
        public Api.Auth.CurrentUser? User { get; } = new(userId, "owner@test", Api.Auth.UserRole.Admin, "Owner");
        public Guid UserId => userId;
    }
}
