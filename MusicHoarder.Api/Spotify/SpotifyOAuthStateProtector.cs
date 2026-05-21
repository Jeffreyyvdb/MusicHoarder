using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MusicHoarder.Api.Spotify;

/// <summary>
/// Creates and validates the signed OAuth <c>state</c> for the Spotify relay flow.
/// </summary>
/// <remarks>
/// <para>
/// Format: <c>state = base64url(payloadJson) + "." + base64url(hmacSha256(key, base64url(payloadJson)))</c>.
/// The HMAC is computed over the ASCII bytes of the first segment (not a re-serialized payload), so the relay can
/// verify without reproducing the exact JSON — it signs the literal first segment it received. The TypeScript relay
/// (<c>frontend/src/lib/server/spotify-relay-state.ts</c>) mirrors this exactly.
/// </para>
/// <para>
/// The payload carries the originating environment's own origin (<c>ro</c>), a random nonce (<c>n</c>) and an
/// issued-at unix timestamp (<c>iat</c>). The signature is the CSRF / open-redirect defense; the TTL guards replay.
/// </para>
/// </remarks>
public static class SpotifyOAuthStateProtector
{
    private sealed record StatePayload(
        [property: JsonPropertyName("ro")] string ReturnOrigin,
        [property: JsonPropertyName("n")] string Nonce,
        [property: JsonPropertyName("iat")] long IssuedAtUnix);

    /// <summary>Builds a signed state encoding <paramref name="returnOrigin"/>.</summary>
    public static string Create(string returnOrigin, string signingKey)
    {
        if (string.IsNullOrEmpty(signingKey))
            throw new ArgumentException("Signing key required.", nameof(signingKey));

        var payload = new StatePayload(
            returnOrigin,
            Base64UrlEncode(RandomNumberGenerator.GetBytes(16)),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var payloadSegment = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signatureSegment = Base64UrlEncode(ComputeHmac(signingKey, payloadSegment));
        return $"{payloadSegment}.{signatureSegment}";
    }

    /// <summary>
    /// Verifies the signature and TTL and extracts the return origin. Returns false on any tampering, bad signature,
    /// malformed input, or expiry.
    /// </summary>
    public static bool TryValidate(string? state, string signingKey, TimeSpan ttl, out string returnOrigin)
    {
        returnOrigin = string.Empty;
        if (string.IsNullOrWhiteSpace(state) || string.IsNullOrEmpty(signingKey))
            return false;

        var dot = state.IndexOf('.');
        if (dot <= 0 || dot == state.Length - 1)
            return false;

        var payloadSegment = state[..dot];
        var signatureSegment = state[(dot + 1)..];

        byte[] providedSignature;
        try
        {
            providedSignature = Base64UrlDecode(signatureSegment);
        }
        catch (FormatException)
        {
            return false;
        }

        var expectedSignature = ComputeHmac(signingKey, payloadSegment);
        if (!CryptographicOperations.FixedTimeEquals(providedSignature, expectedSignature))
            return false;

        StatePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<StatePayload>(Base64UrlDecode(payloadSegment));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.ReturnOrigin))
            return false;

        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(payload.IssuedAtUnix);
        var age = DateTimeOffset.UtcNow - issuedAt;
        if (age > ttl || age < -TimeSpan.FromMinutes(1))
            return false;

        returnOrigin = payload.ReturnOrigin;
        return true;
    }

    private static byte[] ComputeHmac(string key, string message)
        => HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.ASCII.GetBytes(message));

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
