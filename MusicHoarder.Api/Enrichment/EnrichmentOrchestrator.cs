using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Settings;

namespace MusicHoarder.Api.Enrichment;

public enum EnrichmentOutcome
{
    Matched,
    NeedsReview,
    Failed,
    Skipped,
}

public interface IEnrichmentOrchestrator
{
    Task<EnrichmentOutcome> ProcessSongAsync(int songId, CancellationToken ct = default);
    Task<IReadOnlySet<EnrichmentProvider>> GetEnabledProviderEnumsAsync(CancellationToken ct = default);
}

public class EnrichmentOrchestrator : IEnrichmentOrchestrator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEnumerable<IEnrichmentProvider> _providers;
    private readonly ILrcLibService _lrcLibService;
    private readonly IOptions<MusicEnricherOptions> _options;
    private readonly IRuntimeSettingsService _runtimeSettings;
    private readonly ILogger<EnrichmentOrchestrator> _logger;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _providerSemaphores = new();

    public EnrichmentOrchestrator(
        IServiceScopeFactory scopeFactory,
        IEnumerable<IEnrichmentProvider> providers,
        ILrcLibService lrcLibService,
        IOptions<MusicEnricherOptions> options,
        IRuntimeSettingsService runtimeSettings,
        ILogger<EnrichmentOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _providers = providers;
        _lrcLibService = lrcLibService;
        _options = options;
        _runtimeSettings = runtimeSettings;
        _logger = logger;

        InitializeSemaphores(options.Value);
    }

    public async Task<EnrichmentOutcome> ProcessSongAsync(int songId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var song = await dbContext.Songs
            .Include(s => s.ProviderAttempts)
            .FirstOrDefaultAsync(s => s.Id == songId, ct);

        if (song is null || song.IsDeleted)
        {
            _logger.LogDebug("Skipping enrichment for missing/deleted song {SongId}", songId);
            return EnrichmentOutcome.Skipped;
        }

        var effective = await _runtimeSettings.GetAsync(ct).ConfigureAwait(false);
        var enabledProviders = GetEnabledProviders(effective);
        var enabledEnums = BuildEnabledEnums(effective);

        if (enabledProviders.Count == 0)
        {
            _logger.LogDebug("No enrichment providers enabled, skipping song {SongId}", songId);
            return EnrichmentOutcome.Skipped;
        }

        var existingAttempts = song.ProviderAttempts.ToDictionary(a => a.Provider);
        var anyProviderActed = false;
        var matchApplied = false;

        foreach (var provider in enabledProviders)
        {
            if (!provider.CanHandle(song))
                continue;

            var providerEnum = MapProviderName(provider.Name);
            if (providerEnum is null)
                continue;

            if (existingAttempts.TryGetValue(providerEnum.Value, out var existing))
            {
                if (existing.Status is ProviderAttemptStatus.Matched
                    or ProviderAttemptStatus.NoMatch
                    or ProviderAttemptStatus.Failed)
                    continue;

                if (existing.Status == ProviderAttemptStatus.RateLimited
                    && existing.RetryAfterUtc > DateTime.UtcNow)
                    continue;
            }

            anyProviderActed = true;
            song.RecordEnrichmentAttempt();

            var semaphore = GetProviderSemaphore(provider.Name);
            await semaphore.WaitAsync(ct);
            try
            {
                var outcome = await TryProviderAsync(provider, providerEnum.Value, song, dbContext, existingAttempts, ct);
                if (outcome == EnrichmentOutcome.Matched)
                {
                    await dbContext.SaveChangesAsync(ct);
                    await FetchLyricsForSongAsync(song, dbContext, ct);
                    return EnrichmentOutcome.Matched;
                }

                if (outcome == EnrichmentOutcome.NeedsReview
                    && song.EnrichmentStatus != EnrichmentStatus.Pending)
                    matchApplied = true;
            }
            finally
            {
                semaphore.Release();
            }
        }

        if (!anyProviderActed)
        {
            if (song.EnrichmentStatus != EnrichmentStatus.Pending)
                return EnrichmentOutcome.Skipped;

            var currentSummary = song.ComputeSummaryStatus(enabledEnums);
            if (currentSummary == song.EnrichmentStatus)
                return EnrichmentOutcome.Skipped;
        }

        if (!matchApplied)
            UpdateSummaryStatus(song, enabledEnums);

        await dbContext.SaveChangesAsync(ct);

        return song.EnrichmentStatus switch
        {
            EnrichmentStatus.Matched => EnrichmentOutcome.Matched,
            EnrichmentStatus.NeedsReview => EnrichmentOutcome.NeedsReview,
            EnrichmentStatus.Failed => EnrichmentOutcome.Failed,
            _ => EnrichmentOutcome.Skipped,
        };
    }

    public async Task<IReadOnlySet<EnrichmentProvider>> GetEnabledProviderEnumsAsync(CancellationToken ct = default)
    {
        var effective = await _runtimeSettings.GetAsync(ct).ConfigureAwait(false);
        return BuildEnabledEnums(effective);
    }

    private IReadOnlySet<EnrichmentProvider> BuildEnabledEnums(EffectiveSettings effective)
    {
        var set = new HashSet<EnrichmentProvider>();
        foreach (var p in _providers)
        {
            if (!IsProviderEnabled(p, effective)) continue;
            var e = MapProviderName(p.Name);
            if (e is not null) set.Add(e.Value);
        }
        return set;
    }

    private async Task<EnrichmentOutcome> TryProviderAsync(
        IEnrichmentProvider provider,
        EnrichmentProvider providerEnum,
        SongMetadata song,
        MusicHoarderDbContext dbContext,
        Dictionary<EnrichmentProvider, SongProviderAttempt> existingAttempts,
        CancellationToken ct)
    {
        ProviderOutcome outcome;
        try
        {
            outcome = await provider.TryEnrichAsync(song, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Provider {Provider} threw for {Track} (SongId={SongId})",
                provider.Name, song.TrackLabel, song.Id);

            UpsertAttempt(song, providerEnum, ProviderAttemptStatus.Failed,
                error: ex.Message, existingAttempts: existingAttempts);
            return EnrichmentOutcome.Failed;
        }

        switch (outcome)
        {
            case ProviderRateLimited rl:
                _logger.LogInformation("Provider {Provider} rate-limited for {Track} (SongId={SongId}), retry after {Delay}s",
                    provider.Name, song.TrackLabel, song.Id, rl.RetryAfter.TotalSeconds);
                UpsertAttempt(song, providerEnum, ProviderAttemptStatus.RateLimited,
                    retryAfterUtc: DateTime.UtcNow + rl.RetryAfter, existingAttempts: existingAttempts);
                return EnrichmentOutcome.Skipped;

            case ProviderNoMatch noMatch:
                _logger.LogDebug("Provider {Provider} returned no match for {Track} (SongId={SongId})",
                    provider.Name, song.TrackLabel, song.Id);
                var noMatchJson = noMatch.BestCandidate is { } candidate
                    ? SerializeResult(candidate)
                    : null;
                UpsertAttempt(song, providerEnum, ProviderAttemptStatus.NoMatch,
                    matchedDataJson: noMatchJson, existingAttempts: existingAttempts);
                return EnrichmentOutcome.NeedsReview;

            case ProviderMatched matched:
            {
                var result = matched.Result;

                if (result.RecommendedStatus == EnrichmentStatus.Matched)
                {
                    ApplyProviderResult(song, result);
                    UpsertAttempt(song, providerEnum, ProviderAttemptStatus.Matched,
                        matchedDataJson: SerializeResult(result), existingAttempts: existingAttempts);

                    _logger.LogInformation(
                        "Enrichment matched {Track} (SongId={SongId}) via {Provider} with confidence {Confidence:F3}",
                        song.TrackLabel, song.Id, result.MatchedBy, result.MatchConfidence);
                    return EnrichmentOutcome.Matched;
                }

                // Sub-threshold / blocked: persist the candidate on the attempt for review tooling
                // but DO NOT overwrite the row's Artist/Title/Album/IDs. Writing a wrong-but-plausible
                // candidate to the row poisons subsequent providers' search queries (they read
                // song.Artist/Title) and surfaces wrong values in the UI as if they were enriched.
                var warningsJson = result.MatchWarnings.Count > 0
                    ? JsonSerializer.Serialize(result.MatchWarnings)
                    : null;
                song.MarkProviderNeedsReview(result.MatchedBy, result.MatchConfidence, warningsJson);
                UpsertAttempt(song, providerEnum, ProviderAttemptStatus.Matched,
                    matchedDataJson: SerializeResult(result), existingAttempts: existingAttempts);

                _logger.LogInformation(
                    "Provider {Provider} returned low-confidence match for {Track} (SongId={SongId}), confidence={Confidence:F3}",
                    provider.Name, song.TrackLabel, song.Id, result.MatchConfidence);
                return EnrichmentOutcome.NeedsReview;
            }

            default:
                return EnrichmentOutcome.Skipped;
        }
    }

    private static void UpsertAttempt(
        SongMetadata song,
        EnrichmentProvider provider,
        ProviderAttemptStatus status,
        DateTime? retryAfterUtc = null,
        string? matchedDataJson = null,
        string? error = null,
        Dictionary<EnrichmentProvider, SongProviderAttempt>? existingAttempts = null)
    {
        if (existingAttempts is not null && existingAttempts.TryGetValue(provider, out var existing))
        {
            existing.Status = status;
            existing.AttemptedAtUtc = DateTime.UtcNow;
            existing.RetryAfterUtc = retryAfterUtc;
            existing.MatchedDataJson = matchedDataJson;
            existing.Error = error;
        }
        else
        {
            var attempt = new SongProviderAttempt
            {
                SongId = song.Id,
                Provider = provider,
                Status = status,
                AttemptedAtUtc = DateTime.UtcNow,
                RetryAfterUtc = retryAfterUtc,
                MatchedDataJson = matchedDataJson,
                Error = error,
            };
            song.ProviderAttempts.Add(attempt);
            existingAttempts?.TryAdd(provider, attempt);
        }
    }

    private static void UpdateSummaryStatus(SongMetadata song, IReadOnlySet<EnrichmentProvider> enabledProviders)
    {
        var newStatus = song.ComputeSummaryStatus(enabledProviders);
        if (newStatus != song.EnrichmentStatus)
        {
            song.EnrichmentStatus = newStatus;
            if (newStatus is EnrichmentStatus.NeedsReview or EnrichmentStatus.Failed)
            {
                song.EnrichedAtUtc = DateTime.UtcNow;
            }
        }
    }

    private IReadOnlyList<IEnrichmentProvider> GetEnabledProviders(EffectiveSettings effective)
    {
        return _providers
            .Where(p => IsProviderEnabled(p, effective))
            .OrderBy(p => p.Priority)
            .ToList();
    }

    private static bool IsProviderEnabled(IEnrichmentProvider provider, EffectiveSettings effective)
    {
        return provider.Name switch
        {
            "AcoustID" => effective.EnableAcoustIdProvider,
            "MusicBrainzWeb" => effective.EnableMusicBrainzWebProvider,
            "SpotifyAPI" => effective.EnableSpotifyApiProvider,
            "Tracker" => effective.EnableTrackerProvider,
            _ => true
        };
    }

    internal static EnrichmentProvider? MapProviderName(string name) => name switch
    {
        "AcoustID" => EnrichmentProvider.AcoustID,
        "SpotifyAPI" => EnrichmentProvider.SpotifyAPI,
        "MusicBrainzWeb" => EnrichmentProvider.MusicBrainzWeb,
        "Tracker" => EnrichmentProvider.Tracker,
        _ => null
    };

    private void InitializeSemaphores(MusicEnricherOptions opts)
    {
        _providerSemaphores["AcoustID"] = new SemaphoreSlim(opts.AcoustIdConcurrency, opts.AcoustIdConcurrency);
        _providerSemaphores["SpotifyAPI"] = new SemaphoreSlim(opts.SpotifyApiConcurrency, opts.SpotifyApiConcurrency);
        _providerSemaphores["MusicBrainzWeb"] = new SemaphoreSlim(1, 1);
        _providerSemaphores["Tracker"] = new SemaphoreSlim(1, 1);
    }

    private SemaphoreSlim GetProviderSemaphore(string providerName) =>
        _providerSemaphores.GetOrAdd(providerName, _ => new SemaphoreSlim(1, 1));

    private static void ApplyProviderResult(SongMetadata song, EnrichmentProviderResult result)
    {
        var warningsJson = result.MatchWarnings.Count > 0
            ? JsonSerializer.Serialize(result.MatchWarnings)
            : null;

        song.ApplyEnrichmentMatch(new EnrichmentMatchData(
            result.Artist,
            result.AlbumArtist,
            result.Title,
            result.Year,
            result.TrackNumber,
            result.MusicBrainzId,
            result.MusicBrainzReleaseId,
            result.SpotifyId,
            result.AcoustIdTrackId,
            result.Isrc,
            result.MatchedBy,
            result.MatchConfidence,
            warningsJson,
            result.RecommendedStatus,
            result.Album));
    }

    private static string SerializeResult(EnrichmentProviderResult result) =>
        JsonSerializer.Serialize(result);

    private async Task FetchLyricsForSongAsync(SongMetadata song, MusicHoarderDbContext dbContext, CancellationToken ct)
    {
        if (!song.IsReadyForLyricsFetch)
        {
            _logger.LogDebug("Skipping lyrics fetch for {Track} (SongId={SongId}): not eligible (status={LyricsStatus})",
                song.TrackLabel, song.Id, song.LyricsStatus);
            return;
        }

        try
        {
            var result = await _lrcLibService.FetchLyricsAsync(song, ct);

            if (result is null)
            {
                song.MarkLyricsNotFound();
                _logger.LogDebug("No lyrics found for {Track} (SongId={SongId})", song.TrackLabel, song.Id);
            }
            else if (result.IsInstrumental)
            {
                song.ApplyLyricsResult(null, null, true, result.LrclibId);
                _logger.LogInformation("Lyrics: instrumental confirmed for {Track} (SongId={SongId})", song.TrackLabel, song.Id);
            }
            else
            {
                song.ApplyLyricsResult(result.SyncedLyrics, result.PlainLyrics, false, result.LrclibId);
                var kind = result.SyncedLyrics is not null ? "synced" : "plain";
                _logger.LogInformation("Lyrics: fetched ({Kind}) for {Track} (SongId={SongId})", kind, song.TrackLabel, song.Id);
            }

            await dbContext.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            song.MarkLyricsFailed();
            await dbContext.SaveChangesAsync(ct);
            _logger.LogWarning(ex, "Lyrics fetch failed for {Track} (SongId={SongId})", song.TrackLabel, song.Id);
        }
    }
}
