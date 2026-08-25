using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Auth;

public sealed class AuthService : IAuthService
{
    private static readonly TimeSpan SlidingExtensionThreshold = TimeSpan.FromDays(7);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMagicLinkSender _sender;
    private readonly IHostEnvironment _hostEnv;
    private readonly IOptionsMonitor<AuthOptions> _options;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IServiceScopeFactory scopeFactory,
        IMagicLinkSender sender,
        IHostEnvironment hostEnv,
        IOptionsMonitor<AuthOptions> options,
        ILogger<AuthService> logger)
    {
        _scopeFactory = scopeFactory;
        _sender = sender;
        _hostEnv = hostEnv;
        _options = options;
        _logger = logger;
    }

    public async Task<RequestLinkResult?> RequestLinkAsync(
        string email,
        string frontendBaseUrl,
        string? ip,
        string? userAgent,
        CancellationToken ct)
    {
        var normalized = User.Normalize(email);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var user = await db.Users
            .FirstOrDefaultAsync(u => u.EmailNormalized == normalized && !u.IsDisabled, ct)
            .ConfigureAwait(false);
        if (user is null)
        {
            _logger.LogInformation("Magic-link requested for unknown/disabled email (suppressed).");
            return null;
        }

        // Revoke any prior unconsumed tokens for this user.
        var nowUtc = DateTime.UtcNow;
        var prior = await db.MagicLinkTokens
            .Where(t => t.UserId == user.Id && t.ConsumedAtUtc == null && t.ExpiresAtUtc > nowUtc)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var t in prior)
            t.ConsumedAtUtc = nowUtc; // mark consumed so they can't be used; not a real consume.

        var rawToken = GenerateRawToken();
        var token = new MagicLinkToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = Sha256(rawToken),
            IssuedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc.AddMinutes(_options.CurrentValue.MagicLinkTtlMinutes),
            RequestedFromIp = ip,
            RequestedUserAgent = userAgent,
        };
        db.MagicLinkTokens.Add(token);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var callbackUrl = BuildCallbackUrl(frontendBaseUrl, rawToken);

        try
        {
            await _sender.SendAsync(user, callbackUrl, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send magic link for {Email}", user.Email);
            throw;
        }

        // In Development, return the dev URL so devs can click without checking email.
        var devUrl = _hostEnv.IsDevelopment() && _sender.IsConsoleFallback ? callbackUrl : null;
        return new RequestLinkResult(devUrl);
    }

    public async Task<Session?> ConsumeLinkAsync(
        string rawToken,
        string? ip,
        string? userAgent,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;

        var hash = Sha256(rawToken);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var nowUtc = DateTime.UtcNow;

        var token = await db.MagicLinkTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
            .ConfigureAwait(false);

        if (token is null || !token.IsConsumable(nowUtc) || token.User is null || token.User.IsDisabled)
            return null;

        token.ConsumedAtUtc = nowUtc;
        token.User.LastLoginAtUtc = nowUtc;
        var session = CreateSession(token.UserId, ip, userAgent, nowUtc);
        db.Sessions.Add(session);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return session;
    }

    public async Task<Session?> StartDemoSessionAsync(string? ip, string? userAgent, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var nowUtc = DateTime.UtcNow;

        var demo = await db.Users
            .FirstOrDefaultAsync(u => u.Id == WellKnownUsers.DemoId && !u.IsDisabled, ct)
            .ConfigureAwait(false);
        if (demo is null) return null;

        demo.LastLoginAtUtc = nowUtc;
        var session = CreateSession(demo.Id, ip, userAgent, nowUtc);
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return session;
    }

    public async Task<Session?> CreateDeviceSessionAsync(Guid userId, string? ip, string? userAgent, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var nowUtc = DateTime.UtcNow;

        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDisabled, ct)
            .ConfigureAwait(false);
        if (user is null) return null;

        var session = CreateSession(userId, ip, userAgent, nowUtc);
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return session;
    }

    public async Task<(Session Session, User User)?> ResolveSessionAsync(Guid sessionId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var nowUtc = DateTime.UtcNow;

        var session = await db.Sessions
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            .ConfigureAwait(false);

        if (session is null || session.User is null || session.User.IsDisabled) return null;
        if (!session.IsActive(nowUtc)) return null;

        // Sliding lifetime: only write when the remaining lifetime is below the threshold to avoid
        // a write per request.
        var remaining = session.ExpiresAtUtc - nowUtc;
        if (remaining < SlidingExtensionThreshold)
        {
            session.ExpiresAtUtc = nowUtc.AddDays(_options.CurrentValue.SessionLifetimeDays);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return (session, session.User);
    }

    public async Task<IReadOnlyList<(Session Session, User User)>> ResolveSessionsAsync(
        IReadOnlyCollection<Guid> sessionIds,
        CancellationToken ct)
    {
        if (sessionIds.Count == 0) return [];

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var nowUtc = DateTime.UtcNow;

        var sessions = await db.Sessions.AsNoTracking()
            .Include(s => s.User)
            .Where(s => sessionIds.Contains(s.Id))
            .ToListAsync(ct).ConfigureAwait(false);

        var byId = sessions
            .Where(s => s.User is not null && !s.User.IsDisabled && s.IsActive(nowUtc))
            .ToDictionary(s => s.Id);

        var result = new List<(Session, User)>(byId.Count);
        foreach (var id in sessionIds)
        {
            if (byId.TryGetValue(id, out var s))
                result.Add((s, s.User!));
        }
        return result;
    }

    public async Task RevokeAsync(Guid sessionId, bool allForUser, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var nowUtc = DateTime.UtcNow;

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct).ConfigureAwait(false);
        if (session is null) return;

        if (allForUser)
        {
            var all = await db.Sessions
                .Where(s => s.UserId == session.UserId && s.RevokedAtUtc == null)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var s in all) s.RevokedAtUtc = nowUtc;
        }
        else
        {
            session.RevokedAtUtc = nowUtc;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<InviteMintResult?> CreateOrRotateInviteAsync(Guid ownerUserId, string email, CancellationToken ct)
    {
        var normalized = User.Normalize(email);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var nowUtc = DateTime.UtcNow;

        // The email must not belong to an existing non-Friend account: the owner can't invite
        // themselves or the demo. A disabled Friend is fine — acceptance re-enables them.
        var existingUser = await db.Users
            .FirstOrDefaultAsync(u => u.EmailNormalized == normalized, ct)
            .ConfigureAwait(false);
        if (existingUser is not null && existingUser.Role != UserRole.Friend)
            return null;

        var rawToken = GenerateRawToken();

        // Explicit CreatedByUserId predicate (not just the ambient query filter): this method
        // may run in a background scope where the filter is off.
        var invite = await db.Invites.IgnoreQueryFilters()
            .Where(i => i.CreatedByUserId == ownerUserId
                && i.EmailNormalized == normalized
                && i.ConsumedAtUtc == null
                && i.RevokedAtUtc == null
                && i.ExpiresAtUtc > nowUtc)
            .OrderByDescending(i => i.CreatedAtUtc)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (invite is null)
        {
            invite = new Invite
            {
                Id = Guid.NewGuid(),
                CreatedByUserId = ownerUserId,
                Email = email.Trim(),
                EmailNormalized = normalized,
                CreatedAtUtc = nowUtc,
            };
            db.Invites.Add(invite);
        }

        // Rotate-in-place for an existing active invite: the old link dies, the row (and the
        // owner's mental model of "one pending invite per person") stays.
        invite.TokenHash = Sha256(rawToken);
        invite.CreatedAtUtc = nowUtc;
        invite.ExpiresAtUtc = nowUtc.AddHours(_options.CurrentValue.InviteTtlHours);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new InviteMintResult(invite, rawToken);
    }

    public async Task<InvitePeekResult?> PeekInviteAsync(string rawToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;

        var hash = Sha256(rawToken);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var nowUtc = DateTime.UtcNow;

        // IgnoreQueryFilters: the clicker is anonymous — or worse, carries a stale demo/friend
        // cookie whose Invites filter (CreatedByUserId == theirs) would hide the row.
        var invite = await db.Invites.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(i => i.TokenHash == hash, ct)
            .ConfigureAwait(false);
        if (invite is null || !invite.IsConsumable(nowUtc)) return null;

        var inviter = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == invite.CreatedByUserId, ct)
            .ConfigureAwait(false);

        return new InvitePeekResult(inviter?.DisplayName ?? "The owner", invite.Email);
    }

    public async Task<Session?> AcceptInviteAsync(string rawToken, string? ip, string? userAgent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;
        var hash = Sha256(rawToken);

        // Two attempts: if a concurrent accept for the same email wins the unique-EmailNormalized
        // race, the retry (in a fresh scope, so no poisoned change tracker) finds the existing
        // user and proceeds down the reuse path. The invite's ConsumedAtUtc guards double-consume.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
            var nowUtc = DateTime.UtcNow;

            var invite = await db.Invites.IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.TokenHash == hash, ct)
                .ConfigureAwait(false);
            if (invite is null || !invite.IsConsumable(nowUtc)) return null;

            var user = await db.Users
                .FirstOrDefaultAsync(u => u.EmailNormalized == invite.EmailNormalized, ct)
                .ConfigureAwait(false);

            if (user is null)
            {
                user = new User
                {
                    Id = Guid.NewGuid(),
                    Email = invite.Email,
                    EmailNormalized = invite.EmailNormalized,
                    Role = UserRole.Friend,
                    CreatedAtUtc = nowUtc,
                };
                db.Users.Add(user);
            }
            else if (user.Role == UserRole.Friend)
            {
                // Owner-authorized re-entry: a removed ("disabled") friend accepting a fresh
                // invite comes back, instead of dead-ending at a disabled account.
                user.IsDisabled = false;
            }
            else
            {
                // The email meanwhile belongs to the owner/demo (CreateOrRotate already rejects
                // this; here we guard the race). Uniform failure.
                return null;
            }

            invite.ConsumedAtUtc = nowUtc;
            invite.ConsumedByUserId = user.Id;
            user.LastLoginAtUtc = nowUtc;
            var session = CreateSession(user.Id, ip, userAgent, nowUtc);
            db.Sessions.Add(session);

            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return session;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                _logger.LogInformation("Invite accept hit a concurrent user insert; retrying once.");
            }
        }
        return null;
    }

    private Session CreateSession(Guid userId, string? ip, string? userAgent, DateTime nowUtc) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            IssuedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc.AddDays(_options.CurrentValue.SessionLifetimeDays),
            IpAddress = ip,
            UserAgent = userAgent,
        };

    private static string BuildCallbackUrl(string frontendBaseUrl, string rawToken)
    {
        var b = frontendBaseUrl.TrimEnd('/');
        return $"{b}/auth/callback?token={Uri.EscapeDataString(rawToken)}";
    }

    internal static string GenerateRawToken()
    {
        // 32 bytes of entropy → 43-char base64-url string. URL-safe characters only.
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    internal static byte[] Sha256(string value) =>
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
}
