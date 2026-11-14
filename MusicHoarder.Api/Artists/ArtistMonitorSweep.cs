using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Artists;

/// <summary>What one pass of the artist monitor did; <see cref="IdleReason"/> says why it did nothing.</summary>
public sealed record ArtistMonitorResult(
    int ArtistsChecked,
    int NewReleases,
    int TracksQueued,
    string? IdleReason = null)
{
    public const string IdleDisabled = "disabled";
    public const string IdleNothingDue = "nothing-due";

    public static ArtistMonitorResult Idle(string reason) => new(0, 0, 0, reason);
}

/// <summary>A release the monitor found, for the notification it sends.</summary>
public sealed record NewReleaseNotice(Guid OwnerUserId, string ArtistName, string Title, ArtistReleaseKinds Kind, int TracksQueued, bool Queued);

/// <summary>
/// Looks for new releases of followed artists. Each check reads the artist's catalog listing, folds it
/// into releases (<see cref="ArtistDiscography.Group"/>) and decides every release it has not seen:
/// <list type="bullet">
/// <item>On the first check everything listed is the <see cref="FollowedReleaseDecision.Baseline"/> —
/// following an artist never downloads their back catalog.</item>
/// <item>A release of a kind the follow does not watch is left undecided, so turning that kind on later
/// still catches a release that is new by then.</item>
/// <item>A release dated in the future waits until it is out.</item>
/// <item>A release dated well before the follow is a catalog back-fill, not news
/// (<see cref="FollowedReleaseDecision.SkippedOld"/>).</item>
/// <item>Anything else is queued through <see cref="ArtistReleaseQueuer"/> (or only noticed, when the
/// follow's auto-download or the downloader is off), and a notification goes out.</item>
/// </list>
/// A release with a decision is never decided again, except a <see cref="FollowedReleaseDecision.Failed"/>
/// one, which the next check retries.
/// </summary>
public sealed class ArtistMonitorSweep(
    MusicHoarderDbContext db,
    IDeezerCatalogService deezer,
    ArtistReleaseQueuer queuer,
    IArtistReleaseNotifier notifier,
    IOptions<MusicEnricherOptions> options,
    ILogger<ArtistMonitorSweep> logger)
{
    public async Task<ArtistMonitorResult> SweepAsync(CancellationToken ct)
    {
        var opts = options.Value;
        if (!opts.EnableArtistMonitor)
            return ArtistMonitorResult.Idle(ArtistMonitorResult.IdleDisabled);

        var now = DateTime.UtcNow;
        var due = await db.FollowedArtists
            .IgnoreQueryFilters()
            .Where(f => f.OwnerUserId != WellKnownUsers.DemoId)
            .Where(f => f.NextCheckAtUtc == null || f.NextCheckAtUtc <= now)
            .OrderBy(f => f.NextCheckAtUtc)
            .ThenBy(f => f.Id)
            .Take(Math.Clamp(opts.ArtistMonitorArtistsPerSweep, 1, 500))
            .Select(f => f.Id)
            .ToListAsync(ct);

        if (due.Count == 0)
            return ArtistMonitorResult.Idle(ArtistMonitorResult.IdleNothingDue);

        int releases = 0, tracks = 0;
        foreach (var id in due)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await CheckAsync(id, now, ct);
            releases += outcome.NewReleases;
            tracks += outcome.TracksQueued;
        }

        return new ArtistMonitorResult(due.Count, releases, tracks);
    }

    /// <summary>Checks one followed artist now, whatever its timer says.</summary>
    public async Task<ArtistMonitorResult> CheckAsync(int followedArtistId, DateTime now, CancellationToken ct)
    {
        var opts = options.Value;
        var follow = await db.FollowedArtists
            .IgnoreQueryFilters()
            .Include(f => f.Releases)
            .FirstOrDefaultAsync(f => f.Id == followedArtistId, ct);
        if (follow is null) return ArtistMonitorResult.Idle(ArtistMonitorResult.IdleNothingDue);

        var listing = await deezer.GetArtistReleasesAsync(follow.DeezerArtistId, ct);
        if (listing is null)
        {
            // Unreadable is not "no releases": nothing is decided, and the check comes round again soon.
            follow.LastError = "Could not read the artist's releases from the catalog.";
            follow.NextCheckAtUtc = now.AddHours(1);
            await db.SaveChangesAsync(ct);
            return new ArtistMonitorResult(1, 0, 0);
        }

        var isBaseline = follow.LastCheckedAtUtc is null;
        var decided = follow.Releases.ToDictionary(r => r.ReleaseKey, StringComparer.Ordinal);
        var today = DateOnly.FromDateTime(now);
        var oldestNews = DateOnly.FromDateTime(follow.FollowedAtUtc).AddDays(-Math.Max(0, opts.ArtistMonitorBackCatalogGraceDays));
        var downloadsOn = opts.EnableWishlistDownloads;
        var notices = new List<NewReleaseNotice>();
        int newReleases = 0, queuedTracks = 0;
        var atCeiling = false;

        // Oldest first, so a burst of releases is queued and announced in the order it came out.
        foreach (var release in ArtistDiscography.Group(listing).Reverse())
        {
            decided.TryGetValue(release.Key, out var row);
            if (row is not null && row.Decision != FollowedReleaseDecision.Failed) continue;

            if (isBaseline)
            {
                Record(follow, release, FollowedReleaseDecision.Baseline, 0, null, now);
                continue;
            }

            if (!follow.ReleaseKinds.HasFlag(release.Kind)) continue;
            if (release.ReleaseDate is { } date && date > today) continue;

            if (release.ReleaseDate is { } dated && dated < oldestNews)
            {
                Record(follow, release, FollowedReleaseDecision.SkippedOld, 0, null, now, row);
                continue;
            }

            if (!follow.AutoDownload || !downloadsOn)
            {
                Record(follow, release, FollowedReleaseDecision.Noticed, 0, null, now, row);
                notices.Add(new NewReleaseNotice(follow.OwnerUserId, follow.Name, release.Title, release.Kind, 0, false));
                newReleases++;
                continue;
            }

            if (atCeiling || await AtPendingCeilingAsync(follow.OwnerUserId, ct))
            {
                // Left undecided: the downloader is behind, and the next check will find it still new.
                atCeiling = true;
                continue;
            }

            var result = await queuer.QueueAsync(
                follow.OwnerUserId, follow.Name, release.EditionIds, release.Kind,
                WishlistItemOrigin.ArtistMonitor,
                trackBudget: Math.Clamp(opts.ArtistMonitorMaxTracksPerRelease, 1, 500),
                requeueFailed: false, ct);

            if (result.Error is not null)
            {
                Record(follow, release, FollowedReleaseDecision.Failed, 0, null, now, row);
                continue;
            }

            var decision = result.Queued > 0 ? FollowedReleaseDecision.Queued : FollowedReleaseDecision.AlreadyOwned;
            Record(follow, release, decision, result.Queued, result.CanonicalAlbumId, now, row);
            if (result.Queued > 0)
                notices.Add(new NewReleaseNotice(follow.OwnerUserId, follow.Name, release.Title, release.Kind, result.Queued, true));
            newReleases++;
            queuedTracks += result.Queued;
        }

        follow.LastCheckedAtUtc = now;
        follow.LastError = null;
        follow.NextCheckAtUtc = now.AddHours(Math.Clamp(opts.ArtistMonitorCheckIntervalHours, 1, 24 * 30));
        await db.SaveChangesAsync(ct);

        if (newReleases > 0)
            logger.LogInformation("Artist monitor: {Count} new release(s) from {Artist}, {Tracks} track(s) queued",
                newReleases, follow.Name, queuedTracks);

        foreach (var notice in notices)
        {
            try
            {
                await notifier.NotifyAsync(notice, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not send the new-release notification for {Artist}", follow.Name);
            }
        }

        return new ArtistMonitorResult(1, newReleases, queuedTracks);
    }

    private async Task<bool> AtPendingCeilingAsync(Guid ownerId, CancellationToken ct)
    {
        var max = options.Value.ArtistMonitorMaxPendingItems;
        if (max <= 0) return false;
        var pending = await db.WishlistItems
            .IgnoreQueryFilters()
            .CountAsync(w => w.OwnerUserId == ownerId
                && w.Origin == WishlistItemOrigin.ArtistMonitor
                && w.Status == WishlistItemStatus.Pending, ct);
        return pending >= max;
    }

    private void Record(
        FollowedArtist follow, DiscographyRelease release, FollowedReleaseDecision decision,
        int tracksQueued, int? canonicalAlbumId, DateTime now, FollowedArtistRelease? existing = null)
    {
        var row = existing;
        if (row is null)
        {
            row = new FollowedArtistRelease { ReleaseKey = Truncate(release.Key, 600) };
            follow.Releases.Add(row);
        }

        row.DeezerAlbumId = release.PrimaryId;
        row.Title = Truncate(release.Title, 512);
        row.Kind = release.Kind;
        row.ReleaseDate = release.ReleaseDate;
        row.CoverUrl = release.CoverUrl is null ? null : Truncate(release.CoverUrl, 1024);
        row.Decision = decision;
        row.TracksQueued = tracksQueued;
        row.CanonicalAlbumId = canonicalAlbumId;
        row.DecidedAtUtc = now;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
