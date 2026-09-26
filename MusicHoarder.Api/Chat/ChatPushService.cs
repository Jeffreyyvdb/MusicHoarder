using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Notifications;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Chat;

public interface IChatNotificationQueue
{
    /// <summary>A message was written; notify its recipients shortly unless they read it first.</summary>
    void Enqueue(long messageId, DateTime createdAtUtc);
}

/// <summary>The hand-off from the request that wrote a message to <see cref="ChatPushService"/>.</summary>
public sealed class ChatNotificationQueue : IChatNotificationQueue
{
    private readonly Channel<(long MessageId, DateTime CreatedAtUtc)> _channel =
        Channel.CreateBounded<(long, DateTime)>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    public ChannelReader<(long MessageId, DateTime CreatedAtUtc)> Reader => _channel.Reader;

    public void Enqueue(long messageId, DateTime createdAtUtc) => _channel.Writer.TryWrite((messageId, createdAtUtc));
}

/// <summary>What a chat notification carries to the service worker, which turns it into the notification.</summary>
/// <param name="UserId">The account it is for: a browser signed in to several accounts gets each one's.</param>
/// <param name="Badge">The account's unread total, for the app icon badge.</param>
public sealed record ChatPushPayload(
    string Type,
    Guid ConversationId,
    long MessageId,
    Guid UserId,
    string Title,
    string Body,
    string Url,
    string Tag,
    int Badge);

/// <summary>
/// Sends chat notifications through Web Push. Each message waits
/// <see cref="WebPushOptions.ChatDelaySeconds"/> first, and a recipient whose read mark has passed it
/// by then — they had the conversation open, on any device — is not notified: an open chat does not
/// buzz your phone. Notifications for one conversation replace each other (same tag and push topic),
/// so a burst of messages to a phone that is offline arrives as one.
/// </summary>
public sealed class ChatPushService(
    ChatNotificationQueue queue,
    IServiceScopeFactory scopes,
    IWebPushSender sender,
    IOptions<WebPushOptions> options,
    TimeProvider time,
    ILogger<ChatPushService> logger) : BackgroundService
{
    /// <summary>Deliveries that fail without the push service saying "gone" before a subscription is dropped.</summary>
    internal const int MaxConsecutiveFailures = 20;

    private const int MaxBodyLength = 180;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (messageId, createdAtUtc) in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var due = createdAtUtc + TimeSpan.FromSeconds(Math.Max(0, options.Value.ChatDelaySeconds));
                var wait = due - time.GetUtcNow().UtcDateTime;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, time, stoppingToken);

                await DeliverAsync(messageId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not send the notifications for chat message {MessageId}", messageId);
            }
        }
    }

    internal async Task DeliverAsync(long messageId, CancellationToken ct)
    {
        if (!options.Value.Enabled) return;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        // Outside any request: every read here names its rows explicitly.
        var message = await db.ChatMessages.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null || message.Kind == ChatMessageKind.ShareOpened) return;

        var recipients = await db.ChatParticipants.AsNoTracking()
            .Where(p => p.ConversationId == message.ConversationId && p.UserId != message.SenderUserId)
            .ToListAsync(ct);
        recipients = recipients
            .Where(p => message.VisibleToUserId == null || message.VisibleToUserId == p.UserId)
            .Where(p => p.LastReadAtUtc == null || p.LastReadAtUtc < message.CreatedAtUtc)
            .ToList();
        if (recipients.Count == 0) return;

        var recipientIds = recipients.Select(r => r.UserId).ToList();
        var subscriptions = await db.WebPushSubscriptions.IgnoreQueryFilters()
            .Where(s => recipientIds.Contains(s.UserId))
            .ToListAsync(ct);
        if (subscriptions.Count == 0) return;

        var senderUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == message.SenderUserId, ct);
        var senderName = senderUser is null ? "MusicHoarder" : ChatService.NameOf(senderUser);
        var body = await DescribeAsync(db, message, ct);

        foreach (var recipient in recipients)
        {
            var mine = subscriptions.Where(s => s.UserId == recipient.UserId).ToList();
            if (mine.Count == 0) continue;

            var unread = await UnreadAsync(db, recipient.UserId, ct);
            var inThisChat = unread.GetValueOrDefault(message.ConversationId);
            var payload = new ChatPushPayload(
                "chat",
                message.ConversationId,
                message.Id,
                recipient.UserId,
                inThisChat > 1 ? $"{senderName} ({inThisChat})" : senderName,
                body,
                $"/chats/{message.ConversationId}",
                $"chat-{message.ConversationId:N}",
                unread.Values.Sum());
            var push = new WebPushMessage(payload, TimeSpan.FromDays(2), Urgent: true, WebPushTopic.For("c", message.ConversationId));

            foreach (var subscription in mine)
            {
                var outcome = await sender.SendAsync(subscription.Endpoint, subscription.P256dh, subscription.Auth, push, ct);
                switch (outcome)
                {
                    case WebPushOutcome.Delivered:
                        subscription.LastDeliveredAtUtc = time.GetUtcNow().UtcDateTime;
                        subscription.FailureCount = 0;
                        break;
                    case WebPushOutcome.Gone:
                        db.WebPushSubscriptions.Remove(subscription);
                        break;
                    default:
                        if (++subscription.FailureCount >= MaxConsecutiveFailures)
                            db.WebPushSubscriptions.Remove(subscription);
                        break;
                }
            }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The notification's text: what was written, else what was sent.</summary>
    private static async Task<string> DescribeAsync(MusicHoarderDbContext db, ChatMessage message, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(message.Text)) return Clip(message.Text);

        if (message.ShareId is { } shareId)
        {
            var share = await db.SongShares.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.Id == shareId)
                .Select(s => new { s.Scope, Title = s.Song!.Title ?? s.Song.FileName, s.Song.Artist, s.Song.AlbumArtist, s.Song.Album })
                .FirstOrDefaultAsync(ct);
            if (share is null) return "Sent you something";
            return share.Scope == ShareScope.Album && !string.IsNullOrWhiteSpace(share.Album)
                ? Clip($"Sent you an album: {share.Album}{By(share.AlbumArtist ?? share.Artist)}")
                : Clip($"Sent you a song: {share.Title}{By(share.Artist)}");
        }

        if (message.LinkUrl is { } url)
        {
            var noun = message.LinkProvider switch
            {
                "spotify" => message.LinkKind is { } kind ? $"a Spotify {kind}" : "a Spotify link",
                "youtube" => message.LinkKind == "playlist" ? "a YouTube playlist" : "a YouTube video",
                _ => "a link",
            };
            var what = message.LinkTitle ?? (Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url);
            return Clip($"Sent you {noun}: {what}{By(message.LinkSubtitle)}");
        }

        return "New message";
    }

    private static string By(string? artist) => string.IsNullOrWhiteSpace(artist) ? "" : $" · {artist}";

    private static string Clip(string text)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= MaxBodyLength ? flat : flat[..(MaxBodyLength - 1)].TrimEnd() + "…";
    }

    /// <summary>The recipient's unread messages per conversation, as the app counts them.</summary>
    private static async Task<Dictionary<Guid, int>> UnreadAsync(MusicHoarderDbContext db, Guid userId, CancellationToken ct) =>
        await (
                from m in db.ChatMessages.IgnoreQueryFilters()
                join p in db.ChatParticipants on m.ConversationId equals p.ConversationId
                where p.UserId == userId
                    && m.SenderUserId != userId
                    && (m.VisibleToUserId == null || m.VisibleToUserId == userId)
                    && (p.LastReadAtUtc == null || m.CreatedAtUtc > p.LastReadAtUtc)
                select m.ConversationId)
            .GroupBy(id => id)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
}
