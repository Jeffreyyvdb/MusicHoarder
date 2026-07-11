using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Sync;

namespace MusicHoarder.Api.Tests.Sync;

public class SyncIngestServiceTests : IDisposable
{
    private static readonly Guid Owner = WellKnownUsers.OwnerId;
    private readonly string syncedDir;

    public SyncIngestServiceTests()
    {
        syncedDir = Path.Combine(Path.GetTempPath(), $"mh-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(syncedDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(syncedDir, recursive: true); } catch { /* best effort */ }
    }

    // ── Matching ladder ─────────────────────────────────────────────────────

    [Fact]
    public async Task Ladder_FingerprintBeatsAcoustIdBeatsMbidBeatsFuzzy()
    {
        await using var db = CreateDbContext();
        var byFingerprint = Song(1, "/lib/a.mp3", fingerprint: "FP1", acoustId: "AC-other", mbid: "MB-other");
        var byAcoustId = Song(2, "/lib/b.mp3", fingerprint: "FP-other", acoustId: "AC1", mbid: "MB-other2");
        var byMbid = Song(3, "/lib/c.mp3", fingerprint: null, acoustId: null, mbid: "MB1");
        var byFuzzy = Song(4, "/lib/d.mp3", artist: "Some Artist", title: "Some Song", durationMs: 200_000);
        db.Songs.AddRange(byFingerprint, byAcoustId, byMbid, byFuzzy);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var (hit1, by1) = await service.FindExistingAsync("FP1", "AC1", "MB1", "Some Artist", "Some Song", 200_000, default);
        Assert.Equal(1, hit1!.Id);
        Assert.Equal("fingerprint", by1);

        var (hit2, by2) = await service.FindExistingAsync(null, "AC1", "MB1", "Some Artist", "Some Song", 200_000, default);
        Assert.Equal(2, hit2!.Id);
        Assert.Equal("acoustid", by2);

        var (hit3, by3) = await service.FindExistingAsync(null, null, "MB1", "Some Artist", "Some Song", 200_000, default);
        Assert.Equal(3, hit3!.Id);
        Assert.Equal("mbid", by3);

        var (hit4, by4) = await service.FindExistingAsync(null, null, null, "some artist", "SOME SONG", 201_000, default);
        Assert.Equal(4, hit4!.Id);
        Assert.Equal("fuzzy", by4);
    }

    [Fact]
    public async Task Ladder_FuzzyRespectsDurationTolerance()
    {
        await using var db = CreateDbContext();
        db.Songs.Add(Song(1, "/lib/a.mp3", artist: "Artist", title: "Song", durationMs: 200_000));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var (miss, _) = await service.FindExistingAsync(null, null, null, "Artist", "Song", 210_000, default);
        Assert.Null(miss); // 10s off > 3s tolerance

        var (hit, _) = await service.FindExistingAsync(null, null, null, "Artist", "Song", 202_000, default);
        Assert.NotNull(hit);
    }

    [Fact]
    public async Task Ladder_IgnoresDeletedSyntheticDuplicateAndDemoRows()
    {
        await using var db = CreateDbContext();
        var deleted = Song(1, "/lib/a.mp3", fingerprint: "FP1");
        deleted.SoftDelete();
        var synthetic = Song(2, "/lib/b.mp3", fingerprint: "FP1");
        synthetic.IsSynthetic = true;
        var duplicate = Song(3, "/lib/c.mp3", fingerprint: "FP1");
        duplicate.MarkAsDuplicate(99);
        var demo = Song(4, "/lib/d.mp3", fingerprint: "FP1");
        demo.OwnerUserId = WellKnownUsers.DemoId;
        db.Songs.AddRange(deleted, synthetic, duplicate, demo);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var (hit, _) = await service.FindExistingAsync("FP1", null, null, null, null, null, default);
        Assert.Null(hit);
    }

    // ── Check verdicts ──────────────────────────────────────────────────────

    [Fact]
    public async Task Check_NotPresent_LowerQuality_SameOrBetter()
    {
        await using var db = CreateDbContext();
        db.Songs.Add(Song(1, "/lib/a.opus", extension: ".opus", bitrate: 128, fingerprint: "FP1"));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var notPresent = await service.CheckAsync(
            new SyncCheckRequest("FP-unknown", null, null, null, null, null, ".flac", 900), default);
        Assert.Equal(SyncVerdict.NotPresent, notPresent.Verdict);

        var lower = await service.CheckAsync(
            new SyncCheckRequest("FP1", null, null, null, null, null, ".flac", 900), default);
        Assert.Equal(SyncVerdict.PresentLowerQuality, lower.Verdict);
        Assert.Equal(1, lower.SongId);
        Assert.Equal("fingerprint", lower.MatchedBy);

        var sameOrBetter = await service.CheckAsync(
            new SyncCheckRequest("FP1", null, null, null, null, null, ".opus", 96), default);
        Assert.Equal(SyncVerdict.PresentSameOrBetter, sameOrBetter.Verdict);
    }

    // ── Upload: create ──────────────────────────────────────────────────────

    [Fact]
    public async Task Ingest_NewTrack_CreatesMatchedLockedRowAndWritesFile()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);
        var payload = Payload(fingerprint: "FP-new", extension: ".flac", bitrate: 900);

        var response = await service.IngestAsync(payload, Bytes(64), default);

        Assert.Equal(SyncUploadOutcome.Created, response.Outcome);
        var song = await db.Songs.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(Owner, song.OwnerUserId);
        Assert.Equal(EnrichmentStatus.Matched, song.EnrichmentStatus);
        Assert.True(song.IsManuallyApproved);
        Assert.Equal("consensus+sync", song.MatchedBy);
        Assert.Equal("FP-new", song.Fingerprint);
        Assert.Equal(LibraryBuildStatus.Pending, song.LibraryBuildStatus);
        Assert.Equal("Some Artist", song.Artist);
        Assert.Equal("la la la", song.PlainLyrics);
        Assert.True(File.Exists(song.SourcePath));
        Assert.StartsWith(syncedDir.Replace('\\', '/'), song.SourcePath);
        Assert.True(song.OriginalMetadataCaptured);
        Assert.Empty(Directory.GetFiles(Path.Combine(syncedDir, ".incoming"))); // temp cleaned up
    }

    // ── Upload: skip ────────────────────────────────────────────────────────

    [Fact]
    public async Task Ingest_IdenticalFingerprint_SkipsWithoutWriting()
    {
        await using var db = CreateDbContext();
        db.Songs.Add(Song(1, "/lib/a.flac", extension: ".flac", bitrate: 900, fingerprint: "FP1"));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var response = await service.IngestAsync(
            Payload(fingerprint: "FP1", extension: ".flac", bitrate: 900), Bytes(64), default);

        Assert.Equal(SyncUploadOutcome.SkippedIdentical, response.Outcome);
        Assert.Equal(1, response.SongId);
        Assert.Empty(Directory.EnumerateFiles(syncedDir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Ingest_WorseQuality_SkipsSameOrBetter()
    {
        await using var db = CreateDbContext();
        db.Songs.Add(Song(1, "/lib/a.flac", extension: ".flac", bitrate: 900, fingerprint: "FP1", acoustId: "AC1"));
        await db.SaveChangesAsync();
        var service = CreateService(db);

        // Different encoding (no fingerprint match), matched via AcoustID, lower tier.
        var response = await service.IngestAsync(
            Payload(fingerprint: "FP-other", acoustId: "AC1", extension: ".opus", bitrate: 320), Bytes(64), default);

        Assert.Equal(SyncUploadOutcome.SkippedSameOrBetter, response.Outcome);
    }

    // ── Upload: replace in place ────────────────────────────────────────────

    [Fact]
    public async Task Ingest_BetterQuality_ReplacesInPlacePreservingIdEnrichmentAndBuildSignal()
    {
        await using var db = CreateDbContext();
        var existing = Song(7, "/lib/a.opus", extension: ".opus", bitrate: 128,
            fingerprint: "FP-old", acoustId: "AC1", artist: "Old Artist", title: "Old Title", durationMs: 200_000);
        existing.MarkBuildDone("/dest/Old Artist/song.opus");
        db.Songs.Add(existing);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var payload = Payload(fingerprint: "FP-new", acoustId: "AC1", extension: ".flac", bitrate: 900);
        var response = await service.IngestAsync(payload, Bytes(128), default);

        Assert.Equal(SyncUploadOutcome.Replaced, response.Outcome);
        Assert.Equal(7, response.SongId);

        var song = await db.Songs.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(7, song.Id); // same row
        Assert.Equal(".flac", song.Extension);
        Assert.Equal("FP-new", song.Fingerprint);
        Assert.Equal("Some Artist", song.Artist); // payload metadata is authoritative
        Assert.Equal(EnrichmentStatus.Matched, song.EnrichmentStatus);
        Assert.True(song.IsManuallyApproved);
        // Destination swap armed: build re-queued, old destination remembered for pruning.
        Assert.Equal(LibraryBuildStatus.Pending, song.LibraryBuildStatus);
        Assert.Null(song.DestinationPath);
        Assert.Equal("/dest/Old Artist/song.opus", song.PreviousDestinationPath);
        Assert.True(File.Exists(song.SourcePath));
    }

    [Fact]
    public async Task Ingest_Replace_DeletesOldFileOnlyInsideManagedDir()
    {
        await using var db = CreateDbContext();

        // Old source INSIDE the managed synced dir → deleted after replace.
        var oldManagedPath = Path.Combine(syncedDir, "Artist", "old [aaaa].opus");
        Directory.CreateDirectory(Path.GetDirectoryName(oldManagedPath)!);
        await File.WriteAllBytesAsync(oldManagedPath, new byte[16]);
        var managed = Song(1, oldManagedPath.Replace('\\', '/'), extension: ".opus", bitrate: 128, fingerprint: "FP-a", acoustId: "AC-a");

        // Old source OUTSIDE the managed dir (scanned original) → left alone.
        var outsidePath = Path.Combine(Path.GetTempPath(), $"mh-outside-{Guid.NewGuid():N}.opus");
        await File.WriteAllBytesAsync(outsidePath, new byte[16]);
        var external = Song(2, outsidePath.Replace('\\', '/'), extension: ".opus", bitrate: 128, fingerprint: "FP-b", acoustId: "AC-b");

        db.Songs.AddRange(managed, external);
        await db.SaveChangesAsync();
        var service = CreateService(db);

        try
        {
            await service.IngestAsync(Payload(fingerprint: "FP-a2", acoustId: "AC-a", extension: ".flac", bitrate: 900), Bytes(64), default);
            await service.IngestAsync(Payload(fingerprint: "FP-b2", acoustId: "AC-b", extension: ".flac", bitrate: 900), Bytes(64), default);

            Assert.False(File.Exists(oldManagedPath));
            Assert.True(File.Exists(outsidePath));
        }
        finally
        {
            try { File.Delete(outsidePath); } catch { /* best effort */ }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static SongMetadata Song(
        int id, string path, string extension = ".mp3", int? bitrate = 320, string? fingerprint = null,
        string? acoustId = null, string? mbid = null, string? artist = "Artist", string? title = "Song",
        int? durationMs = null)
        => new()
        {
            Id = id,
            OwnerUserId = Owner,
            SourcePath = path,
            FileSizeBytes = 1000,
            FileName = Path.GetFileName(path),
            Extension = extension,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Fingerprint = fingerprint,
            AcoustIdTrackId = acoustId,
            MusicBrainzId = mbid,
            Artist = artist,
            Title = title,
            Bitrate = bitrate,
            DurationMs = durationMs,
            EnrichmentStatus = EnrichmentStatus.Matched,
        };

    private static SyncTrackPayload Payload(
        string? fingerprint = null, string? acoustId = null, string? mbid = null,
        string extension = ".flac", int? bitrate = 900)
        => new(
            FileName: "Some Artist - Some Song" + extension,
            Extension: extension,
            FileSizeBytes: 128,
            Bitrate: bitrate,
            DurationSeconds: 200,
            DurationMs: 200_000,
            Fingerprint: fingerprint,
            Isrc: "USTEST1234567",
            MusicBrainzId: mbid,
            MusicBrainzReleaseId: null,
            MusicBrainzReleaseGroupId: null,
            SpotifyId: "spotify-id",
            AcoustIdTrackId: acoustId,
            LrclibId: null,
            Artist: "Some Artist",
            AlbumArtist: "Some Artist",
            Album: "Some Album",
            Title: "Some Song",
            Year: 2020,
            TrackNumber: 3,
            DiscNumber: 1,
            TotalDiscs: 1,
            TotalTracks: 10,
            Artists: "Some Artist",
            ArtistMusicBrainzIds: null,
            AlbumArtistMusicBrainzId: null,
            IsCompilation: false,
            ReleaseTypePrimary: "album",
            ReleaseTypes: "album",
            IsUnreleased: false,
            MatchedBy: "consensus",
            MatchConfidence: 0.97,
            PlainLyrics: "la la la",
            SyncedLyrics: "[00:01.00] la la la",
            IsInstrumental: false,
            LyricsStatus: LyricsStatus.Fetched);

    private static MemoryStream Bytes(int count) => new(Enumerable.Repeat((byte)7, count).ToArray());

    private SyncIngestService CreateService(MusicHoarderDbContext db)
    {
        var options = new StaticOptionsMonitor<SyncOptions>(new SyncOptions
        {
            Mode = SyncMode.Receive,
            ApiKey = new string('k', 40),
            SyncedSourceDirectory = syncedDir,
            DurationToleranceMs = 3000,
        });
        return new SyncIngestService(
            db, new OwnerLookupService(), new JobManager(), options,
            NullLogger<SyncIngestService>.Instance);
    }

    private static MusicHoarderDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
