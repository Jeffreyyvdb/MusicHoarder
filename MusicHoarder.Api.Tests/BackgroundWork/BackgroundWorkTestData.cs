using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.BackgroundWork;

internal static class BackgroundWorkTestData
{
    public static readonly Guid Owner = WellKnownUsers.OwnerId;
    public static readonly DateTime Now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

    public static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    public static TimeProvider Clock(DateTime? now = null) => new FixedTimeProvider(now ?? Now);

    public static SongMetadata NewSong(int n, Action<SongMetadata>? configure = null)
    {
        var song = new SongMetadata
        {
            OwnerUserId = Owner,
            SourcePath = $"/src/{n}.mp3",
            FileName = $"{n}.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1000,
            LastModifiedUtc = Now,
            IndexedAtUtc = Now.AddDays(-n),
            Title = $"Song {n}",
            Artist = "Artist",
            Album = "Album",
            AlbumArtist = "Artist",
            DurationSeconds = 200,
            EnrichmentStatus = EnrichmentStatus.Matched,
        };
        configure?.Invoke(song);
        return song;
    }

    public static SongMetadata AddSong(MusicHoarderDbContext db, int n, Action<SongMetadata>? configure = null)
    {
        var song = NewSong(n, configure);
        db.Songs.Add(song);
        db.SaveChanges();
        return song;
    }

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }
}
