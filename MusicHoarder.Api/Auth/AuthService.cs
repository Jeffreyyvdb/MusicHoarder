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
            _logger.LogError(ex, "Failed to send magic link for user {UserId}", user.Id);
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
