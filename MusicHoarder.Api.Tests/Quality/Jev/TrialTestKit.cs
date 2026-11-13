using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;
using MusicHoarder.Api.Quality.Jev;

namespace MusicHoarder.Api.Tests.Quality.Jev;

/// <summary>Shared fixtures for the grader-trial tests.</summary>
internal static class TrialTestKit
{
    public static MusicHoarderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    public static SongMetadata AddSong(MusicHoarderDbContext db, Guid? owner = null, string title = "Blood On My Jeans")
    {
        var song = new SongMetadata
        {
            OwnerUserId = owner ?? WellKnownUsers.OwnerId,
            SourcePath = $"/root/music/a/{title}.mp3",
            FileName = $"{title}.mp3",
            Extension = ".mp3",
            FileSizeBytes = 5_800_000,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Title = title,
            Artist = "Juice WRLD",
            EnrichmentStatus = EnrichmentStatus.Matched,
        };
        db.Songs.Add(song);
        return song;
    }

    /// <summary>A decision response in the Decisions API reference's shape, for the song question set.</summary>
    public static DecisionResult SongAnswers(
        string verdict = "good", double pWrong = 0.05, double pQuestionable = 0.10,
        double quality = 2.0, double unsupported = 0.03, decimal cost = 0.0003m) =>
        new(new Dictionary<string, DecisionAnswer>
            {
                ["verdict"] = new("choice", Choice: verdict, Confidence: 0.8, Probabilities: new Dictionary<string, double>
                {
                    ["excellent"] = 0.1, ["good"] = 1 - 0.1 - pWrong - pQuestionable,
                    ["questionable"] = pQuestionable, ["wrong"] = pWrong, ["ungradeable"] = 0,
                }),
                ["quality"] = new("score", Score: quality, Confidence: 0.9),
                ["unsupported_identity"] = new("noul", Noul: unsupported),
                ["path_metadata_mismatch"] = new("noul", Noul: 0.02),
                ["identity_changed"] = new("choice", Choice: "none", Probabilities: new Dictionary<string, double> { ["none"] = 0.9 }),
                ["single_source"] = new("noul", Noul: 0.1),
            },
            "typesafe/jev-1.14-20261014", "TypeSafe", 6000, 70, cost, default);

    public static SongQualityGrade Grade(int songId, string fingerprint, SongQualityVerdict verdict = SongQualityVerdict.Good, Guid? owner = null) => new()
    {
        SongId = songId,
        OwnerUserId = owner ?? WellKnownUsers.OwnerId,
        Score = 80,
        Verdict = verdict,
        Summary = "Looks right.",
        Model = "deepseek/deepseek-v4-flash",
        PromptVersion = QualityGradingPrompt.Version,
        InputFingerprint = fingerprint,
        DurationMs = 12_000,
        RawResponseJson = """{"score":80,"verdict":"good"}""",
        GradedAtUtc = DateTime.UtcNow,
    };

    public sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    public sealed class ScopeFactory(MusicHoarderDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(new Provider(db));
        private sealed class Scope(IServiceProvider provider) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = provider;
            public void Dispose() { }
        }
        private sealed class Provider(MusicHoarderDbContext db) : IServiceProvider
        {
            public object? GetService(Type serviceType) => serviceType == typeof(MusicHoarderDbContext) ? db : null;
        }
    }

    public sealed class FixedResolver : IDestinationPathResolver
    {
        public string ResolvePath(SongMetadata song) => "/dest/preview.mp3";
    }

    public sealed class StubOwner : IOwnerLookupService
    {
        public Guid OwnerUserId => WellKnownUsers.OwnerId;
    }

    public sealed class FakeDecisionClient(Func<DecisionRequest, DecisionResult> answer, bool configured = true) : IDecisionClient
    {
        public List<DecisionRequest> Requests { get; } = [];
        public bool IsConfigured => configured;

        public Task<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken ct = default)
        {
            lock (Requests) Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    public sealed class FakeChatClient(string content, decimal? cost = 0.002m) : IChatCompletionClient
    {
        public int CallCount { get; private set; }
        public bool IsConfigured => true;

        public Task<ChatCompletionResult> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new ChatCompletionResult(content, 6000, 1500, "stop", CostUsd: cost));
        }
    }

    public sealed class NoAlbums : IAlbumGradingService
    {
        public Task<GradeAlbumResult> GradeAlbumAsync(int canonicalAlbumId, bool force = false, CancellationToken ct = default) =>
            Task.FromResult(new GradeAlbumResult(GradeOutcome.NotFound, null));

        public Task<AlbumGradingDossier?> BuildDossierAsync(int canonicalAlbumId, CancellationToken ct = default) =>
            Task.FromResult<AlbumGradingDossier?>(null);
    }
}
