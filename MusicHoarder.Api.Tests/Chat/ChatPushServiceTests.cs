using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Chat;
using MusicHoarder.Api.Notifications;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Chat;

public class ChatPushServiceTests
{
    private static readonly Guid AliceId = TestUsers.FriendId;
    private static CurrentUser Admin => TestCurrentUserAccessor.OwnerUser;
    private static CurrentUser Alice => TestChat.Member(AliceId, "Alice");

    [Fact]
    public async Task An_unread_message_is_pushed_to_every_browser_of_the_recipient_only()
    {
        var (options, conversationId) = await SeedConversationAsync();
        await AddSubscriptionAsync(options, AliceId, "https://fcm.googleapis.com/fcm/send/phone");
        await AddSubscriptionAsync(options, AliceId, "https://web.push.apple.com/ipad");
        await AddSubscriptionAsync(options, TestUsers.OwnerId, "https://fcm.googleapis.com/fcm/send/admin");
        var messageId = await SendAsync(options, conversationId, "Listen to this");
        var sender = new RecordingSender();

        await Service(options, sender).DeliverAsync(messageId, CancellationToken.None);

        Assert.Equal(
            ["https://fcm.googleapis.com/fcm/send/phone", "https://web.push.apple.com/ipad"],
            sender.Sent.Select(s => s.Endpoint).Order());
        var payload = Assert.IsType<ChatPushPayload>(sender.Sent[0].Message.Payload);
        Assert.Equal("Owner", payload.Title);
        Assert.Equal("Listen to this", payload.Body);
        Assert.Equal($"/chats/{conversationId}", payload.Url);
        Assert.Equal(AliceId, payload.UserId);
        Assert.Equal(1, payload.Badge);
    }

    [Fact]
    public async Task A_message_read_before_it_was_due_is_not_pushed()
    {
        var (options, conversationId) = await SeedConversationAsync();
        await AddSubscriptionAsync(options, AliceId, "https://fcm.googleapis.com/fcm/send/phone");
        var messageId = await SendAsync(options, conversationId, "hello");
        await using (var db = TestChat.Db(options))
            await TestChat.Service(db).MarkReadAsync(Alice, conversationId, CancellationToken.None);
        var sender = new RecordingSender();

        await Service(options, sender).DeliverAsync(messageId, CancellationToken.None);

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task A_subscription_the_push_service_says_is_gone_is_deleted()
    {
        var (options, conversationId) = await SeedConversationAsync();
        await AddSubscriptionAsync(options, AliceId, "https://fcm.googleapis.com/fcm/send/gone");
        await AddSubscriptionAsync(options, AliceId, "https://fcm.googleapis.com/fcm/send/live");
        var messageId = await SendAsync(options, conversationId, "hello");
        var sender = new RecordingSender { Gone = { "https://fcm.googleapis.com/fcm/send/gone" } };

        await Service(options, sender).DeliverAsync(messageId, CancellationToken.None);

        await using var check = TestChat.Db(options);
        var left = await check.WebPushSubscriptions.IgnoreQueryFilters().ToListAsync();
        var live = Assert.Single(left);
        Assert.Equal("https://fcm.googleapis.com/fcm/send/live", live.Endpoint);
        Assert.NotNull(live.LastDeliveredAtUtc);
    }

    [Fact]
    public async Task A_song_with_no_words_is_described_by_what_was_sent()
    {
        var (options, conversationId) = await SeedConversationAsync(withSong: true);
        await AddSubscriptionAsync(options, AliceId, "https://fcm.googleapis.com/fcm/send/phone");
        long messageId;
        await using (var db = TestChat.Db(options))
            messageId = (await TestChat.Service(db).SendAsync(Admin, conversationId, new SendChatMessageRequest(null, null, 1, "album"), CancellationToken.None)).Value!.Id;
        var sender = new RecordingSender();

        await Service(options, sender).DeliverAsync(messageId, CancellationToken.None);

        var payload = Assert.IsType<ChatPushPayload>(Assert.Single(sender.Sent).Message.Payload);
        Assert.Equal("Sent you an album: Discovery · Daft Punk", payload.Body);
    }

    [Fact]
    public async Task A_share_link_open_is_never_pushed()
    {
        var (options, _) = await SeedConversationAsync(withSong: true);
        await AddSubscriptionAsync(options, AliceId, "https://fcm.googleapis.com/fcm/send/phone");
        SongShare share;
        await using (var seed = TestChat.Db(options))
        {
            share = new SongShare { OwnerUserId = TestUsers.OwnerId, SongId = 1, Token = "tok-open", Scope = ShareScope.Song, CreatedAtUtc = DateTime.UtcNow };
            seed.SongShares.Add(share);
            await seed.SaveChangesAsync();
        }
        await using (var db = TestChat.Db(options))
            await TestChat.Service(db).RecordShareOpenedAsync(share, AliceId, CancellationToken.None);
        long messageId;
        await using (var check = TestChat.Db(options))
            messageId = (await check.ChatMessages.IgnoreQueryFilters().SingleAsync(m => m.Kind == ChatMessageKind.ShareOpened)).Id;
        var sender = new RecordingSender();

        await Service(options, sender).DeliverAsync(messageId, CancellationToken.None);

        Assert.Empty(sender.Sent);
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────

    private static ChatPushService Service(DbContextOptions<MusicHoarderDbContext> options, IWebPushSender sender)
    {
        // What a hosted service gets: a scope whose context has no signed-in user.
        var services = new ServiceCollection();
        services.AddScoped(_ => TestChat.Db(options));
        var provider = services.BuildServiceProvider();
        return new ChatPushService(
            new ChatNotificationQueue(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            sender,
            Microsoft.Extensions.Options.Options.Create(new WebPushOptions()),
            TimeProvider.System,
            NullLogger<ChatPushService>.Instance);
    }

    private static async Task<(DbContextOptions<MusicHoarderDbContext>, Guid)> SeedConversationAsync(bool withSong = false)
    {
        var options = TestChat.NewOptions();
        await using (var seed = TestChat.Db(options))
        {
            seed.Users.AddRange(
                TestChat.UserRow(TestUsers.OwnerId, "owner@test.local", UserRole.Admin, "Owner"),
                TestChat.UserRow(AliceId, "alice@test.local", UserRole.Member, "Alice"));
            if (withSong)
            {
                seed.Songs.Add(new SongMetadata
                {
                    Id = 1,
                    OwnerUserId = TestUsers.OwnerId,
                    SourcePath = "/music/1.flac",
                    FileSizeBytes = 1000,
                    FileName = "1.flac",
                    Extension = ".flac",
                    LastModifiedUtc = DateTime.UtcNow,
                    IndexedAtUtc = DateTime.UtcNow,
                    Title = "One More Time",
                    Artist = "Daft Punk",
                    AlbumArtist = "Daft Punk",
                    Album = "Discovery",
                });
            }
            await seed.SaveChangesAsync();
        }

        await using var db = TestChat.Db(options);
        var conversation = await TestChat.Service(db).StartAsync(Admin, AliceId, CancellationToken.None);
        return (options, conversation.Value!.Id);
    }

    private static async Task<long> SendAsync(DbContextOptions<MusicHoarderDbContext> options, Guid conversationId, string text)
    {
        await using var db = TestChat.Db(options);
        var sent = await TestChat.Service(db).SendAsync(Admin, conversationId, new SendChatMessageRequest(text, null, null, null), CancellationToken.None);
        return sent.Value!.Id;
    }

    private static async Task AddSubscriptionAsync(DbContextOptions<MusicHoarderDbContext> options, Guid userId, string endpoint)
    {
        await using var db = TestChat.Db(options);
        db.WebPushSubscriptions.Add(new WebPushSubscription
        {
            UserId = userId,
            Endpoint = endpoint,
            P256dh = "key",
            Auth = "auth",
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private sealed class RecordingSender : IWebPushSender
    {
        public List<(string Endpoint, WebPushMessage Message)> Sent { get; } = [];
        public HashSet<string> Gone { get; } = [];

        public Task<WebPushOutcome> SendAsync(string endpoint, string p256dh, string auth, WebPushMessage message, CancellationToken ct)
        {
            Sent.Add((endpoint, message));
            return Task.FromResult(Gone.Contains(endpoint) ? WebPushOutcome.Gone : WebPushOutcome.Delivered);
        }
    }
}
