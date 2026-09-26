using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MusicHoarder.Api.Notifications;

public enum WebPushOutcome
{
    Delivered,

    /// <summary>The push service says the subscription no longer exists (404/410): delete it.</summary>
    Gone,

    /// <summary>Anything else: a transient failure, a rejected token, a rate limit.</summary>
    Failed,
}

/// <summary>What is sent to one subscription. Serialised as JSON and encrypted.</summary>
/// <param name="Topic">Replaces an undelivered message with the same topic at the push service
/// (at most 32 base64url characters), so a burst of messages to an offline phone arrives as one.</param>
public sealed record WebPushMessage(object Payload, TimeSpan TimeToLive, bool Urgent, string? Topic);

public interface IWebPushSender
{
    Task<WebPushOutcome> SendAsync(
        string endpoint, string p256dh, string auth, WebPushMessage message, CancellationToken ct);
}

/// <summary>
/// Delivers one encrypted message to one browser's push service (RFC 8030 + 8291 + 8292).
///
/// <para>
/// The endpoint is a URL a browser handed us, so it is the one place the API makes a request to an
/// address a user chose. It is only ever made to a known push service over HTTPS
/// (<see cref="IsAllowedEndpoint"/>) — anything else is refused when the subscription is saved and
/// again here — so a subscription cannot be used to make the server POST into its own network.
/// </para>
/// </summary>
public sealed class WebPushSender(
    IHttpClientFactory httpClients,
    IVapidKeyStore keys,
    ILogger<WebPushSender> logger) : IWebPushSender
{
    public const string HttpClientName = "WebPush";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock _signLock = new();

    /// <summary>
    /// The push services of the browsers that implement Web Push: Chrome/Edge/Samsung/Opera
    /// (FCM), Firefox (Mozilla autopush), Safari and iOS home-screen apps (Apple), and Edge on
    /// Windows (WNS). Suffix-matched on a dot boundary.
    /// </summary>
    private static readonly string[] AllowedHostSuffixes =
    [
        "fcm.googleapis.com",
        "android.googleapis.com",
        "push.services.mozilla.com",
        "push.apple.com",
        "notify.windows.com",
    ];

    public static bool IsAllowedEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > 2048) return false;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        var host = uri.IdnHost.ToLowerInvariant();
        return AllowedHostSuffixes.Any(s => host == s || host.EndsWith("." + s, StringComparison.Ordinal));
    }

    public async Task<WebPushOutcome> SendAsync(
        string endpoint, string p256dh, string auth, WebPushMessage message, CancellationToken ct)
    {
        if (!IsAllowedEndpoint(endpoint)) return WebPushOutcome.Gone;
        var identity = await keys.GetAsync(ct);
        if (identity is null) return WebPushOutcome.Failed;

        byte[] body;
        try
        {
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(message.Payload, message.Payload.GetType(), Json);
            body = WebPushCrypto.Encrypt(plaintext, p256dh, auth);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or System.Security.Cryptography.CryptographicException)
        {
            // Keys a browser would never produce; the subscription can never be delivered to.
            logger.LogInformation(ex, "Dropping a push subscription whose keys cannot be used");
            return WebPushOutcome.Gone;
        }

        var uri = new Uri(endpoint);
        string authorization;
        lock (_signLock)
        {
            authorization = WebPushCrypto.VapidAuthorization(
                uri, identity.Subject, identity.Signer, identity.PublicKey, DateTimeOffset.UtcNow.AddHours(12));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("TTL", ((int)message.TimeToLive.TotalSeconds).ToString());
        request.Headers.TryAddWithoutValidation("Urgency", message.Urgent ? "high" : "normal");
        if (!string.IsNullOrEmpty(message.Topic))
            request.Headers.TryAddWithoutValidation("Topic", message.Topic);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");

        try
        {
            using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return WebPushOutcome.Delivered;
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return WebPushOutcome.Gone;

            var detail = await ReadDetailAsync(response, ct);
            logger.LogWarning("Push service {Host} refused a notification: {Status} {Detail}",
                uri.Host, (int)response.StatusCode, detail);
            return WebPushOutcome.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not reach push service {Host}", uri.Host);
            return WebPushOutcome.Failed;
        }
    }

    private static async Task<string> ReadDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            return text.Length > 300 ? text[..300] : text;
        }
        catch
        {
            return string.Empty;
        }
    }
}

/// <summary>A push topic: letters, digits, '-' and '_' only, at most 32 characters.</summary>
public static class WebPushTopic
{
    public static string For(string prefix, Guid id)
    {
        var raw = new StringBuilder(prefix);
        raw.Append(id.ToString("N"));
        return raw.Length > 32 ? raw.ToString(0, 32) : raw.ToString();
    }
}
