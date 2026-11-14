using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Sync;

namespace MusicHoarder.Api.Tests.Sync;

/// <summary>A sync peer's Extension / album artist must never place or delete files outside the managed directory.</summary>
public class SyncUploadPathContainmentTests : IDisposable
{
    private readonly string baseDir = Path.Combine(Path.GetTempPath(), $"mh-sync-upload-{Guid.NewGuid():N}");
    private string SyncedDir => Path.Combine(baseDir, "lib", "synced");

    public SyncUploadPathContainmentTests() => Directory.CreateDirectory(SyncedDir);

    public void Dispose()
    {
        try { Directory.Delete(baseDir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("/../../../planted")]
    [InlineData(".flac/../../../planted")]
    [InlineData("/../../../../planted")]
    [InlineData(".exe")]
    public async Task ExtensionThatIsNotAnAudioSuffix_IsRejectedAndWritesNothingOutside(string extension)
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.IngestAsync(Payload(extension, "ArtA"), Bytes(), default));

        Assert.Empty(await db.Songs.ToListAsync());
        Assert.Empty(OutsideManaged());
    }

    [Fact]
    public async Task ExtensionWithSeparators_LeavesExistingFilesAlone()
    {
        var victim = Path.Combine(baseDir, "victim.cfg");
        await File.WriteAllTextAsync(victim, "keep");
        await using var db = CreateDbContext();
        var service = CreateService(db);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.IngestAsync(Payload("/../../../victim.cfg", "ArtA"), Bytes(), default));

        Assert.Equal("keep", await File.ReadAllTextAsync(victim));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    public async Task DotOnlyArtistFolder_StaysInsideManagedDirectory(string artist)
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        await service.IngestAsync(Payload(".flac", artist), Bytes(), default);

        var song = await db.Songs.SingleAsync();
        var root = Path.GetFullPath(SyncedDir) + Path.DirectorySeparatorChar;
        Assert.StartsWith(root, Path.GetFullPath(song.SourcePath), StringComparison.Ordinal);
        Assert.Empty(OutsideManaged());
    }

    // Anything under baseDir that is not inside the managed directory.
    private List<string> OutsideManaged() =>
        Directory.EnumerateFileSystemEntries(baseDir, "*", SearchOption.AllDirectories)
            .Where(p => !p.StartsWith(Path.GetFullPath(SyncedDir), StringComparison.Ordinal)
                && !p.Equals(Path.Combine(baseDir, "lib"), StringComparison.Ordinal)
                && !p.EndsWith("victim.cfg", StringComparison.Ordinal))
            .ToList();

    private static MemoryStream Bytes() => new(Enumerable.Repeat((byte)7, 32).ToArray());

    private static SyncTrackPayload Payload(string extension, string albumArtist)
        => new(
            FileName: "x", Extension: extension, FileSizeBytes: 32, Bitrate: 900,
            DurationSeconds: 200, DurationMs: 200_000, Fingerprint: "FP-sync-upload",
            Isrc: null, MusicBrainzId: null, MusicBrainzReleaseId: null, MusicBrainzReleaseGroupId: null,
            SpotifyId: null, AcoustIdTrackId: null, LrclibId: null,
            Artist: albumArtist, AlbumArtist: albumArtist, Album: "Album", Title: "Title",
            Year: 2020, TrackNumber: 1, DiscNumber: 1, TotalDiscs: 1, TotalTracks: 1,
            Artists: albumArtist, ArtistMusicBrainzIds: null, AlbumArtistMusicBrainzId: null,
            IsCompilation: false, ReleaseTypePrimary: "album", ReleaseTypes: "album",
            IsUnreleased: false, MatchedBy: "consensus", MatchConfidence: 0.97,
            PlainLyrics: null, SyncedLyrics: null, IsInstrumental: false,
            LyricsStatus: LyricsStatus.NotFetched, LikedAtUtc: null,
            AcquisitionIntent: SongAcquisitionIntent.Explicit);

    private SyncIngestService CreateService(MusicHoarderDbContext db)
    {
        var options = new StaticOptionsMonitor<SyncOptions>(new SyncOptions
        {
            Mode = SyncMode.Receive,
            ApiKey = new string('k', 40),
            SyncedSourceDirectory = SyncedDir,
            DurationToleranceMs = 3000,
        });
        return new SyncIngestService(
            db, new OwnerLookupService(), new JobManager(), options,
            NullLogger<SyncIngestService>.Instance);
    }

    private static MusicHoarderDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
