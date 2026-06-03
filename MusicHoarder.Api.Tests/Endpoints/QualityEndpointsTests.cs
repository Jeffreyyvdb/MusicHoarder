using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;

namespace MusicHoarder.Api.Tests.Endpoints;

public class QualityEndpointsTests
{
    private static MusicHoarderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    // The current (non-outdated) model used by the endpoints under test.
    private const string CurrentModel = "openai/gpt-4o-mini";

    private static IOptionsMonitor<QualityGradingOptions> Opts(string model = CurrentModel) =>
        new TestOptionsMonitor(new QualityGradingOptions { Model = model });

    private sealed class TestOptionsMonitor(QualityGradingOptions value) : IOptionsMonitor<QualityGradingOptions>
    {
        public QualityGradingOptions CurrentValue { get; } = value;
        public QualityGradingOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<QualityGradingOptions, string?> listener) => null;
    }

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

    private static void AddGrade(
        MusicHoarderDbContext db, int songId, SongQualityVerdict verdict, int score, string statusAtGrade,
        int? promptVersion = null, string? model = CurrentModel)
    {
        db.SongQualityGrades.Add(new SongQualityGrade
        {
            SongId = songId,
            OwnerUserId = Api.Auth.WellKnownUsers.OwnerId,
            Score = score,
            Verdict = verdict,
            EnrichmentStatusAtGrade = statusAtGrade,
            PromptVersion = promptVersion ?? QualityGradingPrompt.Version,
            Model = model,
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

        var result = await QualityEndpoints.GetSongs(db, Opts(), CancellationToken.None);

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

        Assert.Equal(2, Read<int>(await QualityEndpoints.GetSongs(db, Opts(), CancellationToken.None), "total"));
        Assert.Equal(1, Read<int>(await QualityEndpoints.GetSongs(db, Opts(), CancellationToken.None, category: "silent"), "total"));
        Assert.Equal(1, Read<int>(await QualityEndpoints.GetSongs(db, Opts(), CancellationToken.None, category: "verified"), "total"));
        Assert.Equal(0, Read<int>(await QualityEndpoints.GetSongs(db, Opts(), CancellationToken.None, category: "flagged"), "total"));
    }

    [Fact]
    public async Task GetSongs_FlagsGradesFromAnOlderPromptVersionOrModelAsOutdated()
    {
        using var db = NewContext();
        var current = AddSong(db, 1, EnrichmentStatus.Matched);
        AddGrade(db, current.Id, SongQualityVerdict.Good, 80, "Matched"); // current prompt + model
        var oldPrompt = AddSong(db, 2, EnrichmentStatus.Matched);
        AddGrade(db, oldPrompt.Id, SongQualityVerdict.Good, 80, "Matched", promptVersion: 1);
        var oldModel = AddSong(db, 3, EnrichmentStatus.Matched);
        AddGrade(db, oldModel.Id, SongQualityVerdict.Good, 80, "Matched", model: "some/older-model");

        var items = Read<List<object>>(
            await QualityEndpoints.GetSongs(db, Opts(), CancellationToken.None, take: 500), "items");

        bool Outdated(object row) => (bool)row.GetType().GetProperty("isOutdated")!.GetValue(row)!;
        int IdOf(object row) => (int)row.GetType().GetProperty("songId")!.GetValue(row)!;
        var byId = items.ToDictionary(IdOf, Outdated);

        Assert.False(byId[current.Id]);
        Assert.True(byId[oldPrompt.Id]);
        Assert.True(byId[oldModel.Id]);
    }

    [Fact]
    public async Task GetOverview_CountsOutdatedGrades()
    {
        using var db = NewContext();
        var current = AddSong(db, 1, EnrichmentStatus.Matched);
        AddGrade(db, current.Id, SongQualityVerdict.Good, 80, "Matched");
        var stale = AddSong(db, 2, EnrichmentStatus.Matched);
        AddGrade(db, stale.Id, SongQualityVerdict.Good, 80, "Matched", promptVersion: 1);

        var result = await QualityEndpoints.GetOverview(db, Opts(), CancellationToken.None);

        Assert.Equal(1, Read<int>(result, "outdatedCount"));
    }
}
