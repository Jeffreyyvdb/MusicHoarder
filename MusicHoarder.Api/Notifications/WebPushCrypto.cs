using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace MusicHoarder.Api.Notifications;

/// <summary>
/// The two pieces of cryptography Web Push needs, on the framework's own primitives (no package):
/// the payload encryption of RFC 8291 (<c>aes128gcm</c>, RFC 8188) and the VAPID token of RFC 8292.
/// Pinned against the RFC 8291 worked example in <c>WebPushCryptoTests</c>.
/// </summary>
public static class WebPushCrypto
{
    /// <summary>The record size written into the header. One record holds any payload we send.</summary>
    private const int RecordSize = 4096;

    /// <summary>What a push service accepts: 4096 bytes of body, less the header and the tag.</summary>
    public const int MaxPlaintextBytes = RecordSize - 86 - 16 - 1;

    private static readonly byte[] KeyInfoLabel = Encoding.ASCII.GetBytes("WebPush: info\0");
    private static readonly byte[] CekInfo = Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0");
    private static readonly byte[] NonceInfo = Encoding.ASCII.GetBytes("Content-Encoding: nonce\0");

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> to one browser subscription, returning the request body
    /// (header + ciphertext). A fresh ephemeral key and salt every call.
    /// </summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, string p256dh, string auth)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return Encrypt(plaintext, p256dh, auth, ephemeral, RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>The deterministic core, with the sender key and salt supplied (the tests pass the RFC's).</summary>
    internal static byte[] Encrypt(
        ReadOnlySpan<byte> plaintext, string p256dh, string auth, ECDiffieHellman sender, byte[] salt)
    {
        if (plaintext.Length > MaxPlaintextBytes)
            throw new ArgumentException($"A push payload holds at most {MaxPlaintextBytes} bytes.", nameof(plaintext));

        var uaPublic = WebEncoders.Base64UrlDecode(p256dh);
        var authSecret = WebEncoders.Base64UrlDecode(auth);
        if (uaPublic.Length != 65 || uaPublic[0] != 0x04)
            throw new ArgumentException("p256dh must be an uncompressed P-256 point.", nameof(p256dh));
        if (authSecret.Length < 16)
            throw new ArgumentException("auth must be 16 bytes.", nameof(auth));

        var asPublic = ExportUncompressed(sender);
        using var receiver = ImportPublic(uaPublic);
        var ecdhSecret = sender.DeriveRawSecretAgreement(receiver.PublicKey);

        // IKM = HKDF(salt = auth_secret, ikm = ecdh_secret, info = "WebPush: info\0" || ua || as, 32)
        var keyInfo = new byte[KeyInfoLabel.Length + 65 + 65];
        KeyInfoLabel.CopyTo(keyInfo, 0);
        uaPublic.CopyTo(keyInfo, KeyInfoLabel.Length);
        asPublic.CopyTo(keyInfo, KeyInfoLabel.Length + 65);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, authSecret, keyInfo);

        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, CekInfo);
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, NonceInfo);

        // One record, so it is the last one: the padding delimiter is 0x02 and there is no padding.
        var padded = new byte[plaintext.Length + 1];
        plaintext.CopyTo(padded);
        padded[^1] = 0x02;

        var body = new byte[86 + padded.Length + 16];
        salt.CopyTo(body, 0);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(16, 4), RecordSize);
        body[20] = 65;
        asPublic.CopyTo(body, 21);

        using var aes = new AesGcm(cek, 16);
        aes.Encrypt(nonce, padded, body.AsSpan(86, padded.Length), body.AsSpan(86 + padded.Length, 16));
        return body;
    }

    /// <summary>
    /// The <c>Authorization</c> header value for one push request: <c>vapid t=&lt;jwt&gt;, k=&lt;key&gt;</c>.
    /// </summary>
    public static string VapidAuthorization(
        Uri endpoint, string subject, ECDsa signer, string publicKey, DateTimeOffset expiresAt)
    {
        var audience = endpoint.GetLeftPart(UriPartial.Authority);
        var header = Base64Url("""{"typ":"JWT","alg":"ES256"}"""u8);
        var claims = Base64Url(Encoding.UTF8.GetBytes(
            $$"""{"aud":"{{audience}}","exp":{{expiresAt.ToUnixTimeSeconds()}},"sub":"{{JsonEscape(subject)}}"}"""));
        var signingInput = $"{header}.{claims}";
        // IEEE P1363 (r || s) is .NET's default for ECDsa, and exactly what ES256 wants.
        var signature = signer.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
        return $"vapid t={signingInput}.{Base64Url(signature)}, k={publicKey}";
    }

    /// <summary>A new VAPID key pair: (uncompressed public key, private scalar), both base64url.</summary>
    public static (string PublicKey, string PrivateKey) GenerateVapidKeys()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: true);
        return (Base64Url(Uncompressed(parameters.Q)), Base64Url(parameters.D!));
    }

    /// <summary>The signer for a VAPID key pair. Throws when the two halves do not belong together.</summary>
    public static ECDsa ImportVapidKey(string publicKey, string privateKey)
    {
        var point = WebEncoders.Base64UrlDecode(publicKey);
        if (point.Length != 65 || point[0] != 0x04)
            throw new CryptographicException("The VAPID public key must be an uncompressed P-256 point.");
        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = WebEncoders.Base64UrlDecode(privateKey),
            Q = new ECPoint { X = point[1..33], Y = point[33..65] },
        };
        var key = ECDsa.Create();
        key.ImportParameters(parameters); // validates that D and Q match
        return key;
    }

    internal static byte[] ExportUncompressed(ECDiffieHellman key) =>
        Uncompressed(key.ExportParameters(includePrivateParameters: false).Q);

    private static byte[] Uncompressed(ECPoint q)
    {
        var point = new byte[65];
        point[0] = 0x04;
        q.X!.CopyTo(point, 1);
        q.Y!.CopyTo(point, 33);
        return point;
    }

    private static ECDiffieHellman ImportPublic(byte[] point) =>
        ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33], Y = point[33..65] },
        });

    private static string Base64Url(ReadOnlySpan<byte> bytes) => WebEncoders.Base64UrlEncode(bytes);

    private static string JsonEscape(string value) =>
        System.Text.Json.JsonEncodedText.Encode(value).ToString();
}
