using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.BackgroundWork;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Settings;
using MusicHoarder.Api.Storage;
using MusicHoarder.Api.Tests.Download;

namespace MusicHoarder.Api.Tests.BackgroundWork;

internal sealed class BackgroundWorkHarness : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly string _database = Guid.NewGuid().ToString("N");

    public LyricsTimingOptions Lyrics { get; } = new();
    public MusicEnricherOptions Enricher { get; } = new() { SourceDirectory = "/src", DestinationDirectory = "/dst" };
    public QualityGradingOptions Grading { get; } = new() { ApiKey = "key" };
    public StubSettings Settings { get; } = new();
    public StubTranscriber Transcriber { get; } = new();
    public FakeFreeDiskSpace Disk { get; } = new();
    public StorageUsageSnapshotStore Storage { get; } = new();
    public HashSet<EnrichmentProvider> Providers { get; } = [EnrichmentProvider.AcoustID, EnrichmentProvider.SpotifyAPI];
    public BackgroundWorkStatus Status { get; }
    public DateTime Now { get; }

    public BackgroundWorkHarness(DateTime? now = null, Func<MusicHoarderDbContext>? contexts = null)
    {
        Now = now ?? BackgroundWorkTestData.Now;
        var clock = BackgroundWorkTestData.Clock(Now);
        Status = new BackgroundWorkStatus(clock);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(clock);
        services.AddSingleton(Status);
        services.AddSingleton<IOptionsMonitor<LyricsTimingOptions>>(new FixedMonitor<LyricsTimingOptions>(Lyrics));
        services.AddSingleton<IOptionsMonitor<MusicEnricherOptions>>(new FixedMonitor<MusicEnricherOptions>(Enricher));
        services.AddSingleton<IOptionsMonitor<QualityGradingOptions>>(new FixedMonitor<QualityGradingOptions>(Grading));
        services.AddSingleton<IOptions<MusicEnricherOptions>>(Microsoft.Extensions.Options.Options.Create(Enricher));
        services.AddSingleton<IRuntimeSettingsService>(Settings);
        services.AddSingleton<IFreeDiskSpace>(Disk);
        services.AddSingleton(Storage);
        services.AddSingleton<ILyricsTranscriptionService>(Transcriber);
        services.AddSingleton<IEnrichmentOrchestrator>(new StubOrchestrator(Providers));
        services.AddScoped(_ => contexts?.Invoke() ?? new MusicHoarderDbContext(
            new DbContextOptionsBuilder<MusicHoarderDbContext>().UseInMemoryDatabase(_database).Options));
        services.AddBackgroundWork();
        _provider = services.BuildServiceProvider();
    }

    public IServiceScope Scope() => _provider.CreateScope();

    public void Seed(Action<MusicHoarderDbContext> seed)
    {
        using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        seed(db);
        db.SaveChanges();
    }

    public async Task<BackgroundJobDetailDto> DescribeAsync(string id)
    {
        using var scope = Scope();
        var insight = scope.ServiceProvider.GetServices<IBackgroundJobInsight>().Single(i => i.Id == id);
        return await insight.DescribeAsync(CancellationToken.None);
    }

    public async Task<BackgroundJobSummaryDto> SummarizeAsync(string id)
    {
        using var scope = Scope();
        var insight = scope.ServiceProvider.GetServices<IBackgroundJobInsight>().Single(i => i.Id == id);
        return await insight.SummarizeAsync(CancellationToken.None);
    }

    public void Dispose() => _provider.Dispose();

    public sealed class StubSettings : IRuntimeSettingsService
    {
        public bool AutoSync { get; set; }
        public bool Grading { get; set; } = true;
        public bool AlbumCompletion { get; set; }
        public bool AutoDownloadVideos { get; set; }

        public Task<EffectiveSettings> GetAsync(CancellationToken ct = default) => Task.FromResult(
            new EffectiveSettings(
                true, true, true, true, true, true, Grading, true, AlbumCompletion, false, null,
                LyricsAutoSyncEnabled: AutoSync, LyricsAutoSyncChosen: true, AutoDownloadMusicVideos: AutoDownloadVideos));

        public Task<EffectiveSettings> UpdateAsync(RuntimeSettingsUpdate update, CancellationToken ct = default)
            => GetAsync(ct);
    }

    public sealed class StubTranscriber : ILyricsTranscriptionService
    {
        public bool IsConfigured { get; set; } = true;

        public Task<TranscriptionResult> TranscribeAsync(
            SongMetadata song, string audioFilePath, CancellationToken ct = default, Func<CancellationToken, Task>? beforeRetry = null)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TimedWord>> TranscribeClipAsync(
            string audioFilePath, double startSeconds, double lengthSeconds, string? promptText = null,
            CancellationToken ct = default, Func<CancellationToken, Task>? beforeRetry = null)
            => throw new NotSupportedException();
    }

    private sealed class StubOrchestrator(HashSet<EnrichmentProvider> enabled) : IEnrichmentOrchestrator
    {
        public Task<EnrichmentOutcome> ProcessSongAsync(int songId, CancellationToken ct = default)
            => Task.FromResult(EnrichmentOutcome.Skipped);

        public Task<IReadOnlySet<EnrichmentProvider>> GetEnabledProviderEnumsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<EnrichmentProvider>>(enabled);

        public Task<IReadOnlySet<EnrichmentProvider>> GetApplicableProvidersAsync(
            SongMetadata song, CancellationToken ct = default) => GetEnabledProviderEnumsAsync(ct);

        public Task<bool> FetchLyricsForSongAsync(int songId, CancellationToken ct = default) => Task.FromResult(false);

        public Task<bool> RecheckLyricsForSongAsync(int songId, bool force = false, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    private sealed class FixedMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

internal static class DetailExtensions
{
    public static T Panel<T>(this BackgroundJobDetailDto detail, string titleStartsWith) where T : BackgroundPanelDto
        => detail.Panels.OfType<T>().Single(p => p.Title.StartsWith(titleStartsWith, StringComparison.Ordinal));

    public static long Segment(this BreakdownPanelDto panel, string key)
        => panel.Segments.SingleOrDefault(s => s.Key == key)?.Count ?? 0;

    public static FigureDto Item(this FiguresPanelDto panel, string label)
        => panel.Items.Single(i => i.Label == label);
}
