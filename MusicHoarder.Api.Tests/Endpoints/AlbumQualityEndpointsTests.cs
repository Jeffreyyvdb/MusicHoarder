using System.Collections;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Endpoints;

public class AlbumQualityEndpointsTests
{
    [Fact]
    public async Task GetOverview_RollsUpVerdicts_AndOrdersWorstFirst()
    {
        await using var db = NewContext();
        db.CanonicalAlbums.Add(Album(1, "Discovery", CanonicalAlbumStatus.Fetched));
        db.CanonicalAlbums.Add(Album(2, "Homework", CanonicalAlbumStatus.Fetched));
        db.CanonicalAlbumQualityGrades.Add(Grade(1, SongQualityVerdict.Wrong, 20));
        db.CanonicalAlbumQualityGrades.Add(Grade(2, SongQualityVerdict.Excellent, 95));
        await db.SaveChangesAsync();

        var result = await AlbumQualityEndpoints.GetOverview(db, CancellationToken.None);

        var value = Value(result);
        Assert.Equal(2, GetProperty<int>(value, "gradeableTotal"));
        Assert.Equal(1, GetProperty<int>(value, "wrongCount"));

        var worst = ((IEnumerable)GetProperty<object>(value, "worstOffenders")!).Cast<object>().ToList();
        Assert.Equal(2, worst.Count);
        // Wrong (worst) first.
        Assert.Equal("Wrong", GetProperty<string>(worst[0], "verdict"));
    }

    [Fact]
    public async Task GetOverview_UsesLatestGradePerAlbum()
    {
        await using var db = NewContext();
        db.CanonicalAlbums.Add(Album(1, "Discovery", CanonicalAlbumStatus.Fetched));
        db.CanonicalAlbumQualityGrades.Add(Grade(1, SongQualityVerdict.Wrong, 20, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        db.CanonicalAlbumQualityGrades.Add(Grade(1, SongQualityVerdict.Good, 80, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        var result = await AlbumQualityEndpoints.GetOverview(db, CancellationToken.None);

        var value = Value(result);
        // Only the latest grade (Good) counts — not both.
        var library = GetProperty<object>(value, "library");
        var verdicts = GetProperty<object>(library, "verdicts");
        Assert.Equal(1, GetProperty<int>(verdicts, "good"));
        Assert.Equal(0, GetProperty<int>(verdicts, "wrong"));
    }

    [Fact]
    public async Task GetAlbumGrade_ByArtistAlbum_ReturnsLatest()
    {
        await using var db = NewContext();
        var album = Album(1, "Discovery", CanonicalAlbumStatus.Fetched);
        album.ArtistKey = "daft punk";
        album.AlbumKey = "discovery";
        album.DisplayArtist = "Daft Punk";
        db.CanonicalAlbums.Add(album);
        db.CanonicalAlbumQualityGrades.Add(Grade(1, SongQualityVerdict.Questionable, 55));
        await db.SaveChangesAsync();

        var result = await AlbumQualityEndpoints.GetAlbumGrade("Daft Punk", "Discovery", db, CancellationToken.None);

        var value = Value(result);
        Assert.True(GetProperty<bool>(value, "graded"));
        Assert.Equal("Questionable", GetProperty<string>(value, "verdict"));
    }

    [Fact]
    public async Task GetAlbumGrade_Ungraded_ReturnsGradedFalse()
    {
        await using var db = NewContext();
        var album = Album(1, "Discovery", CanonicalAlbumStatus.Fetched);
        album.ArtistKey = "daft punk";
        album.AlbumKey = "discovery";
        db.CanonicalAlbums.Add(album);
        await db.SaveChangesAsync();

        var result = await AlbumQualityEndpoints.GetAlbumGrade("Daft Punk", "Discovery", db, CancellationToken.None);
        Assert.False(GetProperty<bool>(Value(result), "graded"));
    }

    private static object Value(IResult result) => result.GetType().GetProperty("Value")!.GetValue(result)!;

    private static T GetProperty<T>(object obj, string name)
    {
        var prop = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Property '{name}' not found on {obj.GetType()}");
        return (T)prop.GetValue(obj)!;
    }

    private static CanonicalAlbum Album(int id, string title, CanonicalAlbumStatus status) => new()
    {
        Id = id,
        ArtistKey = $"artist{id}",
        AlbumKey = title.ToLowerInvariant(),
        DisplayArtist = $"Artist {id}",
        DisplayTitle = title,
        Status = status,
    };

    private static CanonicalAlbumQualityGrade Grade(int albumId, SongQualityVerdict verdict, int score, DateTime? at = null) => new()
    {
        CanonicalAlbumId = albumId,
        OwnerUserId = WellKnownUsers.OwnerId,
        Verdict = verdict,
        Score = score,
        GradedAtUtc = at ?? DateTime.UtcNow,
    };

    private static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
