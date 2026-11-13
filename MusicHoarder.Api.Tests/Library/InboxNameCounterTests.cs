using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Library;

/// <summary>
/// The Inbox's name counts used to be the lengths of three reports. They are now counted from one
/// read of the library that each detector filters for itself, two of them from memory when nothing
/// they read has changed. Every test here asks the same question of that shortcut: is it still the
/// number the reports would give?
/// </summary>
public class InboxNameCounterTests
{
    private static readonly Guid Owner = WellKnownUsers.OwnerId;
    private static readonly Guid SecondAdmin = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Counts_AreWhatTheThreeReportsHold()
    {
        await using var db = NewContext();
        SeedLibrary(db);
        await db.SaveChangesAsync();

        var counts = await Counter(db, new DetectionCountMemo()).CountAsync(Owner);

        var (artists, albumPairs, splitAlbums) = await Reports(db, Owner);
        // Each queue has something in it, or equal counts would prove little.
        Assert.Equal((2, 1, 2), (artists, albumPairs, splitAlbums));
        Assert.Equal(new InboxNameCounts(artists, splitAlbums + albumPairs), counts);
    }

    // One read serves three detectors whose own queries each filter differently. None may count a
    // row its report would not: deleted, synthetic, the demo library's, a duplicate, unreleased,
    // not matched, without an album. (Another account's rows are the next test.)
    [Fact]
    public async Task Counts_LeaveOutEveryRowTheReportsLeaveOut()
    {
        await using var db = NewContext();
        SeedLibrary(db);
        await db.SaveChangesAsync();
        var before = await Counter(db, new DetectionCountMemo()).CountAsync(Owner);

        // Rows that would each add a suggestion if a detector that leaves them out counted them:
        // a second spelling of an artist on a misspelt copy of an album, and a second year for a
        // one-track album.
        SongMetadata Spellings(string path, string artist = "JAYZ")
        {
            var song = Song(path, "One", album: "Graduaton", artist: "Kanye West", year: 2007);
            song.Artist = artist;
            return song;
        }
        SongMetadata SecondYear(string path) => Song(path, "Two", album: "Yeezus", artist: "Kanye Wst", year: 1999);
        static SongMetadata[] Each(Action<SongMetadata> why, params SongMetadata[] songs)
        {
            foreach (var song in songs)
                why(song);
            return songs;
        }
        db.Songs.AddRange(
        [
            // No report has these.
            .. Each(s => s.DeletedAtUtc = DateTime.UtcNow, Spellings("/x/deleted-1.mp3"), SecondYear("/x/deleted-2.mp3")),
            .. Each(s => s.IsSynthetic = true, Spellings("/x/synthetic-1.mp3"), SecondYear("/x/synthetic-2.mp3")),
            .. Each(s => s.OwnerUserId = WellKnownUsers.DemoId, Spellings("/x/demo-1.mp3"), SecondYear("/x/demo-2.mp3")),
            // A duplicate still names its artists, but is no album's track.
            .. Each(s => s.IsDuplicate = true, Spellings("/x/duplicate-1.mp3", artist: "Kanye West"), SecondYear("/x/duplicate-2.mp3")),
            // An unreleased or unmatched track sits in its album, but never makes it a split one.
            .. Each(s => s.IsUnreleased = true, SecondYear("/x/unreleased.mp3")),
            .. Each(s => s.EnrichmentStatus = EnrichmentStatus.NeedsReview, SecondYear("/x/unmatched.mp3")),
            // And a row without an album is in neither album report.
            .. Each(s => s.Album = "", SecondYear("/x/no-album.mp3")),
        ]);
        await db.SaveChangesAsync();

        var after = await Counter(db, new DetectionCountMemo()).CountAsync(Owner);

        var (artists, albumPairs, splitAlbums) = await Reports(db, Owner);
        Assert.Equal(new InboxNameCounts(artists, splitAlbums + albumPairs), after);
        Assert.Equal(before, after);
    }

    // The duplicate counts are an account's own; the split-album report covers the whole instance,
    // as its page does.
    [Fact]
    public async Task AnotherAdmin_GetsTheirOwnDuplicateCounts_AndTheSameSplitAlbums()
    {
        await using var db = NewContext();
        SeedLibrary(db);
        db.Songs.AddRange(
            Song("/o/1.mp3", "One", album: "Discovery", artist: "Daft Punk", owner: SecondAdmin),
            Song("/o/2.mp3", "Two", album: "Discovery", artist: "Daft Pnk", owner: SecondAdmin));
        await db.SaveChangesAsync();
        var memo = new DetectionCountMemo();

        var first = await Counter(db, memo).CountAsync(Owner);
        var second = await Counter(db, memo).CountAsync(SecondAdmin);

        var (artists, albumPairs, splitAlbums) = await Reports(db, SecondAdmin);
        Assert.Equal((1, 0, 2), (artists, albumPairs, splitAlbums));
        Assert.Equal(new InboxNameCounts(artists, splitAlbums + albumPairs), second);
        Assert.Equal(new InboxNameCounts(2, 3), first);
        // And asking for one account has not disturbed what is remembered for the other.
        Assert.Equal(first, await Counter(db, memo).CountAsync(Owner));
    }

    // The memo's whole risk in one test: with one memo kept across requests, as the running API
    // keeps it, the counts must follow every write that changes a report, at once.
    [Fact]
    public async Task Counts_FollowEveryChange_WithTheMemoKeptBetweenRequests()
    {
        await using var db = NewContext();
        SeedLibrary(db);
        await db.SaveChangesAsync();
        var memo = new DetectionCountMemo();
        async Task AssertCountsMatchReports()
        {
            var (artists, albumPairs, splitAlbums) = await Reports(db, Owner);
            Assert.Equal(new InboxNameCounts(artists, splitAlbums + albumPairs), await Counter(db, memo).CountAsync(Owner));
        }

        await AssertCountsMatchReports();
        await AssertCountsMatchReports();

        // Things that happen all day and change no report.
        var played = await db.Songs.FirstAsync(s => s.Album == "The Blueprint 3");
        played.PlayCount += 1;
        played.LibraryBuildStatus = LibraryBuildStatus.Pending;
        await db.SaveChangesAsync();
        await AssertCountsMatchReports();

        // An artist merge.
        foreach (var song in await db.Songs.Where(s => s.Artist == "Kanye Wst").ToListAsync())
            song.Artist = song.AlbumArtist = "Kanye West";
        await db.SaveChangesAsync();
        Assert.Equal(1, (await Counter(db, memo).CountAsync(Owner)).Artists);
        await AssertCountsMatchReports();

        // An album merge.
        foreach (var song in await db.Songs.Where(s => s.Album == "Blueprint 3").ToListAsync())
            song.Album = "The Blueprint 3";
        await db.SaveChangesAsync();
        await AssertCountsMatchReports();

        // A dismissal.
        await new ArtistDuplicateService(db, EnricherOptions(), memo, NullLogger<ArtistDuplicateService>.Instance)
            .DismissAsync(Owner, ["Lauryn Hill", "Ms. Lauryn Hill"]);
        Assert.Equal(0, (await Counter(db, memo).CountAsync(Owner)).Artists);
        await AssertCountsMatchReports();

        // A split album healed, and a new one appearing.
        await Healer(db).HealAsync();
        await AssertCountsMatchReports();
        db.Songs.Add(Song("/g/3.flac", "Stronger", album: "Graduation", artist: "Kanye West", year: 2006, releaseId: "rel-c"));
        await db.SaveChangesAsync();
        await AssertCountsMatchReports();

        // A song removed, and one restored.
        var removed = await db.Songs.FirstAsync(s => s.Album == "The Blueprint 3");
        removed.DeletedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await AssertCountsMatchReports();
        removed.DeletedAtUtc = null;
        await db.SaveChangesAsync();
        await AssertCountsMatchReports();
    }

    // The split dry run rewrites the rows it is handed with the identity it would elect — here an
    // album artist. Were it to run before the artist detector stamped and read those same rows, a
    // spelling cluster would vanish from the count.
    [Fact]
    public async Task TheSplitDryRun_DoesNotChangeWhatTheOtherTwoCount()
    {
        await using var db = NewContext();
        foreach (var artistKey in new[] { "lauryn hill", "ms lauryn hill" })
            db.CanonicalAlbums.Add(new CanonicalAlbum
            {
                ArtistKey = artistKey,
                AlbumKey = "the miseducation of lauryn hill",
                DisplayTitle = "The Miseducation of Lauryn Hill",
                DisplayArtist = "Ms. Lauryn Hill",
                Status = CanonicalAlbumStatus.Fetched,
            });
        // The two spellings exist only as album artists, tied by the id they carry.
        SongMetadata Miseducation(string path, string title, string albumArtist)
        {
            var song = Song(path, title, album: "The Miseducation of Lauryn Hill", artist: "Refugee Camp All Stars", year: 1998);
            (song.AlbumArtist, song.AlbumArtistMusicBrainzId) = (albumArtist, "mbid-lauryn");
            return song;
        }
        db.Songs.AddRange(
            Miseducation("/m/1.flac", "Lost Ones", "Lauryn Hill"),
            Miseducation("/m/2.flac", "Ex-Factor", "Lauryn Hill"),
            Miseducation("/m/3.flac", "To Zion", "Ms. Lauryn Hill"),
            Miseducation("/m/4.flac", "Doo Wop (That Thing)", "Ms. Lauryn Hill"));
        await db.SaveChangesAsync();

        var counts = await Counter(db, new DetectionCountMemo()).CountAsync(Owner);

        // The heal would move the "Lauryn Hill" half onto "Ms. Lauryn Hill" — one split album — and
        // until it does the library holds both spellings: one cluster.
        Assert.Equal(new InboxNameCounts(Artists: 1, Albums: 1), counts);
        var (artists, albumPairs, splitAlbums) = await Reports(db, Owner);
        Assert.Equal(new InboxNameCounts(artists, splitAlbums + albumPairs), counts);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    // Two artist suggestions (a misspelling, and two spellings sharing an id), one album pair, and
    // two split albums (a release split, and one whose tracks disagree on the year).
    private static void SeedLibrary(MusicHoarderDbContext db)
    {
        db.Songs.AddRange(
            Song("/g/1.flac", "Good Morning", album: "Graduation", artist: "Kanye West", year: 2007, releaseId: "rel-a"),
            Song("/g/2.flac", "Champion", album: "Graduation", artist: "Kanye West", year: 2007, releaseId: "rel-b"),
            Song("/y/1.flac", "On Sight", album: "Yeezus", artist: "Kanye Wst", year: 2013),
            Song("/b/1.mp3", "What We Talkin' About", album: "The Blueprint 3", artist: "JAY-Z", year: 2009),
            Song("/b/2.mp3", "Thank You", album: "The Blueprint 3", artist: "JAY-Z", year: 2010),
            Song("/b/3.mp3", "D.O.A.", album: "Blueprint 3", artist: "JAY-Z", year: 2009),
            Song("/l/1.mp3", "Lost Ones", album: "The Miseducation of Lauryn Hill", artist: "Lauryn Hill", year: 1998, artistMbid: "mbid-lauryn"),
            Song("/l/2.mp3", "Ex-Factor", album: "MTV Unplugged No. 2.0", artist: "Ms. Lauryn Hill", year: 2002, artistMbid: "mbid-lauryn"));
    }

    private static async Task<(int Artists, int AlbumPairs, int SplitAlbums)> Reports(MusicHoarderDbContext db, Guid owner)
    {
        var artists = await new ArtistDuplicateService(
            db, EnricherOptions(), new DetectionCountMemo(), NullLogger<ArtistDuplicateService>.Instance).DetectAsync(owner);
        var albumPairs = await new AlbumDuplicateDetector(db, EnricherOptions(), new DetectionCountMemo()).DetectAsync(owner);
        var splitAlbums = await Healer(db).DetectAsync();
        return (artists.Clusters.Count + artists.CombinedCredits.Count, albumPairs.Count, splitAlbums.Count);
    }

    private static InboxNameCounter Counter(MusicHoarderDbContext db, DetectionCountMemo memo) => new(
        db,
        new ArtistDuplicateService(db, EnricherOptions(), memo, NullLogger<ArtistDuplicateService>.Instance),
        new AlbumDuplicateDetector(db, EnricherOptions(), memo),
        Healer(db));

    private static AlbumSplitHealer Healer(MusicHoarderDbContext db) => new(
        db,
        new AlbumIdentityReconciler(),
        new DestinationPathResolver(EnricherOptions()),
        EnricherOptions(),
        NullLogger<AlbumSplitHealer>.Instance);

    private static Microsoft.Extensions.Options.IOptions<MusicEnricherOptions> EnricherOptions() =>
        Microsoft.Extensions.Options.Options.Create(
            new MusicEnricherOptions { SourceDirectory = "/source", DestinationDirectory = "/dest" });

    private static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static SongMetadata Song(
        string sourcePath,
        string title,
        string album,
        string artist,
        int? year = null,
        string? releaseId = null,
        string? artistMbid = null,
        Guid? owner = null) => new()
    {
        OwnerUserId = owner ?? Owner,
        SourcePath = sourcePath,
        FileName = Path.GetFileName(sourcePath),
        Extension = Path.GetExtension(sourcePath),
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        EnrichmentStatus = EnrichmentStatus.Matched,
        OriginalMetadataCaptured = true,
        Artist = artist,
        AlbumArtist = artist,
        Artists = artistMbid is null ? null : artist,
        ArtistMusicBrainzIds = artistMbid,
        Album = album,
        Title = title,
        Year = year,
        MusicBrainzReleaseId = releaseId,
        LibraryBuildStatus = LibraryBuildStatus.Done,
        DestinationPath = $"/dest/{artist}/{album}/{title}{Path.GetExtension(sourcePath)}",
    };
}
