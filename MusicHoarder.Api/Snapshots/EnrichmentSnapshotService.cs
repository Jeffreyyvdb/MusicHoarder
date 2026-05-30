using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Snapshots;

/// <inheritdoc />
public class EnrichmentSnapshotService(
    MusicHoarderDbContext db,
    IOptionsMonitor<MusicEnricherOptions> enricherOptions,
    IOptionsMonitor<QualityGradingOptions> gradingOptions,
    ILogger<EnrichmentSnapshotService> logger) : IEnrichmentSnapshotService
{
    /// <summary>How many snapshots to retain per owner; older ones are pruned (cascade-deletes song rows).</summary>
    private const int RetentionCap = 50;

    private static readonly JsonSerializerOptions ConfigJsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public async Task<EnrichmentSnapshot?> CaptureAsync(
        Guid ownerId, SnapshotTrigger trigger, string? triggerLabel, CancellationToken ct = default)
    {
        var enr = enricherOptions.CurrentValue;
        var grading = gradingOptions.CurrentValue;

        var configJson = JsonSerializer.Serialize(BuildConfigFingerprint(enr, grading), ConfigJsonOptions);
        var configHash = Hash(configJson);

        // Hosted-service scopes have no current user, so query filters are off; filter explicitly by
        // owner (and ignore filters defensively) to keep the snapshot tenant-correct regardless.
        var active = db.Songs.IgnoreQueryFilters()
            .Where(s => s.DeletedAtUtc == null && s.OwnerUserId == ownerId);

        var statusCounts = await active
            .GroupBy(s => s.EnrichmentStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        int CountOf(EnrichmentStatus s) => statusCounts.FirstOrDefault(x => x.Status == s)?.Count ?? 0;

        var matched = CountOf(EnrichmentStatus.Matched);
        var review = CountOf(EnrichmentStatus.NeedsReview);
        var failed = CountOf(EnrichmentStatus.Failed);
        var pending = CountOf(EnrichmentStatus.Pending);
        var total = statusCounts.Sum(x => x.Count);

        var duplicateCount = await active.CountAsync(s => s.IsDuplicate, ct);
        var buildDoneCount = await active.CountAsync(s => s.LibraryBuildStatus == LibraryBuildStatus.Done, ct);

        var avgConfidence = await active
            .Where(s => s.MatchConfidence != null
                && (s.EnrichmentStatus == EnrichmentStatus.Matched || s.EnrichmentStatus == EnrichmentStatus.NeedsReview))
            .Select(s => s.MatchConfidence)
            .AverageAsync(ct);

        var providerMatched = await db.SongProviderAttempts.IgnoreQueryFilters()
            .Where(a => a.Status == ProviderAttemptStatus.Matched
                && a.Song.OwnerUserId == ownerId && a.Song.DeletedAtUtc == null)
            .GroupBy(a => a.Provider)
            .Select(g => new { Provider = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var providerMatchedJson = JsonSerializer.Serialize(
            providerMatched.ToDictionary(p => p.Provider.ToString(), p => p.Count), ConfigJsonOptions);

        // Latest grade per song for this owner, keyed for per-song lookup + the AI rollup. Reduced in
        // memory (GroupBy→First in a projection isn't translatable by every provider, incl. InMemory).
        var allGrades = await db.SongQualityGrades.IgnoreQueryFilters()
            .Where(g => g.OwnerUserId == ownerId)
            .Select(g => new { g.SongId, g.Score, g.Verdict, g.GradedAtUtc })
            .ToListAsync(ct);
        var gradeBySong = allGrades
            .GroupBy(g => g.SongId)
            .ToDictionary(
                grp => grp.Key,
                grp =>
                {
                    var latest = grp.OrderByDescending(x => x.GradedAtUtc).First();
                    return (latest.Score, latest.Verdict);
                });

        var songRows = await active
            .Select(s => new { s.Id, s.EnrichmentStatus, s.MatchConfidence, s.MatchedBy, s.IsDuplicate })
            .ToListAsync(ct);

        // AI rollup over songs that still exist and have a grade (Ungradeable excluded from the average).
        int aiExcellent = 0, aiGood = 0, aiQuestionable = 0, aiWrong = 0, aiUngradeable = 0;
        int graded = 0, scoreSum = 0, scoreCount = 0;
        var snapshotSongs = new List<EnrichmentSnapshotSong>(songRows.Count);
        foreach (var s in songRows)
        {
            int? aiScore = null;
            SongQualityVerdict? aiVerdict = null;
            if (gradeBySong.TryGetValue(s.Id, out var g))
            {
                graded++;
                aiScore = g.Score;
                aiVerdict = g.Verdict;
                switch (g.Verdict)
                {
                    case SongQualityVerdict.Excellent: aiExcellent++; break;
                    case SongQualityVerdict.Good: aiGood++; break;
                    case SongQualityVerdict.Questionable: aiQuestionable++; break;
                    case SongQualityVerdict.Wrong: aiWrong++; break;
                    default: aiUngradeable++; break;
                }
                if (g.Verdict != SongQualityVerdict.Ungradeable)
                {
                    scoreSum += g.Score;
                    scoreCount++;
                }
            }

            snapshotSongs.Add(new EnrichmentSnapshotSong
            {
                SongId = s.Id,
                EnrichmentStatus = s.EnrichmentStatus,
                MatchConfidence = s.MatchConfidence,
                MatchedBy = s.MatchedBy,
                IsDuplicate = s.IsDuplicate,
                AiScore = aiScore,
                AiVerdict = aiVerdict,
            });
        }

        var avgAiScore = scoreCount > 0 ? (double?)Math.Round((double)scoreSum / scoreCount, 2) : null;
        var roundedConfidence = avgConfidence is { } c ? Math.Round(c, 4) : (double?)null;

        // De-dup: skip when neither the config nor the headline metrics moved since the last snapshot.
        var prev = await db.EnrichmentSnapshots.IgnoreQueryFilters()
            .Where(e => e.OwnerUserId == ownerId)
            .OrderByDescending(e => e.CapturedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (prev is not null
            && prev.ConfigHash == configHash
            && prev.MatchedCount == matched && prev.NeedsReviewCount == review
            && prev.FailedCount == failed && prev.PendingCount == pending
            && prev.DuplicateCount == duplicateCount && prev.BuildDoneCount == buildDoneCount
            && prev.GradedCount == graded
            && NullableEquals(prev.AvgAiScore, avgAiScore)
            && NullableEquals(prev.AvgMatchConfidence, roundedConfidence))
        {
            logger.LogDebug(
                "Snapshot skipped for owner {Owner} ({Trigger}): unchanged since snapshot {PrevId}.",
                ownerId, trigger, prev.Id);
            return null;
        }

        var snapshot = new EnrichmentSnapshot
        {
            OwnerUserId = ownerId,
            CapturedAtUtc = DateTime.UtcNow,
            Trigger = trigger,
            TriggerLabel = Truncate(triggerLabel, 256),
            Version = Truncate(ResolveVersion(enr.PipelineVersion), 128),
            ConfigJson = configJson,
            ConfigHash = configHash,
            TotalSongs = total,
            MatchedCount = matched,
            NeedsReviewCount = review,
            FailedCount = failed,
            PendingCount = pending,
            DuplicateCount = duplicateCount,
            BuildDoneCount = buildDoneCount,
            AvgMatchConfidence = roundedConfidence,
            ProviderMatchedJson = providerMatchedJson,
            GradedCount = graded,
            AvgAiScore = avgAiScore,
            AiExcellent = aiExcellent,
            AiGood = aiGood,
            AiQuestionable = aiQuestionable,
            AiWrong = aiWrong,
            AiUngradeable = aiUngradeable,
            Songs = snapshotSongs,
        };

        db.EnrichmentSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);

        await PruneAsync(ownerId, ct);

        logger.LogInformation(
            "Captured enrichment snapshot {Id} for owner {Owner} ({Trigger}): {Matched} matched, {Review} review, {Failed} failed, avgAi={AvgAi}.",
            snapshot.Id, ownerId, trigger, matched, review, failed, avgAiScore);

        return snapshot;
    }

    private async Task PruneAsync(Guid ownerId, CancellationToken ct)
    {
        var stale = await db.EnrichmentSnapshots.IgnoreQueryFilters()
            .Where(e => e.OwnerUserId == ownerId)
            .OrderByDescending(e => e.CapturedAtUtc)
            .Skip(RetentionCap)
            .ToListAsync(ct);
        if (stale.Count == 0) return;

        db.EnrichmentSnapshots.RemoveRange(stale);
        await db.SaveChangesAsync(ct);
        logger.LogDebug("Pruned {Count} old snapshots for owner {Owner}.", stale.Count, ownerId);
    }

    /// <summary>
    /// The behavioral "version" of the pipeline: which providers vote, the consensus/matching knobs,
    /// and the AI grader's model + prompt version. Serialized + hashed to detect "what changed".
    /// </summary>
    private static object BuildConfigFingerprint(MusicEnricherOptions e, QualityGradingOptions q) => new
    {
        providers = new
        {
            acoustId = e.EnableAcoustIdProvider,
            musicBrainzWeb = e.EnableMusicBrainzWebProvider,
            spotifyApi = e.EnableSpotifyApiProvider,
            deezer = e.EnableDeezerProvider,
            appleMusic = e.EnableAppleMusicProvider,
            tracker = e.EnableTrackerProvider,
            yeTracker = e.EnableYeTrackerProvider,
        },
        consensus = new
        {
            corroborationFloor = e.ConsensusCorroborationFloor,
            autoUpgradeConfidence = e.AutoUpgradeConfidence,
            preferOriginalRelease = e.PreferOriginalRelease,
            albumAgreementBoost = e.AlbumAgreementConfidenceBoost,
            identityArtistThreshold = e.IdentityArtistThreshold,
            identityTitleThreshold = e.IdentityTitleThreshold,
            identityDurationDeltaSeconds = e.IdentityDurationDeltaSeconds,
        },
        spotify = new
        {
            minConfidence = e.SpotifyApiMinConfidence,
            matchedThreshold = e.SpotifyApiMatchedThreshold,
            isrcBoost = e.SpotifyApiIsrcConfidenceBoost,
            durationMismatchPenalty = e.SpotifyApiDurationMismatchPenalty,
            durationDeltaThresholdSeconds = e.SpotifyApiDurationDeltaThresholdSeconds,
        },
        deezer = new { minConfidence = e.DeezerApiMinConfidence, matchedThreshold = e.DeezerApiMatchedThreshold },
        appleMusic = new { minConfidence = e.AppleMusicApiMinConfidence, matchedThreshold = e.AppleMusicApiMatchedThreshold },
        musicBrainz = new { minConfidence = e.MusicBrainzMinConfidence, matchedThreshold = e.MusicBrainzMatchedThreshold },
        tracker = new { minConfidence = e.TrackerMinConfidence, matchedThreshold = e.TrackerMatchedThreshold },
        ai = new { enabled = q.Enabled, model = q.Model, promptVersion = q.PromptVersion },
    };

    internal static string ResolveVersion(string? overrideVersion)
    {
        if (!string.IsNullOrWhiteSpace(overrideVersion)) return overrideVersion.Trim();
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(info) ? "dev" : info;
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(bytes);
    }

    private static bool NullableEquals(double? a, double? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return Math.Abs(a.Value - b.Value) < 1e-9;
    }

    private static string? Truncate(string? value, int max) =>
        value is { Length: > 0 } && value.Length > max ? value[..max] : value;
}
