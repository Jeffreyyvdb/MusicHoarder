using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Sharing;

/// <summary>
/// Who is on the other end of a share-link beacon, as far as the counter cares.
/// </summary>
/// <param name="UserId">The signed-in account, if the visitor has one on this instance.</param>
/// <param name="IpAddress">Used only to derive the day's visitor key; never stored.</param>
/// <param name="UserAgent">Used for bot filtering, the source and the visitor key; never stored.</param>
/// <param name="Referrer">The page's <c>document.referrer</c>, as the beacon reported it.</param>
public sealed record ShareVisitor(Guid? UserId, string? IpAddress, string? UserAgent, string? Referrer)
{
    /// <summary>
    /// Reads the visitor off a request. The IP is the first <c>X-Forwarded-For</c> hop when there is
    /// one: every request reaches the API through the frontend's proxy (and in production a reverse
    /// proxy in front of that), so the socket address is the same for everybody. The header is
    /// client-controlled, which is acceptable here and nowhere else — it only feeds a visitor count.
    /// </summary>
    public static ShareVisitor From(HttpContext http, CurrentUser? user, string? referrer)
    {
        var headers = http.Request.Headers;
        var forwarded = headers["X-Forwarded-For"].ToString();
        var ip = !string.IsNullOrWhiteSpace(forwarded)
            ? forwarded.Split(',')[0].Trim()
            : !string.IsNullOrWhiteSpace(headers["X-Real-IP"].ToString())
                ? headers["X-Real-IP"].ToString().Trim()
                : http.Connection.RemoteIpAddress?.ToString();
        return new ShareVisitor(user?.Id, ip, headers.UserAgent.ToString(), referrer);
    }
}

/// <summary>
/// Counts opens of and plays from a share link — the numbers behind the owner's Share links page.
///
/// <para>
/// What it will not count: the link's owner (opening your own link to check it is not a visitor),
/// anything that looks like a bot, and a repeat of the same thing by the same visitor within
/// <see cref="DedupeWindow"/> (a reload, a replay). "The same visitor" is a
/// <see cref="ShareVisit.VisitorKey"/>: an HMAC of (share, IP, user agent) under a random salt
/// that lives only in memory and is replaced every UTC day, so no address is stored and yesterday's
/// keys cannot be matched to today's. The price of never persisting the salt is that an API
/// restart starts a fresh one: a visitor who comes back later that same day counts once more.
/// </para>
///
/// <para>
/// The beacon endpoints are anonymous and the links are public by design — they get posted in
/// comment sections — so a per-share rate cap bounds how fast forged beacons can grow the table.
/// Over the cap, visits are dropped rather than queued: an undercount during a real spike beats an
/// unbounded write path.
/// </para>
/// </summary>
public sealed class ShareVisitTracker : IDisposable
{
    public static readonly TimeSpan DedupeWindow = TimeSpan.FromMinutes(30);

    /// <summary>Recorded visits per share per minute before the rest are dropped.</summary>
    public const int PerShareMinuteCap = 120;

    private readonly TimeProvider _time;
    private readonly PartitionedRateLimiter<int> _limiter;
    private readonly Lock _saltLock = new();
    private DateOnly _saltDay;
    private byte[] _salt = [];

    public ShareVisitTracker()
        : this(TimeProvider.System, PerShareMinuteCap)
    {
    }

    internal ShareVisitTracker(TimeProvider time, int perShareMinuteCap)
    {
        _time = time;
        _limiter = PartitionedRateLimiter.Create<int, int>(shareId =>
            RateLimitPartition.GetFixedWindowLimiter(shareId, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = perShareMinuteCap,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    }

    /// <summary>
    /// Records one visit unless it is the owner's own, a bot's, a repeat, or over the cap. Returns
    /// whether a row was written. <paramref name="share"/> must already be resolved and active.
    /// </summary>
    public async Task<bool> TryRecordAsync(
        MusicHoarderDbContext db,
        SongShare share,
        ShareVisitKind kind,
        int? songId,
        ShareVisitor visitor,
        CancellationToken ct)
    {
        if (visitor.UserId == share.OwnerUserId)
            return false;
        if (ShareVisitSource.IsLikelyBot(visitor.UserAgent))
            return false;

        var now = _time.GetUtcNow().UtcDateTime;
        var key = VisitorKeyFor(share.Id, visitor, now);
        var since = now - DedupeWindow;

        // Unfiltered: a signed-in visitor's tenancy filter would hide the owner's rows and let every
        // reload through.
        var repeat = await db.ShareVisits.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(v => v.ShareId == share.Id
                && v.Kind == kind
                && v.SongId == songId
                && v.VisitorKey == key
                && v.OccurredAtUtc >= since, ct);
        if (repeat)
            return false;

        using var lease = _limiter.AttemptAcquire(share.Id);
        if (!lease.IsAcquired)
            return false;

        db.ShareVisits.Add(new ShareVisit
        {
            ShareId = share.Id,
            OwnerUserId = share.OwnerUserId,
            Kind = kind,
            SongId = kind == ShareVisitKind.Play ? songId : null,
            OccurredAtUtc = now,
            VisitorKey = key,
            Source = ShareVisitSource.Classify(visitor.Referrer, visitor.UserAgent),
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    internal string VisitorKeyFor(int shareId, ShareVisitor visitor, DateTime nowUtc)
    {
        var salt = SaltFor(DateOnly.FromDateTime(nowUtc));
        var input = Encoding.UTF8.GetBytes($"{shareId}\n{visitor.IpAddress}\n{visitor.UserAgent}");
        var hash = HMACSHA256.HashData(salt, input);
        return Convert.ToHexStringLower(hash.AsSpan(0, 12));
    }

    private byte[] SaltFor(DateOnly day)
    {
        lock (_saltLock)
        {
            // Forward only: a request stamped a moment before midnight that takes the lock after
            // one stamped just after it must not roll the new day's salt back.
            if (day > _saltDay || _salt.Length == 0)
            {
                _saltDay = day;
                _salt = RandomNumberGenerator.GetBytes(32);
            }
            return _salt;
        }
    }

    public void Dispose() => _limiter.Dispose();
}
