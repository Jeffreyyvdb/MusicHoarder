using System.IO.Abstractions.TestingHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Library;

public class SongReportServiceTests
{
    private const string Staged = "/downloads/Artist - Wrong.opus";
    private const string Library = "/dest/Artist/Single/01 - Wrong.opus";
    private static readonly Guid Owner = WellKnownUsers.OwnerId;

    [Fact]
    public async Task Removing_a_download_deletes_its_files_and_parks_the_wishlist_item()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Staged] = new("staged"),
            [Library] = new("library"),
            ["/videos/clip.mp4"] = new("video"),
            ["/videos/clip.jpg"] = new("thumb"),
        });
        await using var db = CreateDb();
        var song = Song(Staged);
        song.MarkBuildDone(Library);
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        db.SongMusicVideos.Add(new SongMusicVideo { SongId = song.Id, FilePath = "/videos/clip.mp4" });
        var item = new WishlistItem
        {
            OwnerUserId = Owner,
            Title = "Right Song",
            Artist = "Artist",
            Album = "Album",
            DurationMs = 200_000,
            SpotifyTrackId = "sp-right",
            Status = WishlistItemStatus.Downloaded,
            DownloadProvider = "yt-dlp",
            DownloadedSourceId = "yt-abc",
            DownloadedFilePath = Staged,
            DownloadedSongId = song.Id,
        };
        db.WishlistItems.Add(item);
        await db.SaveChangesAsync();

        var result = await Service(db, fs).ReportAsync(
            Owner, song.Id, SongReportReason.WrongVersion, "  live version  ", remove: true, CancellationToken.None);

        Assert.Equal(SongReportOutcome.Reported, result.Outcome);
        Assert.False(fs.File.Exists(Staged));
        Assert.False(fs.File.Exists(Library));
        Assert.False(fs.File.Exists("/videos/clip.mp4"));
        Assert.False(fs.File.Exists("/videos/clip.jpg"));
        Assert.Empty(await db.SongMusicVideos.ToListAsync());

        var reloaded = await db.Songs.IgnoreQueryFilters().SingleAsync();
        Assert.True(reloaded.IsDeleted);

        var parked = await db.WishlistItems.SingleAsync();
        Assert.Equal(WishlistItemStatus.Failed, parked.Status);
        Assert.Null(parked.NextAttemptAtUtc);
        Assert.Null(parked.DownloadedSongId);
        Assert.Null(parked.DownloadedFilePath);
        Assert.Equal(SongReportService.WishlistReportedError, parked.LastError);

        var report = await db.SongReports.SingleAsync();
        Assert.Equal(song.Id, report.SongId);
        Assert.Equal(SongReportReason.WrongVersion, report.Reason);
        Assert.Equal("live version", report.Note);
        Assert.Equal("Wrong Song", report.Title);
        Assert.Equal("Right Song", report.RequestedTitle);
        Assert.Equal(200_000, report.RequestedDurationMs);
        Assert.Equal("yt-dlp", report.DownloadProvider);
        Assert.Equal("yt-abc", report.DownloadedSourceId);
        Assert.Equal("sp-right", report.SpotifyTrackId);
        Assert.Equal(item.Id, report.WishlistItemId);
        Assert.NotNull(report.RemovedAtUtc);
    }

    [Fact]
    public async Task A_released_download_only_has_its_library_copy_to_delete()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData> { [Library] = new("library") });
        await using var db = CreateDb();
        var song = Song(Staged);
        song.MarkBuildDone(Library);
        song.SourceReleasedAtUtc = DateTime.UtcNow.AddDays(-1);
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var result = await Service(db, fs).ReportAsync(
            Owner, song.Id, SongReportReason.WrongSong, null, remove: true, CancellationToken.None);

        Assert.Equal(SongReportOutcome.Reported, result.Outcome);
        Assert.False(fs.File.Exists(Library));
        Assert.True((await db.Songs.IgnoreQueryFilters().SingleAsync()).IsDeleted);
        Assert.Null((await db.SongReports.SingleAsync()).WishlistItemId);
    }

    [Fact]
    public async Task A_file_in_the_owners_source_library_is_never_deleted()
    {
        const string own = "/source/Artist/Track.flac";
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [own] = new("mine"),
            [Library] = new("library"),
        });
        await using var db = CreateDb();
        var song = Song(own);
        song.MarkBuildDone(Library);
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var result = await Service(db, fs).ReportAsync(
            Owner, song.Id, SongReportReason.WrongSong, null, remove: true, CancellationToken.None);

        Assert.Equal(SongReportOutcome.NotRemovable, result.Outcome);
        Assert.True(fs.File.Exists(own));
        Assert.True(fs.File.Exists(Library));
        Assert.False((await db.Songs.SingleAsync()).IsDeleted);
        Assert.Empty(await db.SongReports.ToListAsync());
    }

    [Fact]
    public async Task Reporting_without_removing_keeps_the_song()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData> { [Staged] = new("staged") });
        await using var db = CreateDb();
        var song = Song(Staged);
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var result = await Service(db, fs).ReportAsync(
            Owner, song.Id, SongReportReason.BadAudio, null, remove: false, CancellationToken.None);

        Assert.Equal(SongReportOutcome.Reported, result.Outcome);
        Assert.True(fs.File.Exists(Staged));
        Assert.False((await db.Songs.SingleAsync()).IsDeleted);
        Assert.Null((await db.SongReports.SingleAsync()).RemovedAtUtc);
    }

    [Fact]
    public async Task Another_owners_or_a_removed_song_is_not_found()
    {
        var fs = new MockFileSystem();
        await using var db = CreateDb();
        var foreign = Song(Staged);
        foreign.OwnerUserId = Guid.NewGuid();
        var removed = Song("/downloads/gone.opus");
        removed.SoftDelete();
        db.Songs.AddRange(foreign, removed);
        await db.SaveChangesAsync();

        var service = Service(db, fs);
        Assert.Equal(SongReportOutcome.NotFound, (await service.ReportAsync(
            Owner, foreign.Id, SongReportReason.WrongSong, null, true, CancellationToken.None)).Outcome);
        Assert.Equal(SongReportOutcome.NotFound, (await service.ReportAsync(
            Owner, removed.Id, SongReportReason.WrongSong, null, true, CancellationToken.None)).Outcome);
    }

    private static SongReportService Service(MusicHoarderDbContext db, MockFileSystem fs) => new(
        db,
        fs,
        new LibraryDestinationCleaner(fs),
        Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
            DownloadDirectory = "/downloads",
        }),
        new StaticOptionsMonitor<SlskdOptions>(new SlskdOptions()),
        NullLogger<SongReportService>.Instance);

    private static SongMetadata Song(string sourcePath) => new()
    {
        OwnerUserId = Owner,
        SourcePath = sourcePath,
        FileName = Path.GetFileName(sourcePath),
        Extension = Path.GetExtension(sourcePath),
        FileSizeBytes = 1000,
        LastModifiedUtc = new DateTime(2026, 1, 1),
        IndexedAtUtc = new DateTime(2026, 1, 1),
        Artist = "Artist",
        Title = "Wrong Song",
        Album = "Single",
        DurationMs = 180_000,
    };

    private static MusicHoarderDbContext CreateDb() => new(
        new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private sealed class StaticOptionsMonitor<T>(T value) : Microsoft.Extensions.Options.IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
