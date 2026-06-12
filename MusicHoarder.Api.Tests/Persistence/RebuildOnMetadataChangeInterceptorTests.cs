using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Persistence.Interceptors;

namespace MusicHoarder.Api.Tests.Persistence;

public class RebuildOnMetadataChangeInterceptorTests
{
    private const string DestPath = "/dest/Artist/Album/01 - Track.mp3";

    [Fact]
    public async Task LyricsChangeOnBuiltSong_ResetsBuild()
    {
        await using var db = NewContext(autoStartPipeline: true);
        var song = await SeedBuiltSongAsync(db);

        song.ApplyLyricsResult("[00:00] synced", "plain", instrumental: false, lrclibId: 42);
        await db.SaveChangesAsync();

        Assert.Equal(LibraryBuildStatus.Pending, song.LibraryBuildStatus);
        Assert.Null(song.DestinationPath);
        Assert.Equal(DestPath, song.PreviousDestinationPath);
    }

    [Theory]
    [InlineData(nameof(SongMetadata.PlainLyrics))]
    [InlineData(nameof(SongMetadata.SyncedLyrics))]
    [InlineData(nameof(SongMetadata.Title))]
    [InlineData(nameof(SongMetadata.IsCompilation))]
    public async Task TagRelevantChangeOnBuiltSong_ResetsBuild(string field)
    {
        await using var db = NewContext(autoStartPipeline: true);
        var song = await SeedBuiltSongAsync(db);

        switch (field)
        {
            case nameof(SongMetadata.PlainLyrics): song.PlainLyrics = "new lyrics"; break;
            case nameof(SongMetadata.SyncedLyrics): song.SyncedLyrics = "[00:01.00] late line"; break;
            case nameof(SongMetadata.Title): song.Title = "Renamed Title"; break;
            case nameof(SongMetadata.IsCompilation): song.IsCompilation = true; break;
        }
        await db.SaveChangesAsync();

        Assert.Equal(LibraryBuildStatus.Pending, song.LibraryBuildStatus);
        Assert.Equal(DestPath, song.PreviousDestinationPath);
    }

    [Fact]
    public async Task NonTagChangeOnBuiltSong_DoesNotResetBuild()
    {
        await using var db = NewContext(autoStartPipeline: true);
        var song = await SeedBuiltSongAsync(db);

        // A non-tag field — not embedded into the file, so no re-tag is warranted.
        song.LibraryBuildLastAttemptedAtUtc = new DateTime(2026, 2, 2);
        await db.SaveChangesAsync();

        Assert.Equal(LibraryBuildStatus.Done, song.LibraryBuildStatus);
        Assert.Equal(DestPath, song.DestinationPath);
        Assert.Null(song.PreviousDestinationPath);
    }

    [Fact]
    public async Task LyricsChange_WhenAutoStartPipelineDisabled_DoesNotResetBuild()
    {
        await using var db = NewContext(autoStartPipeline: false);
        var song = await SeedBuiltSongAsync(db);

        song.ApplyLyricsResult("[00:00] synced", "plain", instrumental: false, lrclibId: 42);
        await db.SaveChangesAsync();

        Assert.Equal(LibraryBuildStatus.Done, song.LibraryBuildStatus);
        Assert.Equal(DestPath, song.DestinationPath);
        Assert.Null(song.PreviousDestinationPath);
    }

    [Fact]
    public async Task TagChange_OnNotYetBuiltSong_DoesNotResetBuild()
    {
        await using var db = NewContext(autoStartPipeline: true);
        var song = NewSong();
        song.EnrichmentStatus = EnrichmentStatus.Matched; // Matched but never built.
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        song.PlainLyrics = "lyrics arrive before first build";
        await db.SaveChangesAsync();

        // Still Pending — it will pick the lyrics up at its first build, nothing to reset.
        Assert.Equal(LibraryBuildStatus.Pending, song.LibraryBuildStatus);
        Assert.Null(song.PreviousDestinationPath);
    }

    [Fact]
    public async Task ExplicitResetPlusTagChange_DoesNotDoubleReset()
    {
        await using var db = NewContext(autoStartPipeline: true);
        var song = await SeedBuiltSongAsync(db);

        // Mimic an endpoint that mutates metadata and explicitly resets in the same unit of work.
        song.Title = "Approved Title";
        song.ResetLibraryBuild();
        await db.SaveChangesAsync();

        Assert.Equal(LibraryBuildStatus.Pending, song.LibraryBuildStatus);
        // The explicit reset's PreviousDestinationPath must survive — a second reset would null it.
        Assert.Equal(DestPath, song.PreviousDestinationPath);
        Assert.Null(song.DestinationPath);
    }

    private static async Task<SongMetadata> SeedBuiltSongAsync(MusicHoarderDbContext db)
    {
        var song = NewSong();
        song.EnrichmentStatus = EnrichmentStatus.Matched;
        song.MarkBuildDone(DestPath);
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        return song;
    }

    private static SongMetadata NewSong() => new()
    {
        OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = "/src/track.mp3",
        FileName = "track.mp3",
        Extension = ".mp3",
        FileSizeBytes = 1234L,
        LastModifiedUtc = new DateTime(2026, 1, 1),
        IndexedAtUtc = new DateTime(2026, 1, 1),
        Artist = "Artist",
        Album = "Album",
        Title = "Title",
    };

    private static MusicHoarderDbContext NewContext(bool autoStartPipeline)
    {
        var interceptor = new RebuildOnMetadataChangeInterceptor(
            new TestOptionsMonitor<MusicEnricherOptions>(new MusicEnricherOptions
            {
                SourceDirectory = "/src",
                DestinationDirectory = "/dest",
                AutoStartPipeline = autoStartPipeline,
            }));

        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .AddInterceptors(interceptor)
            .Options;

        return new MusicHoarderDbContext(options);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
