using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>
/// Regression cover for a reset that looked like a delete: the endpoints loaded the song without
/// <c>.Include(s => s.ProviderAttempts)</c>, so <see cref="SongMetadata.ResetEnrichment"/>'s
/// <c>Clear()</c> was a silent no-op. The stale attempts survived — the orchestrator then skipped
/// every provider that already had a Matched attempt — and nothing enqueued the now-Pending song,
/// so it dropped out of the destination library with no way back until a restart.
/// </summary>
public class ResetEnrichmentEndpointTests
{
    [Fact]
    public async Task ResetSong_clears_provider_attempts_and_enqueues()
    {
        var dbName = Guid.NewGuid().ToString("N");
        int songId;

        await using (var seed = CreateDb(dbName))
        {
            var song = NewSong(EnrichmentStatus.Matched);
            seed.Songs.Add(song);
            await seed.SaveChangesAsync();
            songId = song.Id;
            seed.SongProviderAttempts.Add(MatchedAttempt(songId, EnrichmentProvider.SpotifyAPI));
            seed.SongProviderAttempts.Add(MatchedAttempt(songId, EnrichmentProvider.Deezer));
            await seed.SaveChangesAsync();
        }

        // A fresh context is the point: with the seeding context EF's relationship fix-up would
        // populate ProviderAttempts for free and hide the missing Include.
        var (channel, _, _) = NewChannel();
        await using (var db = CreateDb(dbName))
        {
            var result = await SongsEndpoints.ResetSongEnrichment(songId, db, channel);
            Assert.IsNotType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(result);
        }

        await using (var verify = CreateDb(dbName))
        {
            Assert.Empty(await verify.SongProviderAttempts.Where(a => a.SongId == songId).ToListAsync());
            var song = await verify.Songs.FirstAsync(s => s.Id == songId);
            Assert.Equal(EnrichmentStatus.Pending, song.EnrichmentStatus);
            Assert.Equal(LibraryBuildStatus.Pending, song.LibraryBuildStatus);
        }

        Assert.Equal(1, channel.InFlight);
        Assert.True(channel.Reader.TryRead(out var queuedId));
        Assert.Equal(songId, queuedId);
    }

    [Fact]
    public async Task ResetSong_locked_song_is_rejected_and_not_enqueued()
    {
        var dbName = Guid.NewGuid().ToString("N");
        int songId;

        await using (var seed = CreateDb(dbName))
        {
            var song = NewSong(EnrichmentStatus.Matched);
            song.LockManualApproval();
            seed.Songs.Add(song);
            await seed.SaveChangesAsync();
            songId = song.Id;
            seed.SongProviderAttempts.Add(MatchedAttempt(songId, EnrichmentProvider.SpotifyAPI));
            await seed.SaveChangesAsync();
        }

        var (channel, _, _) = NewChannel();
        await using (var db = CreateDb(dbName))
            await SongsEndpoints.ResetSongEnrichment(songId, db, channel);

        await using (var verify = CreateDb(dbName))
        {
            Assert.Single(await verify.SongProviderAttempts.Where(a => a.SongId == songId).ToListAsync());
            Assert.Equal(EnrichmentStatus.Matched, (await verify.Songs.FirstAsync(s => s.Id == songId)).EnrichmentStatus);
        }

        Assert.Equal(0, channel.InFlight);
    }

    [Fact]
    public async Task ResetBatch_clears_provider_attempts_and_enqueues()
    {
        var dbName = Guid.NewGuid().ToString("N");
        int songId;

        await using (var seed = CreateDb(dbName))
        {
            var song = NewSong(EnrichmentStatus.Matched);
            seed.Songs.Add(song);
            await seed.SaveChangesAsync();
            songId = song.Id;
            seed.SongProviderAttempts.Add(MatchedAttempt(songId, EnrichmentProvider.SpotifyAPI));
            await seed.SaveChangesAsync();
        }

        var (channel, _, _) = NewChannel();
        await using (var db = CreateDb(dbName))
            await SongsEndpoints.ResetEnrichmentBatch(new EnrichmentResetRequest("matched"), db, channel);

        await using (var verify = CreateDb(dbName))
        {
            Assert.Empty(await verify.SongProviderAttempts.Where(a => a.SongId == songId).ToListAsync());
            Assert.Equal(EnrichmentStatus.Pending, (await verify.Songs.FirstAsync(s => s.Id == songId)).EnrichmentStatus);
        }

        Assert.Equal(1, channel.InFlight);
        Assert.True(channel.Reader.TryRead(out var queuedId));
        Assert.Equal(songId, queuedId);
    }

    private static (EnrichmentPipelineChannel channel, JobManager jobs, EnrichmentProgressTracker tracker) NewChannel()
    {
        var jobs = new JobManager();
        var tracker = new EnrichmentProgressTracker();
        return (new EnrichmentPipelineChannel(jobs, tracker), jobs, tracker);
    }

    private static SongProviderAttempt MatchedAttempt(int songId, EnrichmentProvider provider) => new()
    {
        SongId = songId,
        Provider = provider,
        Status = ProviderAttemptStatus.Matched,
        AttemptedAtUtc = DateTime.UtcNow.AddDays(-20),
    };

    private static SongMetadata NewSong(EnrichmentStatus status) => new()
    {
        OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = $"/source/{Guid.NewGuid():N}.opus",
        FileName = "song.opus",
        Extension = ".opus",
        FileSizeBytes = 1000,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = "Saint Levant",
        Title = "MITSUBISHI",
        EnrichmentStatus = status,
        LibraryBuildStatus = LibraryBuildStatus.Done,
        DestinationPath = "/dest/Saint Levant/2026 - MITSUBISHI/01 - MITSUBISHI.opus",
    };

    private static MusicHoarderDbContext CreateDb(string name)
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new MusicHoarderDbContext(options);
    }
}
