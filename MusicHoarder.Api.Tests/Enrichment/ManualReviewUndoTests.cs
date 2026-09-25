using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

/// <summary>
/// Regression cover for an Inbox approval that could not be undone: the manual-review approve path
/// wrote the reviewer's values straight onto the row without <see cref="SongMetadata.CaptureOriginalMetadata"/>,
/// so a later <see cref="SongMetadata.ResetEnrichment"/> with <c>restoreOriginal</c> had no snapshot to
/// restore and the file's own tags were gone for good.
/// </summary>
public class ManualReviewUndoTests
{
    [Fact]
    public async Task Approve_then_forced_reset_restores_the_pre_approve_tags()
    {
        var dbName = Guid.NewGuid().ToString("N");
        int songId;

        await using (var seed = CreateDb(dbName))
        {
            var song = NeedsReviewSong();
            seed.Songs.Add(song);
            await seed.SaveChangesAsync();
            songId = song.Id;
            seed.SongProviderAttempts.Add(new SongProviderAttempt
            {
                SongId = songId,
                Provider = EnrichmentProvider.SpotifyAPI,
                Status = ProviderAttemptStatus.Matched,
                AttemptedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            });
            await seed.SaveChangesAsync();
        }

        await using (var db = CreateDb(dbName))
        {
            var result = await SongsEndpoints.ManualReviewTrack(songId, new ManualReviewRequest(
                Decision: "approve",
                Artist: "Saint Levant",
                AlbumArtist: "Saint Levant",
                Album: "Deira",
                Title: "MITSUBISHI",
                Year: 2026,
                TrackNumber: 3), db);
            Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        }

        await using (var approved = CreateDb(dbName))
        {
            var song = await approved.Songs.FirstAsync(s => s.Id == songId);
            Assert.Equal(EnrichmentStatus.Matched, song.EnrichmentStatus);
            Assert.True(song.IsManuallyApproved);
            Assert.Equal("MITSUBISHI", song.Title);
        }

        // The Inbox's Undo: the approval locked the song, so the reset has to be forced.
        var channel = new EnrichmentPipelineChannel(new JobManager(), new EnrichmentProgressTracker());
        await using (var db = CreateDb(dbName))
            await SongsEndpoints.ResetSongEnrichment(songId, db, channel, restoreOriginalMetadata: true, force: true);

        await using (var verify = CreateDb(dbName))
        {
            var song = await verify.Songs.FirstAsync(s => s.Id == songId);
            Assert.Equal("saint levant", song.Artist);
            Assert.Null(song.AlbumArtist);
            Assert.Equal("Untitled Demos", song.Album);
            Assert.Equal("mitsubishi (demo)", song.Title);
            Assert.Null(song.Year);
            Assert.Equal(7, song.TrackNumber);
            Assert.Equal(EnrichmentStatus.Pending, song.EnrichmentStatus);
            Assert.False(song.IsManuallyApproved);
        }

        Assert.True(channel.Reader.TryRead(out var queuedId));
        Assert.Equal(songId, queuedId);
    }

    [Fact]
    public async Task Approve_keeps_a_snapshot_an_earlier_match_already_captured()
    {
        // The snapshot means "the file's own tags", not "whatever the row said last": a song that was
        // matched once and later reset without restoring must still undo to the file's tags.
        await using var db = CreateDb(Guid.NewGuid().ToString("N"));
        var song = NeedsReviewSong();
        song.CaptureOriginalMetadata();
        song.Title = "MITSUBISHI (a provider's spelling)";
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        await SongsEndpoints.ManualReviewTrack(song.Id, new ManualReviewRequest("approve", Title: "MITSUBISHI"), db);

        Assert.Equal("MITSUBISHI", song.Title);
        song.ResetEnrichment(restoreOriginal: true, force: true);
        Assert.Equal("mitsubishi (demo)", song.Title);
    }

    private static SongMetadata NeedsReviewSong() => new()
    {
        OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = $"/source/{Guid.NewGuid():N}.opus",
        FileName = "07 mitsubishi (demo).opus",
        Extension = ".opus",
        FileSizeBytes = 1000,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = "saint levant",
        Album = "Untitled Demos",
        Title = "mitsubishi (demo)",
        TrackNumber = 7,
        EnrichmentStatus = EnrichmentStatus.NeedsReview,
        MatchedBy = "SpotifyAPI",
        MatchConfidence = 0.6,
    };

    private static MusicHoarderDbContext CreateDb(string name)
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new MusicHoarderDbContext(options);
    }
}
