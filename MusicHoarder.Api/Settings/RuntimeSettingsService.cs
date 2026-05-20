using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Settings;

public sealed class RuntimeSettingsService : IRuntimeSettingsService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<MusicEnricherOptions> _options;

    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private EffectiveSettings? _cache;

    public RuntimeSettingsService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<MusicEnricherOptions> options)
    {
        _scopeFactory = scopeFactory;
        _options = options;
    }

    public async Task<EffectiveSettings> GetAsync(CancellationToken ct = default)
    {
        if (_cache is { } cached)
            return cached;

        await _cacheLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache is { } again)
                return again;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
            var row = await db.RuntimeSettings.AsNoTracking().FirstOrDefaultAsync(ct).ConfigureAwait(false);
            _cache = Build(row);
            return _cache;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task<EffectiveSettings> UpdateAsync(RuntimeSettingsUpdate update, CancellationToken ct = default)
    {
        await _cacheLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
            var row = await db.RuntimeSettings.FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (row == null)
            {
                row = new RuntimeSettings();
                db.RuntimeSettings.Add(row);
            }

            if (update.EnableAcoustIdProvider.HasValue) row.EnableAcoustIdProvider = update.EnableAcoustIdProvider;
            if (update.EnableMusicBrainzWebProvider.HasValue) row.EnableMusicBrainzWebProvider = update.EnableMusicBrainzWebProvider;
            if (update.EnableSpotifyApiProvider.HasValue) row.EnableSpotifyApiProvider = update.EnableSpotifyApiProvider;
            if (update.EnableTrackerProvider.HasValue) row.EnableTrackerProvider = update.EnableTrackerProvider;
            if (update.SpotifyApiMatchedThreshold.HasValue) row.SpotifyApiMatchedThreshold = update.SpotifyApiMatchedThreshold;
            if (update.AcoustIdScoreThreshold.HasValue) row.AcoustIdScoreThreshold = update.AcoustIdScoreThreshold;
            if (update.EnrichmentWorkerConcurrency.HasValue) row.EnrichmentWorkerConcurrency = update.EnrichmentWorkerConcurrency;
            if (update.LibraryBuilderWorkerConcurrency.HasValue) row.LibraryBuilderWorkerConcurrency = update.LibraryBuilderWorkerConcurrency;

            row.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            _cache = Build(row);
            return _cache;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private EffectiveSettings Build(RuntimeSettings? row)
    {
        var defaults = _options.CurrentValue;
        return new EffectiveSettings(
            EnableAcoustIdProvider: row?.EnableAcoustIdProvider ?? defaults.EnableAcoustIdProvider,
            EnableMusicBrainzWebProvider: row?.EnableMusicBrainzWebProvider ?? defaults.EnableMusicBrainzWebProvider,
            EnableSpotifyApiProvider: row?.EnableSpotifyApiProvider ?? defaults.EnableSpotifyApiProvider,
            EnableTrackerProvider: row?.EnableTrackerProvider ?? defaults.EnableTrackerProvider,
            SpotifyApiMatchedThreshold: row?.SpotifyApiMatchedThreshold ?? defaults.SpotifyApiMatchedThreshold,
            AcoustIdScoreThreshold: row?.AcoustIdScoreThreshold ?? defaults.AcoustIdScoreThreshold,
            EnrichmentWorkerConcurrency: row?.EnrichmentWorkerConcurrency ?? defaults.EnrichmentWorkerConcurrency,
            LibraryBuilderWorkerConcurrency: row?.LibraryBuilderWorkerConcurrency ?? defaults.LibraryBuilderWorkerConcurrency,
            UpdatedAtUtc: row?.UpdatedAtUtc);
    }
}
