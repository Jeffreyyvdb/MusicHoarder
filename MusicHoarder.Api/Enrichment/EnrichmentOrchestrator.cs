using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Matching;
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
            .IgnoreQueryFilters()
            .Include(s => s.ProviderAttempts)
            .FirstOrDefaultAsync(s => s.Id == songId, ct);

        if (song is null || song.IsDeleted)
        {
            _logger.LogDebug("Skipping enrichment for missing/deleted song {SongId}", songId);
            return EnrichmentOutcome.Skipped;
        }

        if (song.IsManuallyApproved)
        {
            _logger.LogDebug("Skipping enrichment for manually-approved (locked) song {SongId}", songId);
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

        // Identifiers a provider already wrote on the row stay authoritative; we only ever fill
        // empty identifier fields with what an earlier provider discovered this pass, then restore
        // the originals before the row is finalized. Snapshot so the gossip never persists.
        var originalMbId = song.MusicBrainzId;
        var originalIsrc = song.Isrc;

        // Run every eligible provider (no stop-at-first-match): a single provider can no
        // longer finalize a song. The final decision is made by ConsensusEvaluator below.
        // Providers read the original *name* fields (never mutated mid-pass, so a wrong-but-
        // plausible hit can't poison a later query), but high-precision identifiers (MBID/ISRC)
        // discovered by an earlier provider ARE handed to later ones so they can do an exact
        // lookup instead of a fuzzy name search — genuine corroboration, not poisoning.
        foreach (var provider in enabledProviders)
        {
            if (!provider.CanHandle(song))
                continue;

            var providerEnum = MapProviderName(provider.Name);
            if (providerEnum is null)
                continue;

            if (existingAttempts.TryGetValue(providerEnum.Value, out var existing))
            {
                // Matched candidates are kept; re-running won't improve them.
                if (existing.Status == ProviderAttemptStatus.Matched)
                    continue;

                // Terminal NoMatch/Failed are retried once their cooldown elapses (catalogs grow).
                if (existing.Status is ProviderAttemptStatus.NoMatch or ProviderAttemptStatus.Failed)
                {
                    if (existing.NextRetryAfterUtc is null || existing.NextRetryAfterUtc > DateTime.UtcNow)
                        continue;
                }

                if (existing.Status == ProviderAttemptStatus.RateLimited
                    && existing.RetryAfterUtc > DateTime.UtcNow)
                    continue;
            }

            anyProviderActed = true;
            song.RecordEnrichmentAttempt();

            var semaphore = GetProviderSemaphore(provider.Name);
            await semaphore.WaitAsync(ct);
            EnrichmentProviderResult? matchedResult;
            try
            {
                matchedResult = await PersistProviderOutcomeAsync(provider, providerEnum.Value, song, existingAttempts, ct);
            }
            finally
            {
                semaphore.Release();
            }

            ApplyIdentifierHints(song, matchedResult);
        }

        // Restore the row's original identifiers; only the consensus winner gets to write them.
        song.MusicBrainzId = originalMbId;
        song.Isrc = originalIsrc;

        var consensus = ConsensusEvaluator.Evaluate(
            song, enabledEnums, BuildIdentityOptions(), _options.Value.ConsensusCorroborationFloor);

        // Visibility safety net: a Pending verdict that isn't waiting on a rate-limited provider
        // means no enabled provider can act on this song at all (e.g. no fingerprint and nothing
        // searchable). Surface it for review so it stays visible in the library rather than
        // dead-ending silently in Pending — we never force a terminal Failed for unmatched/leaked
        // tracks (the user keeps them).
        if (consensus.Status == EnrichmentStatus.Pending && !HasActiveRateLimit(song, enabledEnums))
            consensus = consensus with { Status = EnrichmentStatus.NeedsReview };

        // Nothing acted and the verdict is unchanged → no-op.
        if (!anyProviderActed && consensus.Status == song.EnrichmentStatus)
            return ToOutcome(song.EnrichmentStatus);

        var matched = ApplyConsensus(song, consensus, dbContext);
        await dbContext.SaveChangesAsync(ct);

        if (matched)
            await FetchLyricsForSongAsync(song, dbContext, ct);

        return ToOutcome(song.EnrichmentStatus);
    }

    private static bool HasActiveRateLimit(SongMetadata song, IReadOnlySet<EnrichmentProvider> enabled)
        => song.ProviderAttempts.Any(a =>
            enabled.Contains(a.Provider)
            && a.Status == ProviderAttemptStatus.RateLimited
            && a.RetryAfterUtc > DateTime.UtcNow);

    private static EnrichmentOutcome ToOutcome(EnrichmentStatus status) => status switch
    {
        EnrichmentStatus.Matched => EnrichmentOutcome.Matched,
        EnrichmentStatus.NeedsReview => EnrichmentOutcome.NeedsReview,
        EnrichmentStatus.Failed => EnrichmentOutcome.Failed,
        _ => EnrichmentOutcome.Skipped,
    };

    private IdentityMatchOptions BuildIdentityOptions()
    {
        var o = _options.Value;
        return new IdentityMatchOptions(o.IdentityArtistThreshold, o.IdentityTitleThreshold, o.IdentityDurationDeltaSeconds);
    }

    /// <summary>
    /// Applies the consensus verdict to the row. Returns true when the song ended Matched.
    /// On NeedsReview the row's user-visible metadata is NOT overwritten — only the
    /// review bookkeeping (best candidate + confidence) is recorded.
    /// </summary>
    private bool ApplyConsensus(SongMetadata song, ConsensusEvaluator.ConsensusResult consensus, MusicHoarderDbContext dbContext)
    {
        switch (consensus.Status)
        {
            case EnrichmentStatus.Matched when consensus.Winner is not null:
                var warningsJson = consensus.Winner.MatchWarnings.Count > 0
                    ? JsonSerializer.Serialize(consensus.Winner.MatchWarnings)
                    : null;

                // Quality-aware, non-destructive merge: fill holes, keep curated values unless a
                // strong multi-provider consensus justifies an upgrade; record every change.
                var changes = MetadataMerger.ApplyMatch(
                    song, consensus.Winner, consensus.Confidence, consensus.AgreeingProviders.Count,
                    _options.Value.AutoUpgradeConfidence, warningsJson);

                var now = DateTime.UtcNow;
                foreach (var c in changes)
                {
                    dbContext.SongMetadataChanges.Add(new SongMetadataChange
                    {
                        SongId = song.Id,
                        FieldName = c.Field,
                        OldValue = c.OldValue,
                        NewValue = c.NewValue,
                        Source = consensus.Winner.MatchedBy,
                        Confidence = consensus.Confidence,
                        CreatedAtUtc = now,
                        AppliedAtUtc = c.Applied ? now : null,
                    });
                }

                _logger.LogInformation(
                    "Enrichment matched {Track} (SongId={SongId}) by consensus of [{Providers}] confidence {Confidence:F3}; {Applied} applied, {Proposed} proposed",
                    song.TrackLabel, song.Id, string.Join(", ", consensus.AgreeingProviders), consensus.Confidence,
                    changes.Count(c => c.Applied), changes.Count(c => !c.Applied));
                return true;

            case EnrichmentStatus.NeedsReview:
                if (consensus.Winner is not null)
                {
                    var reviewWarnings = consensus.Winner.MatchWarnings.Count > 0
                        ? JsonSerializer.Serialize(consensus.Winner.MatchWarnings)
                        : null;
                    song.MarkProviderNeedsReview(consensus.Winner.MatchedBy, consensus.Confidence, reviewWarnings);
                }
                else
                {
                    song.EnrichmentStatus = EnrichmentStatus.NeedsReview;
                    song.EnrichedAtUtc = DateTime.UtcNow;
                }
                return false;

            case EnrichmentStatus.Failed:
                var failError = song.ProviderAttempts
                    .Where(a => a.Status == ProviderAttemptStatus.Failed)
                    .Select(a => a.Error)
                    .FirstOrDefault(e => !string.IsNullOrWhiteSpace(e));
                song.MarkEnrichmentFailed(failError ?? "All enabled providers failed.");
                return false;

            default:
                // Pending — incomplete picture (provider rate-limited or not all attempted yet).
                return false;
        }
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

    /// <summary>
    /// Runs a provider and records its attempt. Does NOT mutate the song's row — the row
    /// decision is deferred to <see cref="ConsensusEvaluator"/>. A <see cref="ProviderMatched"/>
    /// is always stored as a <see cref="ProviderAttemptStatus.Matched"/> attempt with its
    /// candidate JSON (the candidate's own RecommendedStatus is preserved in the JSON for the
    /// evaluator to weigh).
    /// </summary>
    private async Task<EnrichmentProviderResult?> PersistProviderOutcomeAsync(
        IEnrichmentProvider provider,
        EnrichmentProvider providerEnum,
        SongMetadata song,
        Dictionary<EnrichmentProvider, SongProviderAttempt> existingAttempts,
        CancellationToken ct)
    {
        // The term the provider searched, for the review timeline (what was queried + click-through).
        var searchQuery = ProviderSearchQuery.For(providerEnum, song, _options.Value.SourceDirectory);

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
                error: ex.Message, existingAttempts: existingAttempts,
                nextRetryAfterUtc: CooldownFor(ProviderAttemptStatus.Failed), searchQuery: searchQuery);
            return null;
        }

        switch (outcome)
        {
            case ProviderRateLimited rl:
                _logger.LogInformation("Provider {Provider} rate-limited for {Track} (SongId={SongId}), retry after {Delay}s",
                    provider.Name, song.TrackLabel, song.Id, rl.RetryAfter.TotalSeconds);
                UpsertAttempt(song, providerEnum, ProviderAttemptStatus.RateLimited,
                    retryAfterUtc: DateTime.UtcNow + rl.RetryAfter, existingAttempts: existingAttempts,
                    searchQuery: searchQuery);
                return null;

            case ProviderNoMatch noMatch:
                _logger.LogDebug("Provider {Provider} returned no match for {Track} (SongId={SongId})",
                    provider.Name, song.TrackLabel, song.Id);
                var noMatchJson = noMatch.BestCandidate is { } candidate
                    ? SerializeResult(candidate)
                    : null;
                UpsertAttempt(song, providerEnum, ProviderAttemptStatus.NoMatch,
                    matchedDataJson: noMatchJson, existingAttempts: existingAttempts,
                    nextRetryAfterUtc: CooldownFor(ProviderAttemptStatus.NoMatch), searchQuery: searchQuery);
                return null;

            case ProviderMatched matched:
                _logger.LogDebug(
                    "Provider {Provider} produced a candidate for {Track} (SongId={SongId}), confidence={Confidence:F3}, recommends={Recommended}",
                    provider.Name, song.TrackLabel, song.Id, matched.Result.MatchConfidence, matched.Result.RecommendedStatus);
                UpsertAttempt(song, providerEnum, ProviderAttemptStatus.Matched,
                    matchedDataJson: SerializeResult(matched.Result), existingAttempts: existingAttempts,
                    searchQuery: searchQuery);
                return matched.Result;

            default:
                return null;
        }
    }

    /// <summary>
    /// Fills the row's empty MBID/ISRC with a high-precision identifier an earlier provider just
    /// discovered, so a later provider can do an exact lookup this pass. Names are never gossiped,
    /// and these writes are reverted before the row is finalized (see ProcessSongAsync).
    /// </summary>
    private static void ApplyIdentifierHints(SongMetadata song, EnrichmentProviderResult? result)
    {
        if (result is null)
            return;

        if (string.IsNullOrWhiteSpace(song.MusicBrainzId) && !string.IsNullOrWhiteSpace(result.MusicBrainzId))
            song.MusicBrainzId = result.MusicBrainzId;

        if (string.IsNullOrWhiteSpace(song.Isrc) && !string.IsNullOrWhiteSpace(result.Isrc))
            song.Isrc = result.Isrc;
    }

    private static void UpsertAttempt(
        SongMetadata song,
        EnrichmentProvider provider,
        ProviderAttemptStatus status,
        DateTime? retryAfterUtc = null,
        DateTime? nextRetryAfterUtc = null,
        string? matchedDataJson = null,
        string? error = null,
        string? searchQuery = null,
        Dictionary<EnrichmentProvider, SongProviderAttempt>? existingAttempts = null)
    {
        if (existingAttempts is not null && existingAttempts.TryGetValue(provider, out var existing))
        {
            existing.Status = status;
            existing.AttemptedAtUtc = DateTime.UtcNow;
            existing.RetryAfterUtc = retryAfterUtc;
            existing.NextRetryAfterUtc = nextRetryAfterUtc;
            existing.MatchedDataJson = matchedDataJson;
            existing.Error = error;
            existing.SearchQuery = searchQuery;
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
                NextRetryAfterUtc = nextRetryAfterUtc,
                MatchedDataJson = matchedDataJson,
                Error = error,
                SearchQuery = searchQuery,
            };
            song.ProviderAttempts.Add(attempt);
            existingAttempts?.TryAdd(provider, attempt);
        }
    }

    private DateTime? CooldownFor(ProviderAttemptStatus status)
    {
        var o = _options.Value;
        return status switch
        {
            ProviderAttemptStatus.NoMatch => o.EnrichmentNoMatchRetryDays > 0
                ? DateTime.UtcNow.AddDays(o.EnrichmentNoMatchRetryDays) : null,
            ProviderAttemptStatus.Failed => o.EnrichmentFailedRetryDays > 0
                ? DateTime.UtcNow.AddDays(o.EnrichmentFailedRetryDays) : null,
            _ => null,
        };
    }

    private IReadOnlyList<IEnrichmentProvider> GetEnabledProviders(EffectiveSettings effective)
    {
        return _providers
            .Where(p => IsProviderEnabled(p, effective))
            .OrderBy(p => p.Priority)
            .ToList();
    }

    private bool IsProviderEnabled(IEnrichmentProvider provider, EffectiveSettings effective)
    {
        return provider.Name switch
        {
            "AcoustID" => effective.EnableAcoustIdProvider,
            "MusicBrainzWeb" => effective.EnableMusicBrainzWebProvider,
            "SpotifyAPI" => effective.EnableSpotifyApiProvider,
            "Tracker" => effective.EnableTrackerProvider,
            "Deezer" => effective.EnableDeezerProvider,
            "AppleMusic" => effective.EnableAppleMusicProvider,
            // Config-only toggle (no runtime/DB setting): the yetracker is a static local catalog.
            "YeTracker" => _options.Value.EnableYeTrackerProvider,
            // No master toggle: the provider is gated by whether any user match-rule is enabled.
            "CustomRule" => true,
            _ => true
        };
    }

    internal static EnrichmentProvider? MapProviderName(string name) => name switch
    {
        "AcoustID" => EnrichmentProvider.AcoustID,
        "SpotifyAPI" => EnrichmentProvider.SpotifyAPI,
        "MusicBrainzWeb" => EnrichmentProvider.MusicBrainzWeb,
        "Tracker" => EnrichmentProvider.Tracker,
        "Deezer" => EnrichmentProvider.Deezer,
        "AppleMusic" => EnrichmentProvider.AppleMusic,
        "YeTracker" => EnrichmentProvider.YeTracker,
        "CustomRule" => EnrichmentProvider.CustomRule,
        _ => null
    };

    private void InitializeSemaphores(MusicEnricherOptions opts)
    {
        _providerSemaphores["AcoustID"] = new SemaphoreSlim(opts.AcoustIdConcurrency, opts.AcoustIdConcurrency);
        _providerSemaphores["SpotifyAPI"] = new SemaphoreSlim(opts.SpotifyApiConcurrency, opts.SpotifyApiConcurrency);
        _providerSemaphores["MusicBrainzWeb"] = new SemaphoreSlim(1, 1);
        _providerSemaphores["Tracker"] = new SemaphoreSlim(1, 1);
        _providerSemaphores["YeTracker"] = new SemaphoreSlim(1, 1);
        _providerSemaphores["CustomRule"] = new SemaphoreSlim(1, 1);
        _providerSemaphores["Deezer"] = new SemaphoreSlim(opts.DeezerConcurrency, opts.DeezerConcurrency);
        _providerSemaphores["AppleMusic"] = new SemaphoreSlim(opts.AppleMusicConcurrency, opts.AppleMusicConcurrency);
    }

    private SemaphoreSlim GetProviderSemaphore(string providerName) =>
        _providerSemaphores.GetOrAdd(providerName, _ => new SemaphoreSlim(1, 1));

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
