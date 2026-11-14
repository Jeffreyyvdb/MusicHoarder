using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// Encrypts credential columns (a Spotify refresh token, a client secret) before they reach the
/// database, with the same DataProtection key ring as the session cookie. A database dump or a
/// backup alone then holds no usable credential; the key ring under
/// <c>Auth__DataProtectionKeysPath</c> is needed too.
/// </summary>
public interface ISecretColumnProtector
{
    string Protect(string plaintext);

    /// <summary>
    /// The plaintext of a stored value. A value without <see cref="SecretColumnProtector.Prefix"/>
    /// was written before encryption and is returned unchanged; a value the key ring can no longer
    /// open (the key directory was lost) reads as <c>null</c>, so the account shows as not
    /// connected and can be connected again, instead of every query on the row throwing.
    /// </summary>
    string? Unprotect(string stored);
}

public sealed class SecretColumnProtector(IDataProtectionProvider provider) : ISecretColumnProtector
{
    /// <summary>Marks an encrypted value. Versioned so the scheme can change without guessing.</summary>
    public const string Prefix = "dp1:";

    private const string Purpose = "MusicHoarder.SecretColumns.v1";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(string plaintext) => Prefix + _protector.Protect(plaintext);

    public string? Unprotect(string stored)
    {
        if (!IsProtected(stored))
            return stored;
        try
        {
            return _protector.Unprotect(stored[Prefix.Length..]);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public static bool IsProtected(string? stored) =>
        stored is not null && stored.StartsWith(Prefix, StringComparison.Ordinal);
}

/// <summary>EF value converter that runs a column through an <see cref="ISecretColumnProtector"/>.</summary>
public sealed class ProtectedStringConverter(ISecretColumnProtector protector)
    : ValueConverter<string?, string?>(
        v => v == null ? null : protector.Protect(v),
        v => v == null ? null : protector.Unprotect(v));
