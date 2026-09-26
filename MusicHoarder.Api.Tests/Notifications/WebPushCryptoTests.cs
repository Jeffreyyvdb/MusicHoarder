using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using MusicHoarder.Api.Notifications;

namespace MusicHoarder.Api.Tests.Notifications;

public class WebPushCryptoTests
{
    // RFC 8291, section 5 and appendix A.
    private const string Plaintext = "V2hlbiBJIGdyb3cgdXAsIEkgd2FudCB0byBiZSBhIHdhdGVybWVsb24";
    private const string AsPublic = "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string AsPrivate = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    private const string UaPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string UaPrivate = "q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94";
    private const string Salt = "DGv6ra1nlYgDCS1FRnbzlw";
    private const string AuthSecret = "BTBZMqHH6r4Tts7J_aSIgg";
    private const string ExpectedBody =
        "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27ml" +
        "mlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPT" +
        "pK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN";

    [Fact]
    public void Encryption_matches_the_worked_example_of_RFC_8291()
    {
        using var sender = ImportEcdh(AsPublic, AsPrivate);

        var body = WebPushCrypto.Encrypt(
            WebEncoders.Base64UrlDecode(Plaintext), UaPublic, AuthSecret, sender, WebEncoders.Base64UrlDecode(Salt));

        Assert.Equal(ExpectedBody, WebEncoders.Base64UrlEncode(body));
    }

    [Fact]
    public void A_browser_holding_its_private_key_can_decrypt_what_is_sent_to_it()
    {
        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p256dh = WebEncoders.Base64UrlEncode(Uncompressed(browser.ExportParameters(false).Q));
        var auth = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
        var message = """{"type":"chat","title":"Alice","body":"Listen to this 🎧"}""";

        var body = WebPushCrypto.Encrypt(Encoding.UTF8.GetBytes(message), p256dh, auth);

        Assert.Equal(message, Encoding.UTF8.GetString(Decrypt(body, browser, auth)));
    }

    [Fact]
    public void A_payload_too_large_for_one_record_is_refused()
    {
        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p256dh = WebEncoders.Base64UrlEncode(Uncompressed(browser.ExportParameters(false).Q));

        Assert.Throws<ArgumentException>(() =>
            WebPushCrypto.Encrypt(new byte[WebPushCrypto.MaxPlaintextBytes + 1], p256dh, AuthSecret));
    }

    [Fact]
    public void The_vapid_token_is_an_ES256_JWT_for_the_push_services_origin()
    {
        var (publicKey, privateKey) = WebPushCrypto.GenerateVapidKeys();
        using var signer = WebPushCrypto.ImportVapidKey(publicKey, privateKey);
        var expires = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

        var header = WebPushCrypto.VapidAuthorization(
            new Uri("https://fcm.googleapis.com/fcm/send/abc:def"), "mailto:admin@example.com", signer, publicKey, expires);

        Assert.StartsWith("vapid t=", header);
        Assert.EndsWith($", k={publicKey}", header);
        var jwt = header["vapid t=".Length..header.IndexOf(',')];
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);

        using var claims = JsonDocument.Parse(WebEncoders.Base64UrlDecode(parts[1]));
        Assert.Equal("https://fcm.googleapis.com", claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal(expires.ToUnixTimeSeconds(), claims.RootElement.GetProperty("exp").GetInt64());
        Assert.Equal("mailto:admin@example.com", claims.RootElement.GetProperty("sub").GetString());

        // Verifies with the public key alone, as the push service will.
        var point = WebEncoders.Base64UrlDecode(publicKey);
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33], Y = point[33..65] },
        });
        Assert.True(verifier.VerifyData(
            Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), WebEncoders.Base64UrlDecode(parts[2]), HashAlgorithmName.SHA256));
    }

    [Fact]
    public void A_private_key_that_does_not_match_its_public_key_is_rejected()
    {
        var (publicKey, _) = WebPushCrypto.GenerateVapidKeys();
        var (_, otherPrivate) = WebPushCrypto.GenerateVapidKeys();

        Assert.ThrowsAny<CryptographicException>(() => WebPushCrypto.ImportVapidKey(publicKey, otherPrivate));
    }

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc", true)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/abc", true)]
    [InlineData("https://web.push.apple.com/QGuQyavXutnMH", true)]
    [InlineData("https://wns2-db5p.notify.windows.com/w/?token=abc", true)]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc", false)]            // not https
    [InlineData("https://fcm.googleapis.com:8443/fcm/send/abc", false)]      // not the default port
    [InlineData("https://evilfcm.googleapis.com.attacker.example/x", false)]
    [InlineData("https://notfcm.googleapis.com.example/x", false)]
    [InlineData("https://localhost/push", false)]
    [InlineData("https://10.0.0.5/push", false)]
    [InlineData("https://postgres/push", false)]
    [InlineData("", false)]
    public void Only_known_push_services_are_ever_called(string endpoint, bool allowed) =>
        Assert.Equal(allowed, WebPushSender.IsAllowedEndpoint(endpoint));

    // ── The receiving side, as a browser does it ────────────────────────────────────────────

    private static byte[] Decrypt(byte[] body, ECDiffieHellman browser, string auth)
    {
        var salt = body[..16];
        var recordSize = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16, 4));
        Assert.Equal(4096u, recordSize);
        Assert.Equal(65, body[20]);
        var asPublic = body[21..86];

        using var sender = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = asPublic[1..33], Y = asPublic[33..65] },
        });
        var ecdhSecret = browser.DeriveRawSecretAgreement(sender.PublicKey);
        var uaPublic = Uncompressed(browser.ExportParameters(false).Q);
        var keyInfo = Encoding.ASCII.GetBytes("WebPush: info\0").Concat(uaPublic).Concat(asPublic).ToArray();
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, WebEncoders.Base64UrlDecode(auth), keyInfo);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        var ciphertext = body[86..^16];
        var tag = body[^16..];
        var padded = new byte[ciphertext.Length];
        using var aes = new AesGcm(cek, 16);
        aes.Decrypt(nonce, ciphertext, tag, padded);
        Assert.Equal(0x02, padded[^1]);
        return padded[..^1];
    }

    private static ECDiffieHellman ImportEcdh(string publicKey, string privateKey)
    {
        var point = WebEncoders.Base64UrlDecode(publicKey);
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = WebEncoders.Base64UrlDecode(privateKey),
            Q = new ECPoint { X = point[1..33], Y = point[33..65] },
        });
    }

    private static byte[] Uncompressed(ECPoint q) => [0x04, .. q.X!, .. q.Y!];
}
