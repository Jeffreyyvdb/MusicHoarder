using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Endpoints;

public class PipelineSummaryTests
{
    [Fact]
    public async Task BuildSummary_CountsStatusesProviderOutcomesAndErrors_ExcludingDeleted()
    {
        await using var db = NewContext();

        // Matched + built, with two provider attempts.
        var matched = NewSong("/a.mp3", "a.mp3");
        matched.EnrichmentStatus = EnrichmentStatus.Matched;
        matched.LibraryBuildStatus = LibraryBuildStatus.Done;
        matched.LyricsStatus = LyricsStatus.Fetched;
        matched.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = EnrichmentProvider.AcoustID,
            Status = ProviderAttemptStatus.Matched,
            AttemptedAtUtc = DateTime.UtcNow,
        });
        matched.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = EnrichmentProvider.SpotifyAPI,
            Status = ProviderAttemptStatus.NoMatch,
            AttemptedAtUtc = DateTime.UtcNow,
        });

        // NeedsReview with an enrichment error.
        var review = NewSong("/b.mp3", "b.mp3");
        review.EnrichmentStatus = EnrichmentStatus.NeedsReview;
        review.EnrichmentError = "low confidence";
        review.EnrichmentLastAttemptedAtUtc = DateTime.UtcNow;

        // Failed build error + duplicate flag.
        var failed = NewSong("/c.mp3", "c.mp3");
        failed.EnrichmentStatus = EnrichmentStatus.Matched;
        failed.LibraryBuildStatus = LibraryBuildStatus.Failed;
        failed.LibraryBuildError = "disk full";
        failed.LibraryBuildLastAttemptedAtUtc = DateTime.UtcNow;
        failed.IsDuplicate = true;

        // Deleted song must be excluded entirely.
        var deleted = NewSong("/d.mp3", "d.mp3");
        deleted.EnrichmentStatus = EnrichmentStatus.Matched;
        deleted.SoftDelete();
        deleted.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = EnrichmentProvider.AcoustID,
            Status = ProviderAttemptStatus.Failed,
            AttemptedAtUtc = DateTime.UtcNow,
        });

        db.Songs.AddRange(matched, review, failed, deleted);
        await db.SaveChangesAsync();

        var summary = await DebugEndpoints.BuildSummaryAsync(db);

        Assert.Equal(3, summary.TotalSongs);
        Assert.Equal(1, summary.DuplicateCount);

        Assert.Equal(2, summary.EnrichmentStatus["Matched"]);
        Assert.Equal(1, summary.EnrichmentStatus["NeedsReview"]);
        Assert.Equal(1, summary.LibraryBuildStatus["Done"]);
        Assert.Equal(1, summary.LibraryBuildStatus["Failed"]);

        // Deleted song's AcoustID/Failed attempt must not be counted.
        Assert.Contains(summary.ProviderAttempts, p => p is { Provider: "AcoustID", Status: "Matched", Count: 1 });
        Assert.Contains(summary.ProviderAttempts, p => p is { Provider: "SpotifyAPI", Status: "NoMatch", Count: 1 });
        Assert.DoesNotContain(summary.ProviderAttempts, p => p.Status == "Failed");

        Assert.Contains(summary.RecentErrors, e => e is { Stage: "Enrichment", Error: "low confidence" });
        Assert.Contains(summary.RecentErrors, e => e is { Stage: "LibraryBuild", Error: "disk full" });
    }

    private static SongMetadata NewSong(string sourcePath, string fileName) => new()
    {
        OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = sourcePath,
        FileName = fileName,
        Extension = Path.GetExtension(fileName),
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
    };

    private static MusicHoarderDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }
}
