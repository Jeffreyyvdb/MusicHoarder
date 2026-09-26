using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Notifications;

/// <summary>The instance's VAPID identity: the key browsers subscribe with and the signer for requests.</summary>
public sealed record VapidIdentity(string PublicKey, ECDsa Signer, string Subject);

public interface IVapidKeyStore
{
    /// <summary>The key pair to use, or null when Web Push is switched off.</summary>
    Task<VapidIdentity?> GetAsync(CancellationToken ct);
}

/// <summary>
/// Resolves the VAPID key pair once per process: the configured pair when there is one, else the
/// pair stored in <see cref="WebPushKeys"/>, else a new one that is stored for next time.
///
/// <para>
/// A stored private key that no longer unprotects (the data-protection key ring was lost) cannot be
/// recovered, and every subscription was made against its public half, so they are all useless: the
/// store makes a new pair and deletes them. Browsers re-subscribe on their own the next time the app
/// opens, because the client compares the key it subscribed with to the one the API now hands out.
/// </para>
/// </summary>
public sealed class VapidKeyStore(
    IServiceScopeFactory scopes,
    IDataProtectionProvider dataProtection,
    IOptions<WebPushOptions> options,
    IOptions<FrontendOptions> frontend,
    IOptions<AuthOptions> auth,
    ILogger<VapidKeyStore> logger) : IVapidKeyStore, IDisposable
{
    private const string ProtectorPurpose = "MusicHoarder.WebPush.VapidPrivateKey";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private VapidIdentity? _identity;

    public async Task<VapidIdentity?> GetAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled) return null;
        if (_identity is { } ready) return ready;

        await _gate.WaitAsync(ct);
        try
        {
            if (_identity is { } raced) return raced;
            var (publicKey, signer) = await LoadAsync(ct);
            _identity = new VapidIdentity(publicKey, signer, ResolveSubject());
            return _identity;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(string PublicKey, ECDsa Signer)> LoadAsync(CancellationToken ct)
    {
        var configured = options.Value;
        if (!string.IsNullOrWhiteSpace(configured.PublicKey) && !string.IsNullOrWhiteSpace(configured.PrivateKey))
            return (configured.PublicKey.Trim(), WebPushCrypto.ImportVapidKey(configured.PublicKey.Trim(), configured.PrivateKey.Trim()));

        var protector = dataProtection.CreateProtector(ProtectorPurpose);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var stored = await db.WebPushKeys.OrderBy(k => k.Id).FirstOrDefaultAsync(ct);
        if (stored is not null)
        {
            try
            {
                var privateKey = protector.Unprotect(stored.ProtectedPrivateKey);
                return (stored.PublicKey, WebPushCrypto.ImportVapidKey(stored.PublicKey, privateKey));
            }
            catch (CryptographicException ex)
            {
                logger.LogWarning(ex,
                    "The stored Web Push key can no longer be read (was the data-protection key ring reset?). " +
                    "Generating a new one; every browser will re-subscribe the next time it opens the app.");
                db.WebPushKeys.Remove(stored);
                await db.WebPushSubscriptions.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
            }
        }

        var (publicKey, generatedPrivate) = WebPushCrypto.GenerateVapidKeys();
        db.WebPushKeys.Add(new WebPushKeys
        {
            PublicKey = publicKey,
            ProtectedPrivateKey = protector.Protect(generatedPrivate),
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Generated this instance's Web Push (VAPID) key pair");
        return (publicKey, WebPushCrypto.ImportVapidKey(publicKey, generatedPrivate));
    }

    private string ResolveSubject()
    {
        var configured = options.Value.Subject.Trim();
        if (configured.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || configured.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return configured;

        var origin = frontend.Value.PublicBaseUrl.Trim().TrimEnd('/');
        if (origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !origin.Contains("localhost", StringComparison.OrdinalIgnoreCase))
            return origin;

        var email = auth.Value.OwnerEmail.Trim();
        if (email.Contains('@'))
            return $"mailto:{email}";

        // Push services want some contact; this one says "none configured" without pretending.
        return "mailto:webpush@musichoarder.invalid";
    }

    public void Dispose()
    {
        _gate.Dispose();
        _identity?.Signer.Dispose();
    }
}
