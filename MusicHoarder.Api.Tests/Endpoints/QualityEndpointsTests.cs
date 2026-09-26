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

    private static SongMetadata AddSong(
        MusicHoarderDbContext db, int n, EnrichmentStatus status, string directory = "/root/music")
    {
        var song = new SongMetadata
        {
            OwnerUserId = Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = $"{directory}/song{n}.mp3",
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
        int? promptVersion = null, string? model = CurrentModel, DateTime? at = null, string? issuesJson = null)
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
            IssuesJson = issuesJson,
            GradedAtUtc = at ?? DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private static T Read<T>(object result, string prop)
    {
        var value = ((IValueHttpResult)result).Value!;
        return (T)value.GetType().GetProperty(prop)!.GetValue(value)!;
    }

    private static T Prop<T>(object row, string prop) => (T)row.GetType().GetProperty(prop)!.GetValue(row)!;

    // Grades songs n..n+count-1 with one verdict, scores stepping from `score`; returns the next free n.
    private static int AddGraded(
        MusicHoarderDbContext db, int n, int count, SongQualityVerdict verdict, int score, int scoreStep = 0,
        string directory = "/root/music")
    {
        for (var i = 0; i < count; i++)
        {
            var song = AddSong(db, n + i, EnrichmentStatus.Matched, directory);
            AddGrade(db, song.Id, verdict, score + i * scoreStep, "Matched");
        }
        return n + count;
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
    public async Task GetSongs_UsesOnlyTheLatestGradePerSong()
    {
        using var db = NewContext();
        var song = AddSong(db, 1, EnrichmentStatus.Matched);
        AddGrade(db, song.Id, SongQualityVerdict.Wrong, 20, "Matched",
            at: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        AddGrade(db, song.Id, SongQualityVerdict.Good, 80, "Matched",
            at: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = await QualityEndpoints.GetSongs(db, Opts(), CancellationToken.None);

        // The superseded Wrong grade must not surface as a second row.
        Assert.Equal(1, Read<int>(result, "total"));
        var row = Read<List<object>>(result, "items").Single();
        Assert.Equal("Good", (string)row.GetType().GetProperty("verdict")!.GetValue(row)!);
    }

    [Fact]
    public async Task GetSongs_ParsesStoredIssueJson_AndTreatsGarbageAsNoIssues()
    {
        using var db = NewContext();
        var graded = AddSong(db, 1, EnrichmentStatus.Matched);
        AddGrade(db, graded.Id, SongQualityVerdict.Wrong, 20, "Matched",
            issuesJson: """[{"code":"unsupported_identity","severity":"high"},{"code":"low_confidence","severity":"medium"}]""");
        var garbage = AddSong(db, 2, EnrichmentStatus.Matched);
        AddGrade(db, garbage.Id, SongQualityVerdict.Good, 80, "Matched", issuesJson: "not-json");

        var items = Read<List<object>>(
            await QualityEndpoints.GetSongs(db, Opts(), CancellationToken.None), "items");

        int IdOf(object row) => (int)row.GetType().GetProperty("songId")!.GetValue(row)!;
        List<GradingIssue> IssuesOf(object row) =>
            (List<GradingIssue>)row.GetType().GetProperty("issues")!.GetValue(row)!;
        var byId = items.ToDictionary(IdOf, IssuesOf);

        Assert.Equal(["unsupported_identity", "low_confidence"], byId[graded.Id].Select(i => i.Code));
        Assert.Empty(byId[garbage.Id]);
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

    [Fact]
    public async Task GetOverview_RanksWrongThenQuestionableFirst_EvenBehindManyUngradeable()
    {
        // Regression: Ungradeable is 0 in the enum, so ordering by the number filled all 50
        // worst-offender slots with it and the Inbox's AI-flagged queue read 0.
        using var db = NewContext();
        var n = AddGraded(db, 1, 60, SongQualityVerdict.Ungradeable, 0);
        n = AddGraded(db, n, 3, SongQualityVerdict.Wrong, 30, scoreStep: -10);        // 30, 20, 10
        n = AddGraded(db, n, 2, SongQualityVerdict.Questionable, 50, scoreStep: -5);  // 50, 45
        AddGraded(db, n, 5, SongQualityVerdict.Good, 80);

        var result = await QualityEndpoints.GetOverview(db, Opts(), CancellationToken.None);

        var worst = Read<List<object>>(result, "worstOffenders");
        Assert.Equal(50, worst.Count);
        Assert.Equal(
            ["Wrong", "Wrong", "Wrong", "Questionable", "Questionable", "Good", "Good", "Good", "Good", "Good"],
            worst.Take(10).Select(r => Prop<string>(r, "verdict")));
        Assert.Equal([10, 20, 30, 45, 50], worst.Take(5).Select(r => Prop<int>(r, "score")));
        Assert.All(worst.Skip(10), r => Assert.Equal("Ungradeable", Prop<string>(r, "verdict")));
        Assert.Equal(5, Read<int>(result, "aiFlaggedCount"));
    }

    [Fact]
    public async Task GetSongs_WrongOrQuestionable_ListsTheWholeQueue_WorstFirst()
    {
        using var db = NewContext();
        var n = AddGraded(db, 1, 60, SongQualityVerdict.Ungradeable, 0);
        n = AddGraded(db, n, 30, SongQualityVerdict.Questionable, 69, scoreStep: -1);
        n = AddGraded(db, n, 40, SongQualityVerdict.Wrong, 39, scoreStep: -1);
        AddGraded(db, n, 10, SongQualityVerdict.Excellent, 95);

        var result = await QualityEndpoints.GetSongs(
            db, Opts(), CancellationToken.None, category: "wrong-or-questionable");

        // All 70 — not capped at the overview's 50 — and the same figure the overview reports.
        Assert.Equal(70, Read<int>(result, "total"));
        var items = Read<List<object>>(result, "items");
        Assert.Equal(70, items.Count);
        Assert.All(items.Take(40), r => Assert.Equal("Wrong", Prop<string>(r, "verdict")));
        Assert.All(items.Skip(40), r => Assert.Equal("Questionable", Prop<string>(r, "verdict")));
        var scores = items.Select(r => Prop<int>(r, "score")).ToList();
        Assert.Equal(scores.Take(40).Order(), scores.Take(40));
        Assert.Equal(scores.Skip(40).Order(), scores.Skip(40));
        Assert.Equal(70, Read<int>(await QualityEndpoints.GetOverview(db, Opts(), CancellationToken.None), "aiFlaggedCount"));

        // Paged: the second page carries the remainder under the same total.
        var page2 = await QualityEndpoints.GetSongs(
            db, Opts(), CancellationToken.None, category: "wrong-or-questionable", skip: 50, take: 50);
        Assert.Equal(70, Read<int>(page2, "total"));
        Assert.Equal(20, Read<List<object>>(page2, "items").Count);
    }

    [Fact]
    public async Task GetSongs_All_PutsUngradeableLast()
    {
        using var db = NewContext();
        var n = AddGraded(db, 1, 1, SongQualityVerdict.Ungradeable, 0);
        n = AddGraded(db, n, 1, SongQualityVerdict.Excellent, 95);
        n = AddGraded(db, n, 1, SongQualityVerdict.Wrong, 20);
        AddGraded(db, n, 1, SongQualityVerdict.Questionable, 55);

        var items = Read<List<object>>(await QualityEndpoints.GetSongs(db, Opts(), CancellationToken.None), "items");

        Assert.Equal(["Wrong", "Questionable", "Excellent", "Ungradeable"], items.Select(r => Prop<string>(r, "verdict")));
    }

    [Fact]
    public async Task GetOverview_DirectoryWorstVerdict_IsUngradeableOnlyWhenNothingWasJudged()
    {
        using var db = NewContext();
        var n = AddGraded(db, 1, 1, SongQualityVerdict.Ungradeable, 0, directory: "/music/mixed");
        n = AddGraded(db, n, 1, SongQualityVerdict.Wrong, 20, directory: "/music/mixed");
        n = AddGraded(db, n, 1, SongQualityVerdict.Excellent, 95, directory: "/music/mixed");
        n = AddGraded(db, n, 1, SongQualityVerdict.Ungradeable, 0, directory: "/music/good");
        n = AddGraded(db, n, 1, SongQualityVerdict.Good, 80, directory: "/music/good");
        AddGraded(db, n, 2, SongQualityVerdict.Ungradeable, 0, directory: "/music/unjudged");

        var directories = Read<List<object>>(
            await QualityEndpoints.GetOverview(db, Opts(), CancellationToken.None), "directories");
        var worstByDir = directories.ToDictionary(d => Prop<string>(d, "directory"), d => Prop<string>(d, "worstVerdict"));

        Assert.Equal("Wrong", worstByDir["/music/mixed"]);
        Assert.Equal("Good", worstByDir["/music/good"]);
        Assert.Equal("Ungradeable", worstByDir["/music/unjudged"]);
    }
}
