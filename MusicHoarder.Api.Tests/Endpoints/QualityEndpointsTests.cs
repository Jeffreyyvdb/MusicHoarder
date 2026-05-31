using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Endpoints;

public class QualityEndpointsTests
{
    private static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static SongMetadata AddSong(MusicHoarderDbContext db, int n, EnrichmentStatus status)
    {
        var song = new SongMetadata
        {
            OwnerUserId = Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = $"/root/music/song{n}.mp3",
            FileName = $"song{n}.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1_000_000,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Title = $"Song {n}",
            Artist = "Artist",
            EnrichmentStatus = status,
        };
        db.Songs.Add(song);
        db.SaveChanges();
        return song;
    }

    private static void AddGrade(MusicHoarderDbContext db, int songId, SongQualityVerdict verdict, int score, string statusAtGrade)
    {
        db.SongQualityGrades.Add(new SongQualityGrade
        {
            SongId = songId,
            OwnerUserId = Api.Auth.WellKnownUsers.OwnerId,
            Score = score,
            Verdict = verdict,
            EnrichmentStatusAtGrade = statusAtGrade,
            GradedAtUtc = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private static T Read<T>(object result, string prop)
    {
        var value = ((IValueHttpResult)result).Value!;
        return (T)value.GetType().GetProperty(prop)!.GetValue(value)!;
    }

    [Fact]
    public async Task GetSongs_WithoutPagingParams_Returns200()
    {
        // Regression: skip/take are optional. A non-nullable int query param is treated as
        // *required* by minimal API and 400s when absent — defaults must keep the route callable.
        using var db = NewContext();

        var result = await QualityEndpoints.GetSongs(db, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal(0, Read<int>(result, "total"));
    }

    [Fact]
    public async Task GetSongs_FiltersByBucketCategory()
    {
        using var db = NewContext();
        var silent = AddSong(db, 1, EnrichmentStatus.Matched);
        AddGrade(db, silent.Id, SongQualityVerdict.Wrong, 20, "Matched");        // silent failure
        var verified = AddSong(db, 2, EnrichmentStatus.Matched);
        AddGrade(db, verified.Id, SongQualityVerdict.Excellent, 95, "Matched");  // verified clean

        Assert.Equal(2, Read<int>(await QualityEndpoints.GetSongs(db, CancellationToken.None), "total"));
        Assert.Equal(1, Read<int>(await QualityEndpoints.GetSongs(db, CancellationToken.None, category: "silent"), "total"));
        Assert.Equal(1, Read<int>(await QualityEndpoints.GetSongs(db, CancellationToken.None, category: "verified"), "total"));
        Assert.Equal(0, Read<int>(await QualityEndpoints.GetSongs(db, CancellationToken.None, category: "flagged"), "total"));
    }
}
