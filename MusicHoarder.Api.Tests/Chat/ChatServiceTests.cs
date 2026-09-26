using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Chat;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Chat;

public class ChatServiceTests
{
    private static readonly Guid AliceId = TestUsers.FriendId;
    private static readonly Guid BobId = TestUsers.SecondFriendId;
    private static readonly Guid CarolId = new("c0c0c0c0-1111-4222-8333-444455556666");

    private static CurrentUser Admin => TestCurrentUserAccessor.OwnerUser;
    private static CurrentUser Alice => TestChat.Member(AliceId, "Alice");
    private static CurrentUser Bob => TestChat.Member(BobId, "Bob");

    // ── Who you can talk to ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_admin_can_reach_every_real_account_and_sees_their_emails()
    {
        var options = await SeedAsync();
        await using var db = TestChat.Db(options);

        var people = await TestChat.Service(db).ListPeopleAsync(Admin, CancellationToken.None);

        Assert.Equal(["Alice", "bob"], people.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(people, p => Assert.NotNull(p.Email));
        // Never the demo account, a disabled account, or yourself.
        Assert.DoesNotContain(people, p => p.Id == TestUsers.DemoId || p.Id == CarolId || p.Id == TestUsers.OwnerId);
    }

    [Fact]
    public async Task A_member_reaches_the_admins_and_the_people_they_share_with_without_seeing_emails()
    {
        var options = await SeedAsync();
        await using var db = TestChat.Db(options);

        var before = await TestChat.Service(db).ListPeopleAsync(Alice, CancellationToken.None);
        Assert.Equal([TestUsers.OwnerId], before.Select(p => p.Id));
        Assert.All(before, p => Assert.Null(p.Email));

        await using (var seed = TestChat.Db(options))
        {
            seed.LibraryShareGrants.Add(new LibraryShareGrant
            {
                OwnerUserId = BobId,
                GranteeUserId = AliceId,
                Scope = ShareGrantScope.Library,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var after = await TestChat.Service(db).ListPeopleAsync(Alice, CancellationToken.None);
        Assert.Contains(after, p => p.Id == BobId);
    }

    [Fact]
    public async Task A_member_cannot_open_a_chat_with_a_stranger()
    {
        var options = await SeedAsync();
        await using var db = TestChat.Db(options);

        var outcome = await TestChat.Service(db).StartAsync(Alice, BobId, CancellationToken.None);

        Assert.Equal(404, outcome.Error?.StatusCode);
        Assert.Empty(await db.ChatConversations.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Someone_who_wrote_to_you_becomes_someone_you_can_write_to()
    {
        var options = await SeedAsync();
        await using (var adminDb = TestChat.Db(options))
        {
            var chat = TestChat.Service(adminDb);
            var conversation = (await chat.StartAsync(Admin, AliceId, CancellationToken.None)).Value!;
            await chat.SendAsync(Admin, conversation.Id, new SendChatMessageRequest("Hi Alice", null, null, null), CancellationToken.None);
        }

        await using var db = TestChat.Db(options);
        var people = await TestChat.Service(db).ListPeopleAsync(Alice, CancellationToken.None);
        Assert.Contains(people, p => p.Id == TestUsers.OwnerId);
    }

    // ── Sending and reading ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_text_is_unread_for_the_recipient_until_they_read_it_and_is_announced_and_queued()
    {
        var options = await SeedAsync();
        var hub = new ChatHub();
        var queue = new RecordingNotificationQueue();
        using var aliceStream = hub.Subscribe(AliceId);
        using var adminStream = hub.Subscribe(TestUsers.OwnerId);

        Guid conversationId;
        await using (var adminDb = TestChat.Db(options))
        {
            var chat = TestChat.Service(adminDb, hub, queue);
            conversationId = (await chat.StartAsync(Admin, AliceId, CancellationToken.None)).Value!.Id;
            var sent = await chat.SendAsync(Admin, conversationId, new SendChatMessageRequest("  Listen to this  ", null, null, null), CancellationToken.None);
            Assert.Null(sent.Error);
            Assert.Equal("Listen to this", sent.Value!.Text);
            Assert.True(sent.Value.Mine);
            Assert.Equal("text", sent.Value.Kind);

            var mine = Assert.Single(await chat.ListConversationsAsync(Admin, CancellationToken.None));
            Assert.Equal(0, mine.UnreadCount);
        }

        Assert.Single(queue.Enqueued);
        Assert.True(aliceStream.Reader.TryRead(out var aliceEvent));
        Assert.Equal(ChatEventTypes.Message, aliceEvent.Type);
        Assert.True(adminStream.Reader.TryRead(out _));

        await using var db = TestChat.Db(options);
        var aliceChat = TestChat.Service(db, hub, queue);
        var listed = Assert.Single(await aliceChat.ListConversationsAsync(Alice, CancellationToken.None));
        Assert.Equal(1, listed.UnreadCount);
        Assert.False(listed.LastMessage!.Mine);
        Assert.Equal("Owner", Assert.Single(listed.Members).Name);
        Assert.Equal(1, (await aliceChat.GetUnreadAsync(Alice, CancellationToken.None)).Total);

        var read = await aliceChat.MarkReadAsync(Alice, conversationId, CancellationToken.None);
        Assert.Null(read.Error);
        Assert.Equal(0, (await aliceChat.GetUnreadAsync(Alice, CancellationToken.None)).Total);
        // The sender's devices hear about the read too — that is what shows "Seen".
        Assert.True(adminStream.Reader.TryRead(out var readEvent));
        Assert.Equal(ChatEventTypes.Read, readEvent.Type);
    }

    [Fact]
    public async Task Nobody_can_read_or_write_a_conversation_they_are_not_in()
    {
        var options = await SeedAsync();
        Guid conversationId;
        await using (var adminDb = TestChat.Db(options))
        {
            var chat = TestChat.Service(adminDb);
            conversationId = (await chat.StartAsync(Admin, AliceId, CancellationToken.None)).Value!.Id;
            await chat.SendAsync(Admin, conversationId, new SendChatMessageRequest("private", null, null, null), CancellationToken.None);
        }

        await using var db = TestChat.Db(options);
        var bobChat = TestChat.Service(db);
        Assert.Equal(404, (await bobChat.ListMessagesAsync(Bob, conversationId, null, null, null, CancellationToken.None)).Error?.StatusCode);
        Assert.Equal(404, (await bobChat.SendAsync(Bob, conversationId, new SendChatMessageRequest("hi", null, null, null), CancellationToken.None)).Error?.StatusCode);
        Assert.Equal(404, (await bobChat.MarkReadAsync(Bob, conversationId, CancellationToken.None)).Error?.StatusCode);
        Assert.Null(await bobChat.GetConversationAsync(Bob, conversationId, CancellationToken.None));
        Assert.Empty(await bobChat.ListConversationsAsync(Bob, CancellationToken.None));
        // The query filter hides the rows even from a query that forgets to scope.
        await using var bobView = TestChat.As(options, Bob);
        Assert.Empty(await bobView.ChatMessages.ToListAsync());
    }

    [Fact]
    public async Task An_empty_message_is_refused()
    {
        var options = await SeedAsync();
        await using var db = TestChat.Db(options);
        var chat = TestChat.Service(db);
        var conversationId = (await chat.StartAsync(Admin, AliceId, CancellationToken.None)).Value!.Id;

        var outcome = await chat.SendAsync(Admin, conversationId, new SendChatMessageRequest("   ", null, null, null), CancellationToken.None);

        Assert.Equal("empty_message", outcome.Error?.Code);
    }

    [Fact]
    public async Task Messages_page_backwards_and_catch_up_forwards()
    {
        var options = await SeedAsync();
        await using var db = TestChat.Db(options);
        var chat = TestChat.Service(db);
        var conversationId = (await chat.StartAsync(Admin, AliceId, CancellationToken.None)).Value!.Id;
        var ids = new List<long>();
        for (var i = 1; i <= 5; i++)
            ids.Add((await chat.SendAsync(Admin, conversationId, new SendChatMessageRequest($"m{i}", null, null, null), CancellationToken.None)).Value!.Id);

        var newest = (await chat.ListMessagesAsync(Admin, conversationId, null, null, 2, CancellationToken.None)).Value!;
        Assert.Equal(["m4", "m5"], newest.Messages.Select(m => m.Text));
        Assert.True(newest.HasMore);

        var older = (await chat.ListMessagesAsync(Admin, conversationId, newest.Messages[0].Id, null, 10, CancellationToken.None)).Value!;
        Assert.Equal(["m1", "m2", "m3"], older.Messages.Select(m => m.Text));
        Assert.False(older.HasMore);

        var since = (await chat.ListMessagesAsync(Admin, conversationId, null, ids[2], null, CancellationToken.None)).Value!;
        Assert.Equal(["m4", "m5"], since.Messages.Select(m => m.Text));
    }

    // ── Songs and links ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_song_travels_as_the_senders_share_link_which_is_reused()
    {
        var options = await SeedAsync(withSongs: true);
        await using (var adminDb = TestChat.Db(options))
        {
            var chat = TestChat.Service(adminDb);
            var conversationId = (await chat.StartAsync(Admin, AliceId, CancellationToken.None)).Value!.Id;

            var first = await chat.SendAsync(Admin, conversationId, new SendChatMessageRequest(null, null, 1, "album"), CancellationToken.None);
            var second = await chat.SendAsync(Admin, conversationId, new SendChatMessageRequest("again", null, 1, "album"), CancellationToken.None);

            Assert.Equal("share", first.Value!.Kind);
            Assert.Equal(first.Value.Share!.Token, second.Value!.Share!.Token);
            Assert.True(first.Value.Share.OwnedByViewer);
        }

        await using var db = TestChat.Db(options);
        var page = (await TestChat.Service(db).ListMessagesAsync(Alice, (await db.ChatConversations.SingleAsync()).Id, null, null, null, CancellationToken.None)).Value!;
        var share = page.Messages[0].Share!;
        Assert.Equal("Album", share.Scope);
        Assert.Equal("Discovery", share.Title);
        Assert.Equal("Daft Punk", share.Artist);
        Assert.False(share.OwnedByViewer);
        Assert.NotNull(share.Token);
        Assert.Equal(1, await db.SongShares.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Nobody_can_send_a_song_that_is_not_theirs()
    {
        var options = await SeedAsync(withSongs: true);
        await using (var adminDb = TestChat.Db(options))
        {
            var chat = TestChat.Service(adminDb);
            var id = (await chat.StartAsync(Admin, AliceId, CancellationToken.None)).Value!.Id;
            await chat.SendAsync(Admin, id, new SendChatMessageRequest("hi", null, null, null), CancellationToken.None);
        }

        await using var db = TestChat.Db(options);
        var conversationId = (await db.ChatConversations.SingleAsync()).Id;
        var outcome = await TestChat.Service(db).SendAsync(Alice, conversationId, new SendChatMessageRequest(null, null, 1, "song"), CancellationToken.None);

        Assert.Equal(404, outcome.Error?.StatusCode);
        Assert.Empty(await db.SongShares.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task A_revoked_link_leaves_the_message_but_not_the_song()
    {
        var options = await SeedAsync(withSongs: true);
        await using var db = TestChat.Db(options);
        var chat = TestChat.Service(db);
        var conversationId = (await chat.StartAsync(Admin, AliceId, CancellationToken.None)).Value!.Id;
        await chat.SendAsync(Admin, conversationId, new SendChatMessageRequest(null, null, 1, "song"), CancellationToken.None);

        var share = await db.SongShares.SingleAsync();
        share.RevokedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var message = (await chat.ListMessagesAsync(Admin, conversationId, null, null, null, CancellationToken.None)).Value!.Messages.Single();
        Assert.True(message.Share!.Revoked);
        Assert.Null(message.Share.Token);
        Assert.False(message.Share.HasCover);
    }

    [Fact]
    public async Task A_pasted_spotify_link_becomes_a_card_without_its_tracking_parameter()
    {
        var options = await SeedAsync(withSongs: true);
        await using var db = TestChat.Db(options);
        var chat = TestChat.Service(db);
        var conversationId = (await chat.StartAsync(Alice, TestUsers.OwnerId, CancellationToken.None)).Value!.Id;

        var justLink = await chat.SendAsync(Alice, conversationId,
            new SendChatMessageRequest("https://open.spotify.com/track/0DiWol3AO6WpXZgp0goxAV?si=abc123", null, null, null), CancellationToken.None);
        var withText = await chat.SendAsync(Alice, conversationId,
            new SendChatMessageRequest("Check out this song on Spotify: https://open.spotify.com/intl-nl/track/0DiWol3AO6WpXZgp0goxAV?si=x", null, null, null), CancellationToken.None);

        Assert.Equal("link", justLink.Value!.Kind);
        Assert.Null(justLink.Value.Text); // the link is the whole message: shown once, as its card
        Assert.Equal("https://open.spotify.com/track/0DiWol3AO6WpXZgp0goxAV", justLink.Value.Link!.Url);
        Assert.Equal("spotify", justLink.Value.Link.Provider);
        Assert.Equal("track", justLink.Value.Link.Kind);
        Assert.Equal("Daft Punk", justLink.Value.Link.Subtitle);
        Assert.NotNull(withText.Value!.Text);
        Assert.Equal("https://open.spotify.com/track/0DiWol3AO6WpXZgp0goxAV", withText.Value.Link!.Url);
    }

    [Fact]
    public async Task A_spotify_track_the_viewer_owns_offers_their_own_copy()
    {
        var options = await SeedAsync(withSongs: true);
        await using (var aliceDb = TestChat.Db(options))
        {
            var chat = TestChat.Service(aliceDb);
            var id = (await chat.StartAsync(Alice, TestUsers.OwnerId, CancellationToken.None)).Value!.Id;
            await chat.SendAsync(Alice, id, new SendChatMessageRequest("https://open.spotify.com/track/SpotifyTrack0001", null, null, null), CancellationToken.None);
        }

        await using var db = TestChat.Db(options);
        var conversation = Assert.Single(await TestChat.Service(db).ListConversationsAsync(Admin, CancellationToken.None));
        Assert.Equal(1, conversation.LastMessage!.Link!.LibrarySongId);
    }

    [Fact]
    public async Task One_of_this_instances_share_links_is_sent_as_the_song_it_shares()
    {
        var options = await SeedAsync(withSongs: true);
        await using (var seed = TestChat.Db(options))
        {
            seed.SongShares.Add(new SongShare { Id = 7, OwnerUserId = TestUsers.OwnerId, SongId = 1, Token = "abcDEF123_-xyz", Scope = ShareScope.Song, CreatedAtUtc = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }

        await using var db = TestChat.Db(options);
        var chat = TestChat.Service(db);
        var id = (await chat.StartAsync(Alice, TestUsers.OwnerId, CancellationToken.None)).Value!.Id;
        var sent = await chat.SendAsync(Alice, id, new SendChatMessageRequest($"{TestChat.PublicBaseUrl}/share/abcDEF123_-xyz", null, null, null), CancellationToken.None);

        Assert.Equal("share", sent.Value!.Kind);
        Assert.Equal("abcDEF123_-xyz", sent.Value.Share!.Token);
        Assert.Equal("One More Time", sent.Value.Share.Title);
    }

    [Fact]
    public async Task Send_to_several_people_files_one_message_in_each_direct_chat()
    {
        var options = await SeedAsync(withSongs: true);
        var queue = new RecordingNotificationQueue();
        await using var db = TestChat.Db(options);

        var outcome = await TestChat.Service(db, queue: queue).SendToPeopleAsync(
            Admin, new SendToPeopleRequest([AliceId, BobId, AliceId], "For you", null, 1, "song"), CancellationToken.None);

        Assert.Null(outcome.Error);
        Assert.Equal(2, outcome.Value!.Messages.Count);
        Assert.Equal(2, await db.ChatConversations.CountAsync());
        Assert.Equal(2, queue.Enqueued.Count);
        Assert.Equal(1, await db.SongShares.CountAsync());
    }

    [Fact]
    public async Task Send_to_refuses_anyone_the_sender_cannot_reach()
    {
        var options = await SeedAsync();
        await using var db = TestChat.Db(options);

        var outcome = await TestChat.Service(db).SendToPeopleAsync(
            Alice, new SendToPeopleRequest([TestUsers.OwnerId, BobId], "hi", null, null, null), CancellationToken.None);

        Assert.Equal(404, outcome.Error?.StatusCode);
        Assert.Empty(await db.ChatMessages.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Two_first_messages_share_one_conversation()
    {
        var options = await SeedAsync();
        await using var adminDb = TestChat.Db(options);
        await using var aliceDb = TestChat.Db(options);

        var a = await TestChat.Service(adminDb).StartAsync(Admin, AliceId, CancellationToken.None);
        var b = await TestChat.Service(aliceDb).StartAsync(Alice, TestUsers.OwnerId, CancellationToken.None);

        Assert.Equal(a.Value!.Id, b.Value!.Id);
    }

    // ── Opening someone's share link ────────────────────────────────────────────────────────

    [Fact]
    public async Task Opening_a_share_link_files_it_in_the_visitors_chat_and_only_theirs()
    {
        var options = await SeedAsync(withSongs: true);
        var share = await SeedShareAsync(options);
        var hub = new ChatHub();
        using var aliceStream = hub.Subscribe(AliceId);

        await using (var aliceDb = TestChat.Db(options))
        {
            var chat = TestChat.Service(aliceDb, hub);
            Assert.True(await chat.RecordShareOpenedAsync(share, AliceId, CancellationToken.None));
            Assert.False(await chat.RecordShareOpenedAsync(share, AliceId, CancellationToken.None)); // once per person

            var conversation = Assert.Single(await chat.ListConversationsAsync(Alice, CancellationToken.None));
            Assert.Equal(0, conversation.UnreadCount); // they are looking at it
            var message = conversation.LastMessage!;
            Assert.Equal("shareOpened", message.Kind);
            Assert.False(message.Mine);
            Assert.Equal("You’re seeing this message because you opened the link shared by Owner.", message.Notice);
            Assert.Equal("One More Time", message.Share!.Title);
            Assert.Equal(conversation.Id, (await chat.GetShareContextAsync(Alice, share.Token, CancellationToken.None))!.ConversationId);
        }
        Assert.True(aliceStream.Reader.TryRead(out _));

        // The owner is not told who opened their link.
        await using var adminDb = TestChat.Db(options);
        var adminChat = TestChat.Service(adminDb);
        Assert.Empty(await adminChat.ListConversationsAsync(Admin, CancellationToken.None));
        Assert.Equal(0, (await adminChat.GetUnreadAsync(Admin, CancellationToken.None)).Total);
        await using var adminView = TestChat.As(options, Admin);
        Assert.Empty(await adminView.ChatMessages.ToListAsync());
        await using var aliceView = TestChat.As(options, Alice);
        Assert.Single(await aliceView.ChatMessages.ToListAsync());
    }

    [Fact]
    public async Task The_owner_sees_the_conversation_once_the_visitor_writes_but_never_the_open()
    {
        var options = await SeedAsync(withSongs: true);
        var share = await SeedShareAsync(options);
        await using (var aliceDb = TestChat.Db(options))
        {
            var chat = TestChat.Service(aliceDb);
            await chat.RecordShareOpenedAsync(share, AliceId, CancellationToken.None);
            var conversationId = (await chat.ListConversationsAsync(Alice, CancellationToken.None)).Single().Id;
            await chat.SendAsync(Alice, conversationId, new SendChatMessageRequest("Love this one!", null, null, null), CancellationToken.None);
        }

        await using var adminDb = TestChat.Db(options);
        var adminChat = TestChat.Service(adminDb);
        var conversation = Assert.Single(await adminChat.ListConversationsAsync(Admin, CancellationToken.None));
        Assert.Equal(1, conversation.UnreadCount);
        var messages = (await adminChat.ListMessagesAsync(Admin, conversation.Id, null, null, null, CancellationToken.None)).Value!.Messages;
        Assert.Equal(["Love this one!"], messages.Select(m => m.Text));
    }

    [Fact]
    public async Task A_link_already_sent_in_the_chat_is_not_filed_again_when_opened()
    {
        var options = await SeedAsync(withSongs: true);
        await using (var adminDb = TestChat.Db(options))
        {
            var chat = TestChat.Service(adminDb);
            var id = (await chat.StartAsync(Admin, AliceId, CancellationToken.None)).Value!.Id;
            await chat.SendAsync(Admin, id, new SendChatMessageRequest(null, null, 1, "song"), CancellationToken.None);
        }

        await using var db = TestChat.Db(options);
        var share = await db.SongShares.IgnoreQueryFilters().SingleAsync();
        Assert.False(await TestChat.Service(db).RecordShareOpenedAsync(share, AliceId, CancellationToken.None));
    }

    [Fact]
    public async Task The_owners_own_open_and_the_demo_account_file_nothing()
    {
        var options = await SeedAsync(withSongs: true);
        var share = await SeedShareAsync(options);
        await using var db = TestChat.Db(options);
        var chat = TestChat.Service(db);

        Assert.False(await chat.RecordShareOpenedAsync(share, TestUsers.OwnerId, CancellationToken.None));
        Assert.False(await chat.RecordShareOpenedAsync(share, TestUsers.DemoId, CancellationToken.None));
        Assert.False(await chat.RecordShareOpenedAsync(share, CarolId, CancellationToken.None)); // disabled
        Assert.Empty(await db.ChatConversations.IgnoreQueryFilters().ToListAsync());
    }

    // ── Seeding ─────────────────────────────────────────────────────────────────────────────

    private static async Task<DbContextOptions<MusicHoarderDbContext>> SeedAsync(bool withSongs = false)
    {
        var options = TestChat.NewOptions();
        await using var seed = TestChat.Db(options);
        seed.Users.AddRange(
            TestChat.UserRow(TestUsers.OwnerId, "owner@test.local", UserRole.Admin, "Owner"),
            TestChat.UserRow(TestUsers.DemoId, "demo@test.local", UserRole.Demo, "Demo"),
            TestChat.UserRow(AliceId, "alice@test.local", UserRole.Member, "Alice"),
            TestChat.UserRow(BobId, "bob@test.local", UserRole.Member),
            TestChat.UserRow(CarolId, "carol@test.local", UserRole.Member, "Carol", disabled: true));
        if (withSongs)
        {
            seed.Songs.Add(new SongMetadata
            {
                Id = 1,
                OwnerUserId = TestUsers.OwnerId,
                SourcePath = "/music/daft/one-more-time.flac",
                FileSizeBytes = 1000,
                FileName = "one-more-time.flac",
                Extension = ".flac",
                IndexedAtUtc = DateTime.UtcNow,
                Title = "One More Time",
                Artist = "Daft Punk",
                AlbumArtist = "Daft Punk",
                Album = "Discovery",
                Year = 2001,
                SpotifyId = "SpotifyTrack0001",
                LastModifiedUtc = DateTime.UtcNow,
            });
        }
        await seed.SaveChangesAsync();
        return options;
    }

    private static async Task<SongShare> SeedShareAsync(DbContextOptions<MusicHoarderDbContext> options)
    {
        await using var seed = TestChat.Db(options);
        var share = new SongShare
        {
            OwnerUserId = TestUsers.OwnerId,
            SongId = 1,
            Token = "public-link-token",
            Scope = ShareScope.Song,
            CreatedAtUtc = DateTime.UtcNow,
        };
        seed.SongShares.Add(share);
        await seed.SaveChangesAsync();
        return share;
    }
}
