using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Chat;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;
using MusicHoarder.Api.Tests.Chat;

namespace MusicHoarder.Api.Tests.Chat;

public class ShareOpenConversationPrivacyTests
{
    private static readonly Guid AliceId = TestUsers.FriendId;
    private static readonly Guid BobId = TestUsers.SecondFriendId;

    private static CurrentUser Admin => TestCurrentUserAccessor.OwnerUser;
    private static CurrentUser Alice => TestChat.Member(AliceId, "Alice");

    [Fact]
    public async Task Opening_a_share_link_leaves_the_owners_chat_view_and_stream_unchanged()
    {
        var options = TestChat.NewOptions();
        await using (var seed = TestChat.Db(options))
        {
            seed.Users.AddRange(
                TestChat.UserRow(TestUsers.OwnerId, "owner@test.local", UserRole.Admin, "Owner"),
                TestChat.UserRow(AliceId, "alice@test.local", UserRole.Member, "Alice"),
                TestChat.UserRow(BobId, "bob@test.local", UserRole.Member, "Bob"));
            seed.Songs.Add(new SongMetadata
            {
                Id = 1,
                OwnerUserId = TestUsers.OwnerId,
                SourcePath = "/music/a/one.flac",
                FileSizeBytes = 1000,
                FileName = "one.flac",
                Extension = ".flac",
                IndexedAtUtc = DateTime.UtcNow,
                Title = "One",
                Artist = "Artist",
                AlbumArtist = "Artist",
                Album = "Album",
                Year = 2001,
                SpotifyId = "SpotifyTrack0001",
                LastModifiedUtc = DateTime.UtcNow,
            });
            var shareRow = new SongShare
            {
                OwnerUserId = TestUsers.OwnerId,
                SongId = 1,
                Token = "public-link-token",
                Scope = ShareScope.Song,
                CreatedAtUtc = DateTime.UtcNow,
            };
            seed.SongShares.Add(shareRow);
            await seed.SaveChangesAsync();
        }

        var hub = new ChatHub();
        using var ownerStream = hub.Subscribe(TestUsers.OwnerId);

        await using var aliceDb = TestChat.Db(options);
        var aliceChat = TestChat.Service(aliceDb, hub);
        var share = await aliceDb.SongShares.IgnoreQueryFilters().SingleAsync();
        Assert.True(await aliceChat.RecordShareOpenedAsync(share, AliceId, CancellationToken.None));
        var conversationId = (await aliceChat.ListConversationsAsync(Alice, CancellationToken.None)).Single().Id;
        Assert.Null((await aliceChat.MarkReadAsync(Alice, conversationId, CancellationToken.None)).Error);

        await using var adminDb = TestChat.Db(options);
        var adminChat = TestChat.Service(adminDb, hub);
        var opened = (await adminChat.StartAsync(Admin, AliceId, CancellationToken.None)).Value!;
        var notOpened = (await adminChat.StartAsync(Admin, BobId, CancellationToken.None)).Value!;

        Assert.Null(opened.PeerLastReadAtUtc);
        Assert.Null(opened.LastMessage);
        Assert.Equal(notOpened.PeerLastReadAtUtc, opened.PeerLastReadAtUtc);
        Assert.Equal(notOpened.LastMessageAtUtc, opened.LastMessageAtUtc);
        Assert.False(ownerStream.Reader.TryRead(out _));
    }
}
