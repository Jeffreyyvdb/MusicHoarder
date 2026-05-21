using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Auth;

/// <summary>
/// Drives the two WebAuthn ceremonies (registration + authentication) on top of
/// <see cref="IFido2"/> and persists registered passkeys as <see cref="WebAuthnCredential"/>.
/// A successful authentication mints the same server-side <see cref="Session"/> the magic-link
/// flow does, so everything downstream (cookie, middleware) is unchanged.
/// </summary>
public interface IWebAuthnService
{
    Task<CredentialCreateOptions?> BeginRegistrationAsync(Guid userId, CancellationToken ct);

    Task<WebAuthnCredentialView> CompleteRegistrationAsync(
        Guid userId,
        AuthenticatorAttestationRawResponse attestation,
        CredentialCreateOptions originalOptions,
        string displayName,
        CancellationToken ct);

    AssertionOptions BeginAuthentication();

    Task<Session?> CompleteAuthenticationAsync(
        AuthenticatorAssertionRawResponse assertion,
        AssertionOptions originalOptions,
        string? ip,
        string? userAgent,
        CancellationToken ct);

    Task<IReadOnlyList<WebAuthnCredentialView>> ListCredentialsAsync(Guid userId, CancellationToken ct);

    Task<bool> DeleteCredentialAsync(Guid userId, Guid credentialId, CancellationToken ct);
}

public sealed record WebAuthnCredentialView(
    Guid Id,
    string DisplayName,
    Guid AaGuid,
    DateTime CreatedAtUtc,
    DateTime? LastUsedAtUtc);

public sealed class WebAuthnService : IWebAuthnService
{
    private static readonly IReadOnlyList<PubKeyCredParam> SupportedAlgorithms =
    [
        PubKeyCredParam.Ed25519,
        PubKeyCredParam.ES256,
        PubKeyCredParam.RS256,
        PubKeyCredParam.ES384,
        PubKeyCredParam.RS384,
        PubKeyCredParam.ES512,
        PubKeyCredParam.RS512,
        PubKeyCredParam.PS256,
        PubKeyCredParam.PS384,
        PubKeyCredParam.PS512,
    ];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFido2 _fido2;
    private readonly IOptionsMonitor<AuthOptions> _authOptions;
    private readonly ILogger<WebAuthnService> _logger;

    public WebAuthnService(
        IServiceScopeFactory scopeFactory,
        IFido2 fido2,
        IOptionsMonitor<AuthOptions> authOptions,
        ILogger<WebAuthnService> logger)
    {
        _scopeFactory = scopeFactory;
        _fido2 = fido2;
        _authOptions = authOptions;
        _logger = logger;
    }

    public async Task<CredentialCreateOptions?> BeginRegistrationAsync(Guid userId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && !u.IsDisabled, ct)
            .ConfigureAwait(false);
        if (user is null) return null;

        var existing = await db.WebAuthnCredentials
            .Where(c => c.UserId == userId)
            .Select(c => c.CredentialId)
            .ToListAsync(ct).ConfigureAwait(false);

        var fido2User = new Fido2User
        {
            Id = userId.ToByteArray(),
            Name = user.Email,
            DisplayName = user.DisplayName ?? user.Email,
        };

        return _fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = fido2User,
            ExcludeCredentials = existing
                .Select(id => new PublicKeyCredentialDescriptor(id))
                .ToList(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Preferred,
            },
            AttestationPreference = AttestationConveyancePreference.None,
            PubKeyCredParams = SupportedAlgorithms,
        });
    }

    public async Task<WebAuthnCredentialView> CompleteRegistrationAsync(
        Guid userId,
        AuthenticatorAttestationRawResponse attestation,
        CredentialCreateOptions originalOptions,
        string displayName,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var result = await _fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
        {
            AttestationResponse = attestation,
            OriginalOptions = originalOptions,
            IsCredentialIdUniqueToUserCallback = async (args, token) =>
            {
                var exists = await db.WebAuthnCredentials
                    .AnyAsync(c => c.CredentialId == args.CredentialId, token)
                    .ConfigureAwait(false);
                return !exists;
            },
        }, ct).ConfigureAwait(false);

        var nowUtc = DateTime.UtcNow;
        var credential = new WebAuthnCredential
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CredentialId = result.Id,
            PublicKey = result.PublicKey,
            SignCount = result.SignCount,
            AaGuid = result.AaGuid,
            Transports = result.Transports is { Length: > 0 }
                ? string.Join(',', result.Transports)
                : null,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Passkey" : displayName.Trim(),
            CreatedAtUtc = nowUtc,
        };
        db.WebAuthnCredentials.Add(credential);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return ToView(credential);
    }

    public AssertionOptions BeginAuthentication() =>
        // Empty allow-list ⇒ discoverable-credential (usernameless) flow: the authenticator
        // surfaces the resident key and reports the user handle in the assertion.
        _fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Preferred,
        });

    public async Task<Session?> CompleteAuthenticationAsync(
        AuthenticatorAssertionRawResponse assertion,
        AssertionOptions originalOptions,
        string? ip,
        string? userAgent,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var credential = await db.WebAuthnCredentials
            .Include(c => c.User)
            .FirstOrDefaultAsync(c => c.CredentialId == assertion.RawId, ct)
            .ConfigureAwait(false);
        if (credential is null || credential.User is null || credential.User.IsDisabled)
            return null;

        var ownerUserId = credential.UserId;

        VerifyAssertionResult result;
        try
        {
            result = await _fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = assertion,
                OriginalOptions = originalOptions,
                StoredPublicKey = credential.PublicKey,
                StoredSignatureCounter = (uint)credential.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = async (args, token) =>
                {
                    // The user handle must map back to the same user that owns this credential.
                    if (args.UserHandle.Length != 16) return false;
                    var handleUserId = new Guid(args.UserHandle);
                    return await db.WebAuthnCredentials
                        .AnyAsync(c => c.CredentialId == args.CredentialId && c.UserId == handleUserId, token)
                        .ConfigureAwait(false);
                },
            }, ct).ConfigureAwait(false);
        }
        catch (Fido2VerificationException ex)
        {
            _logger.LogWarning(ex, "Passkey assertion verification failed.");
            return null;
        }

        var nowUtc = DateTime.UtcNow;
        credential.SignCount = result.SignCount;
        credential.LastUsedAtUtc = nowUtc;
        credential.User.LastLoginAtUtc = nowUtc;

        var session = new Session
        {
            Id = Guid.NewGuid(),
            UserId = ownerUserId,
            IssuedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc.AddDays(_authOptions.CurrentValue.SessionLifetimeDays),
            IpAddress = ip,
            UserAgent = userAgent,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return session;
    }

    public async Task<IReadOnlyList<WebAuthnCredentialView>> ListCredentialsAsync(Guid userId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var rows = await db.WebAuthnCredentials
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.CreatedAtUtc)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(ToView).ToList();
    }

    public async Task<bool> DeleteCredentialAsync(Guid userId, Guid credentialId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var credential = await db.WebAuthnCredentials
            .FirstOrDefaultAsync(c => c.Id == credentialId && c.UserId == userId, ct)
            .ConfigureAwait(false);
        if (credential is null) return false;

        db.WebAuthnCredentials.Remove(credential);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static WebAuthnCredentialView ToView(WebAuthnCredential c) =>
        new(c.Id, c.DisplayName, c.AaGuid, c.CreatedAtUtc, c.LastUsedAtUtc);
}
