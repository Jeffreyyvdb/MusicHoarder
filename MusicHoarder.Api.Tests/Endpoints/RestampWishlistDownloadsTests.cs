using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>
/// Covers the one-time backfill that heals wishlist downloads poisoned by the downloader's native
/// YouTube tags (<see cref="EnrichmentEndpoints.RestampWishlistDownloadsAsync"/>): it re-stamps the
/// file and DB row from the authoritative WishlistItem identity, then resets + re-enqueues.
/// </summary>
public class RestampWishlistDownloadsTests : IDisposable
{
    private static readonly Guid Owner = WellKnownUsers.OwnerId;
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private readonly List<string> tempFiles = [];

    [Fact]
    public async Task Restamp_FixesPoisonedSongIdentityFileAndDb_AndRequeues()
    {
        await using var db = CreateDbContext();
        var file = CopyFixtureToTemp("silence.mp3");
        using (var seed = TagLib.File.Create(file))
        {
            seed.Tag.Performers = ["현장검거"];          // YouTube channel as artist
            seed.Tag.Title = "¥$ - PROBLEMATIC [가사]";   // full video title
            seed.Save();
        }

        var song = NewSong(file);
        song.Artist = "현장검거";
        song.Title = "¥$ - PROBLEMATIC [가사]";
        song.EnrichmentStatus = EnrichmentStatus.NeedsReview;
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = EnrichmentProvider.SpotifyAPI,
            Status = ProviderAttemptStatus.NoMatch,
            AttemptedAtUtc = DateTime.UtcNow,
        });
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        db.WishlistItems.Add(new WishlistItem
        {
            OwnerUserId = Owner,
            SpotifyTrackId = "track-1",
            Artist = "¥$",
            Title = "PROBLEMATIC",
            Album = "VULTURES 1",
            Isrc = "USUG12400001",
            DurationMs = 200_000,
            Status = WishlistItemStatus.Downloaded,
            DownloadedSongId = song.Id,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var enqueued = new List<int>();
        var (requeued, fileStamped) = await EnrichmentEndpoints.RestampWishlistDownloadsAsync(
            db, Owner, ids => enqueued.AddRange(ids), NullLogger.Instance, default);

        Assert.Equal(1, requeued);
        Assert.Equal(1, fileStamped);
        Assert.Equal([song.Id], enqueued);

        // DB row identity now comes from the WishlistItem, attempts cleared, back to Pending.
        var reloaded = await db.Songs.Include(s => s.ProviderAttempts).SingleAsync(s => s.Id == song.Id);
        Assert.Equal("¥$", reloaded.Artist);
        Assert.Equal("PROBLEMATIC", reloaded.Title);
        Assert.Equal("VULTURES 1", reloaded.Album);
        Assert.Equal("USUG12400001", reloaded.Isrc);
        Assert.Empty(reloaded.ProviderAttempts);
        Assert.Equal(EnrichmentStatus.Pending, reloaded.EnrichmentStatus);

        // File tags were rewritten too (durable against a future re-scan).
        using var written = TagLib.File.Create(file);
        Assert.Equal(["¥$"], written.Tag.Performers);
        Assert.Equal("PROBLEMATIC", written.Tag.Title);
    }

    [Fact]
    public async Task Restamp_MissingFile_StillFixesDbRowAndRequeues()
    {
        await using var db = CreateDbContext();
        var song = NewSong("/data/downloads/gone.opus"); // no such file on disk
        song.Artist = "Channel";
        song.Title = "vid title";
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        db.WishlistItems.Add(new WishlistItem
        {
            OwnerUserId = Owner,
            SpotifyTrackId = "track-1",
            Artist = "Amy Macdonald",
            Title = "This Is the Life",
            DurationMs = 185_000,
            Status = WishlistItemStatus.Downloaded,
            DownloadedSongId = song.Id,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var enqueued = new List<int>();
        var (requeued, fileStamped) = await EnrichmentEndpoints.RestampWishlistDownloadsAsync(
            db, Owner, ids => enqueued.AddRange(ids), NullLogger.Instance, default);

        Assert.Equal(1, requeued);
        Assert.Equal(0, fileStamped); // file write skipped, DB still fixed
        var reloaded = await db.Songs.SingleAsync(s => s.Id == song.Id);
        Assert.Equal("Amy Macdonald", reloaded.Artist);
        Assert.Equal("This Is the Life", reloaded.Title);
    }

    [Fact]
    public async Task Restamp_SkipsManuallyApprovedSongs()
    {
        await using var db = CreateDbContext();
        var song = NewSong("/data/downloads/locked.opus");
        song.LockManualApproval(); // owner approved this match; must not be clobbered
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        db.WishlistItems.Add(new WishlistItem
        {
            OwnerUserId = Owner,
            SpotifyTrackId = "track-1",
            Artist = "Real Artist",
            Title = "Real Title",
            DurationMs = 200_000,
            Status = WishlistItemStatus.Downloaded,
            DownloadedSongId = song.Id,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var enqueued = new List<int>();
        var (requeued, _) = await EnrichmentEndpoints.RestampWishlistDownloadsAsync(
            db, Owner, ids => enqueued.AddRange(ids), NullLogger.Instance, default);

        Assert.Equal(0, requeued);
        Assert.Empty(enqueued);
    }

    private static SongMetadata NewSong(string sourcePath) => new()
    {
        OwnerUserId = Owner,
        SourcePath = sourcePath,
        FileName = Path.GetFileName(sourcePath),
        Extension = Path.GetExtension(sourcePath),
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
    };

    private string CopyFixtureToTemp(string fixtureName)
    {
        var source = Path.Combine(FixtureDir, fixtureName);
        var dest = Path.Combine(Path.GetTempPath(), $"mh-restamp-{Guid.NewGuid():N}{Path.GetExtension(fixtureName)}");
        File.Copy(source, dest, overwrite: true);
        tempFiles.Add(dest);
        return dest;
    }

    private static MusicHoarderDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    public void Dispose()
    {
        foreach (var f in tempFiles)
        {
            try { File.Delete(f); } catch { /* best effort */ }
        }
    }
}
