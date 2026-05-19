using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace MusicHoarder.Api.Auth;

/// <summary>
/// Posts a plain-text email to Resend's REST API. Demo accounts never trigger a real send
/// — that path falls back to the console logger so a stranger typing the demo email can't
/// burn through your Resend quota.
/// </summary>
public sealed class ResendMagicLinkSender : IMagicLinkSender
{
    private readonly HttpClient _http;
    private readonly IOptionsMonitor<ResendOptions> _options;
    private readonly IMagicLinkSender _fallback;
    private readonly ILogger<ResendMagicLinkSender> _logger;

    public ResendMagicLinkSender(
        HttpClient http,
        IOptionsMonitor<ResendOptions> options,
        ConsoleMagicLinkSender fallback,
        ILogger<ResendMagicLinkSender> logger)
    {
        _http = http;
        _options = options;
        _fallback = fallback;
        _logger = logger;
    }

    public bool IsConsoleFallback => false;

    public async Task SendAsync(User user, string magicLinkUrl, CancellationToken ct = default)
    {
        if (user.Role == UserRole.Demo)
        {
            _logger.LogDebug("Skipping Resend for demo user; using console fallback.");
            await _fallback.SendAsync(user, magicLinkUrl, ct);
            return;
        }

        var opts = _options.CurrentValue;
        var payload = new
        {
            from = opts.FromAddress,
            to = new[] { user.Email },
            subject = "Your MusicHoarder sign-in link",
            text = $"Click to sign in:\n\n{magicLinkUrl}\n\nThis link expires in 15 minutes and can only be used once.",
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opts.ApiKey);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogError("Resend send failed {Status}: {Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException($"Resend send failed: {(int)response.StatusCode}");
        }
    }
}
