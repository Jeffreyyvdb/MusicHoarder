using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Library;

public class LibraryBuildQueryTests
{
    private static readonly DateTime Now = new(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Cutoff = Now.AddMinutes(-5);
    private const int MaxAttempts = 5;

    [Fact]
    public async Task FreshMatch_WaitingOnLyrics_IsExcludedUntilLyricsResolveOrWaitElapses()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            // 1: fresh match, lyrics still pending, enriched just now → held back.
            Song(1, LyricsStatus.NotFetched, enrichedAtUtc: Now),
            // 2: lyrics resolved → eligible.
            Song(2, LyricsStatus.Fetched, enrichedAtUtc: Now),
            // 3: lyrics not found is terminal → eligible.
            Song(3, LyricsStatus.NotFound, enrichedAtUtc: Now),
            // 4: lyrics still pending but the match is older than the wait window → build anyway.
            Song(4, LyricsStatus.NotFetched, enrichedAtUtc: Now.AddMinutes(-30)),
            // 5: forced re-tag (PreviousDestinationPath) never waits on lyrics.
            Song(5, LyricsStatus.NotFetched, enrichedAtUtc: Now, previousDest: "/dest/5.flac"),
            // 6: no title → can't fetch lyrics, so don't hold the build.
            Song(6, LyricsStatus.NotFetched, enrichedAtUtc: Now, title: null));
        await db.SaveChangesAsync();

        var eligible = await LibraryBuildQuery
            .BuildCandidates(db.Songs.IgnoreQueryFilters(), Cutoff, MaxAttempts)
            .Select(s => s.Id)
            .OrderBy(id => id)
            .ToListAsync();

        Assert.Equal(new[] { 2, 3, 4, 5, 6 }, eligible);
    }

    [Fact]
    public async Task DisabledGate_IncludesFreshMatchEvenWithoutLyrics()
    {
        await using var db = CreateDbContext();
        db.Songs.Add(Song(1, LyricsStatus.NotFetched, enrichedAtUtc: Now));
        await db.SaveChangesAsync();

        var eligible = await LibraryBuildQuery
            .BuildCandidates(db.Songs.IgnoreQueryFilters(), lyricsWaitCutoff: null, MaxAttempts)
            .Select(s => s.Id)
            .ToListAsync();

        Assert.Equal(new[] { 1 }, eligible);
    }

    [Fact]
    public async Task NonBuildable_AreAlwaysExcluded()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            Song(1, LyricsStatus.Fetched, enrichedAtUtc: Now, status: LibraryBuildStatus.Done, dest: "/dest/1.flac"),
            Song(2, LyricsStatus.Fetched, enrichedAtUtc: Now, isDuplicate: true),
            Song(3, LyricsStatus.Fetched, enrichedAtUtc: Now, isSynthetic: true),
            Song(4, LyricsStatus.Fetched, enrichedAtUtc: Now, deleted: true),
            Song(5, LyricsStatus.Fetched, enrichedAtUtc: Now, enrichment: EnrichmentStatus.NeedsReview),
            // Demo rows stream off a read-only mount — never buildable, even when force-flagged
            // for a re-tag (the exact state a pre-exclusion sweep left them in).
            Song(6, LyricsStatus.Fetched, enrichedAtUtc: Now,
                owner: MusicHoarder.Api.Auth.WellKnownUsers.DemoId,
                status: LibraryBuildStatus.Pending, dest: "/demo/6.flac", previousDest: "/demo/6.flac"));
        await db.SaveChangesAsync();

        var eligible = await LibraryBuildQuery
            .BuildCandidates(db.Songs.IgnoreQueryFilters(), Cutoff, MaxAttempts)
            .ToListAsync();

        Assert.Empty(eligible);
    }

    [Fact]
    public async Task FailedTrack_IsRetriedUntilAttemptCapThenQuarantined()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            // Failed but under the cap → still eligible for another attempt.
            Song(1, LyricsStatus.Fetched, enrichedAtUtc: Now,
                status: LibraryBuildStatus.Failed, failedAttempts: MaxAttempts - 1),
            // Failed and at the cap → quarantined out of the queue.
            Song(2, LyricsStatus.Fetched, enrichedAtUtc: Now,
                status: LibraryBuildStatus.Failed, failedAttempts: MaxAttempts));
        await db.SaveChangesAsync();

        var eligible = await LibraryBuildQuery
            .BuildCandidates(db.Songs.IgnoreQueryFilters(), Cutoff, MaxAttempts)
            .Select(s => s.Id)
            .ToListAsync();

        Assert.Equal(new[] { 1 }, eligible);
    }

    [Fact]
    public async Task BuildNeedsReview_IncludesNeedsReviewOnlyWhenEnabled()
    {
        await using var db = CreateDbContext();
        db.Songs.AddRange(
            // 1: normal Matched track — eligible either way.
            Song(1, LyricsStatus.Fetched, enrichedAtUtc: Now),
            // 2: NeedsReview — only eligible once the opt-in flag is on.
            Song(2, LyricsStatus.Fetched, enrichedAtUtc: Now, enrichment: EnrichmentStatus.NeedsReview),
            // 3: NeedsReview but a duplicate — the flag must not override the other exclusions.
            Song(3, LyricsStatus.Fetched, enrichedAtUtc: Now,
                enrichment: EnrichmentStatus.NeedsReview, isDuplicate: true),
            // 4: still-Pending enrichment stays excluded even with the flag on.
            Song(4, LyricsStatus.Fetched, enrichedAtUtc: Now, enrichment: EnrichmentStatus.Pending));
        await db.SaveChangesAsync();

        var withoutFlag = await LibraryBuildQuery
            .BuildCandidates(db.Songs.IgnoreQueryFilters(), Cutoff, MaxAttempts, buildNeedsReview: false)
            .Select(s => s.Id).OrderBy(id => id).ToListAsync();
        Assert.Equal(new[] { 1 }, withoutFlag);

        var withFlag = await LibraryBuildQuery
            .BuildCandidates(db.Songs.IgnoreQueryFilters(), Cutoff, MaxAttempts, buildNeedsReview: true)
            .Select(s => s.Id).OrderBy(id => id).ToListAsync();
        Assert.Equal(new[] { 1, 2 }, withFlag);
    }

    private static SongMetadata Song(
        int id,
        LyricsStatus lyricsStatus,
        DateTime enrichedAtUtc,
        string? title = "t",
        string? previousDest = null,
        string? dest = null,
        LibraryBuildStatus status = LibraryBuildStatus.Pending,
        EnrichmentStatus enrichment = EnrichmentStatus.Matched,
        bool isDuplicate = false,
        bool isSynthetic = false,
        bool deleted = false,
        int failedAttempts = 0,
        Guid? owner = null) => new()
    {
        Id = id,
        OwnerUserId = owner ?? MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = $"/source/{id}.flac",
        FileName = $"{id}.flac",
        Extension = ".flac",
        FileSizeBytes = 1,
        LastModifiedUtc = Now,
        IndexedAtUtc = Now,
        Title = title,
        Artist = "Artist",
        Album = "Album",
        EnrichmentStatus = enrichment,
        EnrichedAtUtc = enrichedAtUtc,
        LyricsStatus = lyricsStatus,
        LibraryBuildStatus = status,
        LibraryBuildAttempts = failedAttempts,
        DestinationPath = dest,
        PreviousDestinationPath = previousDest,
        IsDuplicate = isDuplicate,
        IsSynthetic = isSynthetic,
        DeletedAtUtc = deleted ? Now : null,
    };

    private static MusicHoarderDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
}
