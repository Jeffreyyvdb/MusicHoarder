using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;

namespace MusicHoarder.Api.Tests.Quality;

public class LatestGradeQueriesTests
{
    private static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static SongQualityGrade SongGrade(int songId, SongQualityVerdict verdict, DateTime at) => new()
    {
        SongId = songId,
        OwnerUserId = WellKnownUsers.OwnerId,
        Verdict = verdict,
        Score = 50,
        PromptVersion = QualityGradingPrompt.Version,
        GradedAtUtc = at,
    };

    private static CanonicalAlbumQualityGrade AlbumGrade(int albumId, SongQualityVerdict verdict, DateTime at) => new()
    {
        CanonicalAlbumId = albumId,
        OwnerUserId = WellKnownUsers.OwnerId,
        Verdict = verdict,
        Score = 50,
        PromptVersion = AlbumGradingPrompt.Version,
        GradedAtUtc = at,
    };

    [Fact]
    public async Task LatestPerSong_ElectsTheNewestGradeForEachSong()
    {
        await using var db = NewContext();
        db.SongQualityGrades.Add(SongGrade(1, SongQualityVerdict.Wrong, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        db.SongQualityGrades.Add(SongGrade(1, SongQualityVerdict.Good, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
        db.SongQualityGrades.Add(SongGrade(2, SongQualityVerdict.Excellent, new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var latest = await db.SongQualityGrades.LatestPerSong().ToListAsync();

        Assert.Equal(2, latest.Count);
        Assert.Equal(SongQualityVerdict.Good, latest.Single(g => g.SongId == 1).Verdict);
        Assert.Equal(SongQualityVerdict.Excellent, latest.Single(g => g.SongId == 2).Verdict);
    }

    [Fact]
    public async Task LatestPerAlbum_ElectsTheNewestGradeForEachAlbum()
    {
        await using var db = NewContext();
        db.CanonicalAlbumQualityGrades.Add(AlbumGrade(1, SongQualityVerdict.Wrong, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        db.CanonicalAlbumQualityGrades.Add(AlbumGrade(1, SongQualityVerdict.Good, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var latest = await db.CanonicalAlbumQualityGrades.LatestPerAlbum().ToListAsync();

        var only = Assert.Single(latest);
        Assert.Equal(SongQualityVerdict.Good, only.Verdict);
    }

    [Fact]
    public async Task LatestPerSong_ComposesWithFurtherFilters()
    {
        // The election must run against the full grade set even when the caller narrows afterwards —
        // filtering to a song id must not resurrect its superseded grades.
        await using var db = NewContext();
        db.SongQualityGrades.Add(SongGrade(1, SongQualityVerdict.Wrong, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        db.SongQualityGrades.Add(SongGrade(1, SongQualityVerdict.Good, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var latest = await db.SongQualityGrades.LatestPerSong().Where(g => g.SongId == 1).ToListAsync();

        var only = Assert.Single(latest);
        Assert.Equal(SongQualityVerdict.Good, only.Verdict);
    }
}
