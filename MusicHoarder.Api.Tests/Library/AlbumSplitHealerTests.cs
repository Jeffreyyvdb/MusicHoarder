using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Library;

public class AlbumSplitHealerTests
{
    private static readonly DateTime EnrichedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task HealAsync_YearAndReleaseSplit_ConvergesMinorityOntoElectedIdentity()
    {
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a1.flac", title: "Track 1", trackNumber: 1, year: 2007, releaseId: "rel-a"),
            Song("/a2.flac", title: "Track 2", trackNumber: 2, year: 2007, releaseId: "rel-a"),
            Song("/a3.flac", title: "Track 3", trackNumber: 3, year: 2008, releaseId: "rel-b"));
        await db.SaveChangesAsync();

        var result = await Healer(db).HealAsync();

        Assert.Equal(1, result.GroupsHealed);
        Assert.Equal(1, result.SongsCorrected);
        Assert.Equal(1, result.SongsRequeued);

        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.All(songs, s => Assert.Equal(2007, s.Year));
        Assert.All(songs, s => Assert.Equal("rel-a", s.MusicBrainzReleaseId));

        // Majority members already matched the elected identity — untouched, still Done.
        Assert.Equal(LibraryBuildStatus.Done, songs[0].LibraryBuildStatus);
        Assert.Equal(LibraryBuildStatus.Done, songs[1].LibraryBuildStatus);

        // The corrected member is re-queued with the force-rebuild signal, keeping DestinationPath
        // so the relocate flow can prune the old folder.
        var healed = songs[2];
        Assert.Equal(LibraryBuildStatus.Pending, healed.LibraryBuildStatus);
        Assert.NotNull(healed.DestinationPath);
        Assert.Equal(healed.DestinationPath, healed.PreviousDestinationPath);

        // Track-level fields are never touched.
        Assert.Equal(3, healed.TrackNumber);
        Assert.Equal("Track 3", healed.Title);

        // Grade-staleness guard: enrichment timestamp untouched, so no auto-regrade is triggered.
        Assert.All(songs, s => Assert.Equal(EnrichedAt, s.EnrichedAtUtc));

        // Reversible history recorded against the heal source.
        var changes = await db.SongMetadataChanges.ToListAsync();
        Assert.NotEmpty(changes);
        Assert.All(changes, c => Assert.Equal("album-identity-heal", c.Source));
        Assert.All(changes, c => Assert.NotNull(c.AppliedAtUtc));
        Assert.All(changes, c => Assert.Equal(songs[2].Id, c.SongId));
    }

    [Fact]
    public async Task HealAsync_CrossFolderTitleVariant_UnifiesAlbumString()
    {
        await using var db = NewContext();
        // Same album, one track enriched with a diacritic variant — resolves to a different
        // destination folder, so the folder-keyed build reconciliation never saw them together.
        db.Songs.AddRange(
            Song("/b1.flac", title: "One", trackNumber: 1, album: "Believe"),
            Song("/b2.flac", title: "Two", trackNumber: 2, album: "Believe"),
            Song("/b3.flac", title: "Three", trackNumber: 3, album: "Belíeve"));
        await db.SaveChangesAsync();

        var result = await Healer(db).HealAsync();

        Assert.Equal(1, result.GroupsHealed);
        var songs = await db.Songs.ToListAsync();
        Assert.All(songs, s => Assert.Equal("Believe", s.Album));
    }

    [Fact]
    public async Task HealAsync_NotYetBuiltMember_CorrectedButNotRequeued()
    {
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/c1.flac", title: "One", trackNumber: 1, year: 2007),
            Song("/c2.flac", title: "Two", trackNumber: 2, year: 2007),
            Song("/c3.flac", title: "Three", trackNumber: 3, year: 2008, buildStatus: LibraryBuildStatus.Pending, destinationPath: null));
        await db.SaveChangesAsync();

        var result = await Healer(db).HealAsync();

        Assert.Equal(1, result.SongsCorrected);
        Assert.Equal(0, result.SongsRequeued);

        var pending = await db.Songs.SingleAsync(s => s.Title == "Three");
        Assert.Equal(2007, pending.Year); // corrected, so it builds straight into the unified folder
        Assert.Equal(LibraryBuildStatus.Pending, pending.LibraryBuildStatus);
        Assert.Null(pending.PreviousDestinationPath);
    }

    [Fact]
    public async Task HealAsync_IsIdempotent()
    {
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/d1.flac", title: "One", trackNumber: 1, year: 2007),
            Song("/d2.flac", title: "Two", trackNumber: 2, year: 2008));
        await db.SaveChangesAsync();

        var first = await Healer(db).HealAsync();
        Assert.Equal(1, first.GroupsHealed);
        var changeCount = await db.SongMetadataChanges.CountAsync();

        var second = await Healer(db).HealAsync();

        Assert.Equal(new AlbumSplitHealResult(0, 0, 0), second);
        Assert.Equal(changeCount, await db.SongMetadataChanges.CountAsync());
    }

    [Fact]
    public async Task HealAsync_IneligibleRows_NeverTouched()
    {
        await using var db = NewContext();
        var deleted = Song("/e1.flac", title: "One", trackNumber: 1, year: 2008);
        deleted.SoftDelete();
        var duplicate = Song("/e2.flac", title: "Two", trackNumber: 2, year: 2008);
        duplicate.MarkAsDuplicate(1);
        var unreleased = Song("/e3.flac", title: "Three", trackNumber: 3, year: 2008);
        unreleased.IsUnreleased = true;
        var synthetic = Song("/e4.flac", title: "Four", trackNumber: 4, year: 2008);
        synthetic.IsSynthetic = true;
        var needsReview = Song("/e5.flac", title: "Five", trackNumber: 5, year: 2008);
        needsReview.EnrichmentStatus = EnrichmentStatus.NeedsReview;

        db.Songs.AddRange(
            Song("/e6.flac", title: "Six", trackNumber: 6, year: 2007),
            Song("/e7.flac", title: "Seven", trackNumber: 7, year: 2007),
            deleted, duplicate, unreleased, synthetic, needsReview);
        await db.SaveChangesAsync();

        var result = await Healer(db).HealAsync();

        // The two eligible 2007 members already agree — nothing heals, and none of the ineligible
        // 2008 rows were pulled into the vote or corrected.
        Assert.Equal(new AlbumSplitHealResult(0, 0, 0), result);
        var untouched = await db.Songs.IgnoreQueryFilters().Where(s => s.Year == 2008).ToListAsync();
        Assert.Equal(5, untouched.Count);
    }

    [Fact]
    public async Task HealAsync_GroupsPerUser_NeverHealAcrossOwners()
    {
        await using var db = NewContext();
        var otherUser = Guid.NewGuid();
        var other1 = Song("/f3.flac", title: "Three", trackNumber: 1, year: 2010, releaseId: "rel-other");
        other1.OwnerUserId = otherUser;
        var other2 = Song("/f4.flac", title: "Four", trackNumber: 2, year: 2010, releaseId: "rel-other");
        other2.OwnerUserId = otherUser;

        db.Songs.AddRange(
            Song("/f1.flac", title: "One", trackNumber: 1, year: 2007, releaseId: "rel-a"),
            Song("/f2.flac", title: "Two", trackNumber: 2, year: 2008, releaseId: "rel-a"),
            other1, other2);
        await db.SaveChangesAsync();

        var result = await Healer(db).HealAsync();

        // Owner's split heals; the other user's internally-consistent copy is not dragged in.
        Assert.Equal(1, result.GroupsHealed);
        var others = await db.Songs.IgnoreQueryFilters().Where(s => s.OwnerUserId == otherUser).ToListAsync();
        Assert.All(others, s => Assert.Equal(2010, s.Year));
        Assert.All(others, s => Assert.Equal("rel-other", s.MusicBrainzReleaseId));
    }

    [Fact]
    public async Task HealAsync_DemoRows_NeverTouched()
    {
        await using var db = NewContext();
        // The demo library is seeded terminal with DestinationPath == SourcePath (read-only mount).
        // A split here must NOT be healed/re-queued, or the builder would try to delete the source.
        var demo1 = Song("/demo/d1.flac", title: "One", trackNumber: 1, year: 2012, destinationPath: "/demo/d1.flac");
        demo1.OwnerUserId = WellKnownUsers.DemoId;
        var demo2 = Song("/demo/d2.flac", title: "Two", trackNumber: 2, year: 2013, destinationPath: "/demo/d2.flac");
        demo2.OwnerUserId = WellKnownUsers.DemoId;
        db.Songs.AddRange(demo1, demo2);
        await db.SaveChangesAsync();

        var result = await Healer(db).HealAsync();

        Assert.Equal(new AlbumSplitHealResult(0, 0, 0), result);
        var demos = await db.Songs.IgnoreQueryFilters()
            .Where(s => s.OwnerUserId == WellKnownUsers.DemoId).ToListAsync();
        Assert.All(demos, s => Assert.Equal(LibraryBuildStatus.Done, s.LibraryBuildStatus));
        Assert.All(demos, s => Assert.Null(s.PreviousDestinationPath));
        Assert.All(demos, s => Assert.Equal(s.SourcePath, s.DestinationPath));
    }

    [Fact]
    public async Task HealAsync_DeluxeEdition_NotMergedIntoStandard()
    {
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/g1.flac", title: "One", trackNumber: 1, album: "Graduation", year: 2007),
            Song("/g2.flac", title: "One", trackNumber: 1, album: "Graduation (Deluxe Edition)", year: 2008));
        await db.SaveChangesAsync();

        var result = await Healer(db).HealAsync();

        Assert.Equal(new AlbumSplitHealResult(0, 0, 0), result);
        Assert.Equal(2008, (await db.Songs.SingleAsync(s => s.Album!.Contains("Deluxe"))).Year);
    }

    [Fact]
    public async Task DetectAsync_ReportsSplit_WithoutPersistingAnything()
    {
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/h1.flac", title: "One", trackNumber: 1, year: 2007, releaseId: "rel-a"),
            Song("/h2.flac", title: "Two", trackNumber: 2, year: 2008, releaseId: "rel-b"));
        await db.SaveChangesAsync();

        var report = await Healer(db).DetectAsync();

        var group = Assert.Single(report);
        Assert.Equal(2, group.MemberCount);
        Assert.True(group.MembersNeedingCorrection >= 1);
        Assert.Equal(new[] { "rel-a", "rel-b" }, group.DistinctReleaseIds);
        Assert.Equal(2, group.DistinctFolders.Count); // year split = two destination folders
        Assert.NotNull(group.ElectedIdentity.MusicBrainzReleaseId);

        // Dry run: rows and change log untouched.
        var songs = await db.Songs.ToListAsync();
        Assert.All(songs, s => Assert.Equal(LibraryBuildStatus.Done, s.LibraryBuildStatus));
        Assert.Contains(songs, s => s.Year == 2008);
        Assert.Empty(await db.SongMetadataChanges.ToListAsync());
    }

    private static MusicHoarderDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private static IAlbumSplitHealer Healer(MusicHoarderDbContext db) => new AlbumSplitHealer(
        db,
        new AlbumIdentityReconciler(),
        new DestinationPathResolver(Microsoft.Extensions.Options.Options.Create(
            new MusicEnricherOptions { SourceDirectory = "/source", DestinationDirectory = "/dest" })),
        NullLogger<AlbumSplitHealer>.Instance);

    private static SongMetadata Song(
        string sourcePath,
        string title,
        int trackNumber,
        int? year = 2007,
        string album = "Graduation",
        string? releaseId = null,
        LibraryBuildStatus buildStatus = LibraryBuildStatus.Done,
        string? destinationPath = "unset") => new()
    {
        OwnerUserId = WellKnownUsers.OwnerId,
        SourcePath = sourcePath,
        FileName = Path.GetFileName(sourcePath),
        Extension = Path.GetExtension(sourcePath),
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        EnrichmentStatus = EnrichmentStatus.Matched,
        EnrichedAtUtc = EnrichedAt,
        OriginalMetadataCaptured = true,
        Artist = "Kanye West",
        AlbumArtist = "Kanye West",
        Album = album,
        Title = title,
        TrackNumber = trackNumber,
        Year = year,
        MusicBrainzReleaseId = releaseId,
        LibraryBuildStatus = buildStatus,
        DestinationPath = destinationPath == "unset"
            ? $"/dest/Kanye West/{year} - {album}/{trackNumber:00} - {title}.flac"
            : destinationPath,
    };
}
