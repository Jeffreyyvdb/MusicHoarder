using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Notifications;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Artists;

public interface IArtistReleaseNotifier
{
    Task NotifyAsync(NewReleaseNotice notice, CancellationToken ct);
}

/// <summary>The push payload the service worker turns into a notification (see <c>service-worker/index.ts</c>).</summary>
public sealed record ReleasePushPayload(string Type, Guid UserId, string Title, string Body, string Url, string Tag);

/// <summary>
/// Tells the owner a followed artist put something out, through Web Push to every browser they enabled
/// notifications in (the same subscriptions chat uses). A no-op where Web Push is not configured.
/// </summary>
public sealed class ArtistReleaseNotifier(
    MusicHoarderDbContext db,
    IWebPushSender sender,
    IOptions<WebPushOptions> options) : IArtistReleaseNotifier
{
    public async Task NotifyAsync(NewReleaseNotice notice, CancellationToken ct)
    {
        if (!options.Value.Enabled) return;

        var subscriptions = await db.WebPushSubscriptions.IgnoreQueryFilters()
            .Where(s => s.UserId == notice.OwnerUserId)
            .ToListAsync(ct);
        if (subscriptions.Count == 0) return;

        var kind = ArtistDiscography.WireKind(notice.Kind) switch
        {
            "ep" => "EP",
            "single" => "single",
            "compilation" => "compilation",
            _ => "album",
        };
        var body = notice.Queued
            ? $"{notice.Title} ({kind}) — downloading {notice.TracksQueued} track{(notice.TracksQueued == 1 ? "" : "s")}"
            : $"{notice.Title} ({kind})";
        var payload = new ReleasePushPayload(
            "release",
            notice.OwnerUserId,
            $"New from {notice.ArtistName}",
            body,
            $"/library?artist={Uri.EscapeDataString(notice.ArtistName)}",
            $"release-{TitleNormalizer.NormalizeForSearch(notice.ArtistName)}-{TitleNormalizer.NormalizeForSearch(notice.Title)}");
        var push = new WebPushMessage(payload, TimeSpan.FromDays(3), Urgent: false, Topic: null);

        foreach (var subscription in subscriptions)
        {
            var outcome = await sender.SendAsync(subscription.Endpoint, subscription.P256dh, subscription.Auth, push, ct);
            if (outcome == WebPushOutcome.Gone) db.WebPushSubscriptions.Remove(subscription);
            else if (outcome == WebPushOutcome.Delivered) subscription.LastDeliveredAtUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }
}
