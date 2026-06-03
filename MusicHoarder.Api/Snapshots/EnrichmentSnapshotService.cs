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
        var version = Truncate(ResolveVersion(enr.PipelineVersion), 128);

        // Assigns the freshly-computed aggregates onto a snapshot row (shared by the insert + update paths).
        void ApplyMetrics(EnrichmentSnapshot target)
        {
            target.ConfigJson = configJson;
            target.ConfigHash = configHash;
            target.TotalSongs = total;
            target.MatchedCount = matched;
            target.NeedsReviewCount = review;
            target.FailedCount = failed;
            target.PendingCount = pending;
            target.DuplicateCount = duplicateCount;
            target.BuildDoneCount = buildDoneCount;
            target.AvgMatchConfidence = roundedConfidence;
            target.ProviderMatchedJson = providerMatchedJson;
            target.GradedCount = graded;
            target.AvgAiScore = avgAiScore;
            target.AiExcellent = aiExcellent;
            target.AiGood = aiGood;
            target.AiQuestionable = aiQuestionable;
            target.AiWrong = aiWrong;
            target.AiUngradeable = aiUngradeable;
        }

        // One timeline point per behavioral version: a row is keyed by (owner, Version, ConfigHash).
        // A new run on the same version updates that point in place instead of spawning a new one, so
        // repeated enrich/grade passes don't flood the timeline. Hosted-service scopes have no current
        // user, so filter explicitly by owner and ignore query filters.
        var matches = await db.EnrichmentSnapshots.IgnoreQueryFilters()
            .Where(e => e.OwnerUserId == ownerId && e.Version == version && e.ConfigHash == configHash)
            .OrderByDescending(e => e.CapturedAtUtc)
            .ThenByDescending(e => e.Id)
            .ToListAsync(ct);

        var keep = matches.FirstOrDefault();

        // Collapse any historical duplicates for this version into the newest one (cleans up rows from
        // before this de-dup existed). Delete child song rows explicitly — Npgsql cascades, but the
        // in-memory provider used by tests does not cascade unloaded children.
        if (matches.Count > 1)
        {
            var extraIds = matches.Skip(1).Select(e => e.Id).ToList();
            var extraSongs = await db.EnrichmentSnapshotSongs
                .Where(s => extraIds.Contains(s.SnapshotId))
                .ToListAsync(ct);
            db.EnrichmentSnapshotSongs.RemoveRange(extraSongs);
            db.EnrichmentSnapshots.RemoveRange(matches.Skip(1));
        }

        if (keep is not null)
        {
            // De-dup: skip when nothing measurable moved (don't drift the timestamp on idle runs).
            if (keep.MatchedCount == matched && keep.NeedsReviewCount == review
                && keep.FailedCount == failed && keep.PendingCount == pending
                && keep.DuplicateCount == duplicateCount && keep.BuildDoneCount == buildDoneCount
                && keep.GradedCount == graded
                && NullableEquals(keep.AvgAiScore, avgAiScore)
                && NullableEquals(keep.AvgMatchConfidence, roundedConfidence))
            {
                if (matches.Count > 1) await db.SaveChangesAsync(ct); // persist duplicate cleanup
                logger.LogDebug(
                    "Snapshot skipped for owner {Owner} ({Trigger}): unchanged since snapshot {KeepId}.",
                    ownerId, trigger, keep.Id);
                return null;
            }

            // Same version, new measurements → refresh the existing point in place.
            ApplyMetrics(keep);
            keep.CapturedAtUtc = DateTime.UtcNow;
            keep.Trigger = trigger;
            keep.TriggerLabel = Truncate(triggerLabel, 256);

            var oldSongs = await db.EnrichmentSnapshotSongs
                .Where(s => s.SnapshotId == keep.Id)
                .ToListAsync(ct);
            db.EnrichmentSnapshotSongs.RemoveRange(oldSongs);
            foreach (var s in snapshotSongs) s.SnapshotId = keep.Id;
            db.EnrichmentSnapshotSongs.AddRange(snapshotSongs);

            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Updated enrichment snapshot {Id} for owner {Owner} ({Trigger}): {Matched} matched, {Review} review, {Failed} failed, avgAi={AvgAi}.",
                keep.Id, ownerId, trigger, matched, review, failed, avgAiScore);

            return keep;
        }

        // New behavioral version → a fresh timeline point.
        var snapshot = new EnrichmentSnapshot
        {
            OwnerUserId = ownerId,
            CapturedAtUtc = DateTime.UtcNow,
            Trigger = trigger,
            TriggerLabel = Truncate(triggerLabel, 256),
            Version = version,
            Songs = snapshotSongs,
        };
        ApplyMetrics(snapshot);

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
        if (!string.IsNullOrWhiteSpace(overrideVersion)) return Clean(overrideVersion);
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(info) ? "dev" : Clean(info);

        // Drop any "+<build-metadata>" suffix (e.g. the source-control SHA the .NET SDK appends to
        // local/dev builds) so the timeline and /api/version always show a clean semver.
        static string Clean(string value)
        {
            var v = value.Trim();
            var plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
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
