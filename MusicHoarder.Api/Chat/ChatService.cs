using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Sharing;

namespace MusicHoarder.Api.Chat;

/// <summary>
/// Chat between the accounts of one instance: direct conversations, text, songs and albums from the
/// sender's library, and links from anywhere (Spotify and YouTube get a card).
///
/// <para>
/// <b>Who you can talk to.</b> An administrator can message every account (they invited them all).
/// Anyone else can message the administrators, the people they share a library grant with in either
/// direction, and anyone who has already written to them. Opening someone's share link while signed
/// in counts as the start of a conversation too — see <see cref="RecordShareOpenedAsync"/>.
/// </para>
///
/// <para>
/// <b>Songs travel as share links.</b> A song or album sent in a chat is carried by the same
/// <see cref="SongShare"/> "Share link…" makes (reused when one exists), so the recipient can play it
/// whatever they can otherwise see of the sender's library — and the link is listed, counted and
/// revocable on the sender's Share links page like any other. A revoked link leaves the message in
/// place, saying it is no longer available.
/// </para>
///
/// <para>
/// <b>Reads.</b> Every query here names its scope explicitly — the caller's conversations, the
/// messages they may see, the songs they own — and reads with <c>IgnoreQueryFilters()</c> rather than
/// leaning on the request's ambient filters, so the service means the same thing whichever context it
/// is handed (a share-link beacon, a test). The DbContext's chat filters stay as the backstop for any
/// other code that reads these tables. Share and song rows belong to their owner's tenancy and are
/// only ever read by id for messages the caller can already see.
/// </para>
/// </summary>
public sealed class ChatService(
    MusicHoarderDbContext db,
    ChatHub hub,
    IChatNotificationQueue notifications,
    ILinkPreviewService previews,
    ChatSendLimiter limiter,
    IOptions<FrontendOptions> frontend,
    TimeProvider time)
{
    public const int MaxTextLength = 4000;
    public const int MaxRecipients = 25;
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    // ── People ──────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ChatPersonDto>> ListPeopleAsync(CurrentUser me, CancellationToken ct)
    {
        var ids = await ContactIdsAsync(me, ct);
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToListAsync(ct);
        return users
            .Select(u => ToPerson(u, me))
            .OrderByDescending(p => p.IsAdmin)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The accounts <paramref name="me"/> may start a conversation with.</summary>
    internal async Task<HashSet<Guid>> ContactIdsAsync(CurrentUser me, CancellationToken ct)
    {
        var reachable = db.Users.AsNoTracking()
            .Where(u => !u.IsDisabled && u.Role != UserRole.Demo && u.Id != me.Id);
        if (me.IsAdmin)
            return (await reachable.Select(u => u.Id).ToListAsync(ct)).ToHashSet();

        var admins = await reachable.Where(u => u.Role == UserRole.Admin).Select(u => u.Id).ToListAsync(ct);

        var grants = await db.LibraryShareGrants.IgnoreQueryFilters().AsNoTracking()
            .Where(g => g.RevokedAtUtc == null && (g.OwnerUserId == me.Id || g.GranteeUserId == me.Id))
            .Select(g => g.OwnerUserId == me.Id ? g.GranteeUserId : g.OwnerUserId)
            .ToListAsync(ct);

        // Anyone in a conversation where the caller can see at least one message. A conversation
        // that holds only a share-link open the other side cannot see does not count for them.
        var visibleConversations = VisibleMessages(me.Id).Select(m => m.ConversationId).Distinct();
        var talkedTo = await db.ChatParticipants.AsNoTracking()
            .Where(p => visibleConversations.Contains(p.ConversationId) && p.UserId != me.Id)
            .Select(p => p.UserId)
            .ToListAsync(ct);

        var candidates = admins.Concat(grants).Concat(talkedTo).ToHashSet();
        return (await reachable.Where(u => candidates.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct)).ToHashSet();
    }

    // ── Conversations ───────────────────────────────────────────────────────────────────────

    public async Task<ChatOutcome<ChatConversationDto>> StartAsync(CurrentUser me, Guid otherUserId, CancellationToken ct)
    {
        if (!(await ContactIdsAsync(me, ct)).Contains(otherUserId))
            return ChatError.NotFound("Person");

        var conversation = await GetOrCreateDirectAsync(me.Id, otherUserId, ct);
        var dto = await GetConversationAsync(me, conversation.Id, ct);
        return dto is null ? ChatError.NotFound() : dto;
    }

    public async Task<IReadOnlyList<ChatConversationDto>> ListConversationsAsync(CurrentUser me, CancellationToken ct) =>
        await LoadConversationsAsync(me, onlyId: null, includeEmpty: false, ct);

    /// <summary>One conversation, including a new one nobody has written in yet.</summary>
    public async Task<ChatConversationDto?> GetConversationAsync(CurrentUser me, Guid id, CancellationToken ct) =>
        (await LoadConversationsAsync(me, id, includeEmpty: true, ct)).FirstOrDefault();

    public async Task<ChatUnreadDto> GetUnreadAsync(CurrentUser me, CancellationToken ct)
    {
        var counts = await UnreadCountsAsync(me.Id, conversationIds: null, ct);
        return new ChatUnreadDto(counts.Values.Sum(), counts.Count(c => c.Value > 0));
    }

    private async Task<List<ChatConversationDto>> LoadConversationsAsync(
        CurrentUser me, Guid? onlyId, bool includeEmpty, CancellationToken ct)
    {
        var conversations = await db.ChatConversations.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Participants.Any(p => p.UserId == me.Id))
            .Where(c => onlyId == null || c.Id == onlyId)
            .Select(c => new
            {
                c.Id,
                c.CreatedAtUtc,
                Participants = c.Participants.Select(p => new { p.UserId, p.LastReadAtUtc }).ToList(),
            })
            .ToListAsync(ct);
        if (conversations.Count == 0) return [];

        var ids = conversations.Select(c => c.Id).ToList();
        var lastIds = await VisibleMessages(me.Id)
            .Where(m => ids.Contains(m.ConversationId))
            .GroupBy(m => m.ConversationId)
            .Select(g => g.Max(m => m.Id))
            .ToListAsync(ct);
        var lastMessages = await db.ChatMessages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => lastIds.Contains(m.Id))
            .ToListAsync(ct);
        var projected = (await ProjectAsync(me, lastMessages, ct)).ToDictionary(m => m.ConversationId);
        var unread = await UnreadCountsAsync(me.Id, ids, ct);

        var peopleIds = conversations.SelectMany(c => c.Participants.Select(p => p.UserId)).Where(id => id != me.Id).Distinct().ToList();
        var people = await db.Users.AsNoTracking().Where(u => peopleIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);

        return conversations
            .Where(c => includeEmpty || projected.ContainsKey(c.Id))
            .Select(c =>
            {
                projected.TryGetValue(c.Id, out var last);
                var mine = c.Participants.FirstOrDefault(p => p.UserId == me.Id);
                var others = c.Participants.Where(p => p.UserId != me.Id).ToList();
                return new ChatConversationDto(
                    c.Id,
                    others.Where(p => people.ContainsKey(p.UserId)).Select(p => ToPerson(people[p.UserId], me)).ToList(),
                    last,
                    unread.GetValueOrDefault(c.Id),
                    last?.CreatedAtUtc ?? c.CreatedAtUtc,
                    mine?.LastReadAtUtc,
                    others.Count == 1 ? others[0].LastReadAtUtc : null);
            })
            .OrderByDescending(c => c.LastMessageAtUtc)
            .ToList();
    }

    /// <summary>Unread messages per conversation: others' messages the viewer can see, after their read mark.</summary>
    private async Task<Dictionary<Guid, int>> UnreadCountsAsync(Guid me, IReadOnlyList<Guid>? conversationIds, CancellationToken ct)
    {
        var query =
            from m in VisibleMessages(me)
            join p in db.ChatParticipants.AsNoTracking() on m.ConversationId equals p.ConversationId
            where p.UserId == me && m.SenderUserId != me && (p.LastReadAtUtc == null || m.CreatedAtUtc > p.LastReadAtUtc)
            select m.ConversationId;
        if (conversationIds is not null)
            query = query.Where(id => conversationIds.Contains(id));

        return await query
            .GroupBy(id => id)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
    }

    // ── Messages ────────────────────────────────────────────────────────────────────────────

    public async Task<ChatOutcome<ChatMessagePageDto>> ListMessagesAsync(
        CurrentUser me, Guid conversationId, long? before, long? after, int? limit, CancellationToken ct)
    {
        if (!await IsParticipantAsync(me.Id, conversationId, ct)) return ChatError.NotFound();
        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);

        var visible = VisibleMessages(me.Id).Where(m => m.ConversationId == conversationId);
        List<ChatMessage> page;
        bool hasMore;
        if (after is { } afterId)
        {
            page = await visible.Where(m => m.Id > afterId).OrderBy(m => m.Id).Take(take).ToListAsync(ct);
            hasMore = false;
        }
        else
        {
            if (before is { } beforeId) visible = visible.Where(m => m.Id < beforeId);
            var newestFirst = await visible.OrderByDescending(m => m.Id).Take(take + 1).ToListAsync(ct);
            hasMore = newestFirst.Count > take;
            page = newestFirst.Take(take).Reverse().ToList();
        }

        return new ChatMessagePageDto(await ProjectAsync(me, page, ct), hasMore);
    }

    public async Task<ChatOutcome<ChatMessageDto>> SendAsync(
        CurrentUser me, Guid conversationId, SendChatMessageRequest request, CancellationToken ct)
    {
        var participants = await db.ChatParticipants
            .Where(p => p.ConversationId == conversationId)
            .ToListAsync(ct);
        if (participants.All(p => p.UserId != me.Id)) return ChatError.NotFound();

        var otherIds = participants.Where(p => p.UserId != me.Id).Select(p => p.UserId).ToList();
        if (!await db.Users.AnyAsync(u => otherIds.Contains(u.Id) && !u.IsDisabled, ct))
            return new ChatError(409, "recipient_unavailable", "This person can no longer receive messages.");

        if (!limiter.TryAcquire(me.Id))
            return new ChatError(429, "chat_rate_limited", "You are sending messages too quickly. Wait a moment.");

        var built = await BuildAsync(me, request.Text, request.Url, request.SongId, request.Scope, ct);
        if (built.Error is { } error) return error;

        var message = built.Value!.CreateFor(conversationId, me.Id, Now());
        db.ChatMessages.Add(message);
        MarkReadOnSend(participants.First(p => p.UserId == me.Id), message.CreatedAtUtc);
        await TouchConversationAsync(conversationId, message.CreatedAtUtc, ct);
        await db.SaveChangesAsync(ct);

        Announce(message, participants.Select(p => p.UserId));
        return (await ProjectAsync(me, [message], ct))[0];
    }

    /// <summary>The same message to several people, each in their direct conversation with the sender.</summary>
    public async Task<ChatOutcome<SendToPeopleResponse>> SendToPeopleAsync(
        CurrentUser me, SendToPeopleRequest request, CancellationToken ct)
    {
        var recipients = (request.RecipientIds ?? []).Where(id => id != me.Id).Distinct().ToList();
        if (recipients.Count == 0)
            return ChatError.Invalid("no_recipients", "Pick at least one person.");
        if (recipients.Count > MaxRecipients)
            return ChatError.Invalid("too_many_recipients", $"Send to at most {MaxRecipients} people at once.");

        var contacts = await ContactIdsAsync(me, ct);
        if (recipients.Any(id => !contacts.Contains(id)))
            return ChatError.NotFound("Person");

        if (!limiter.TryAcquire(me.Id, recipients.Count))
            return new ChatError(429, "chat_rate_limited", "You are sending messages too quickly. Wait a moment.");

        var built = await BuildAsync(me, request.Text, request.Url, request.SongId, request.Scope, ct);
        if (built.Error is { } error) return error;

        var sent = new List<(ChatMessage Message, List<Guid> Participants)>();
        foreach (var recipient in recipients)
        {
            var conversation = await GetOrCreateDirectAsync(me.Id, recipient, ct);
            var message = built.Value!.CreateFor(conversation.Id, me.Id, Now());
            db.ChatMessages.Add(message);
            var mine = await db.ChatParticipants.FirstAsync(p => p.ConversationId == conversation.Id && p.UserId == me.Id, ct);
            MarkReadOnSend(mine, message.CreatedAtUtc);
            await TouchConversationAsync(conversation.Id, message.CreatedAtUtc, ct);
            sent.Add((message, [me.Id, recipient]));
        }
        await db.SaveChangesAsync(ct);

        foreach (var (message, members) in sent)
            Announce(message, members);
        return new SendToPeopleResponse(await ProjectAsync(me, sent.Select(s => s.Message).ToList(), ct));
    }

    public async Task<ChatOutcome<ChatReadEvent>> MarkReadAsync(CurrentUser me, Guid conversationId, CancellationToken ct)
    {
        var participants = await db.ChatParticipants.Where(p => p.ConversationId == conversationId).ToListAsync(ct);
        var mine = participants.FirstOrDefault(p => p.UserId == me.Id);
        if (mine is null) return ChatError.NotFound();

        var now = Now();
        if (mine.LastReadAtUtc is null || mine.LastReadAtUtc < now)
        {
            mine.LastReadAtUtc = now;
            await db.SaveChangesAsync(ct);
        }

        var evt = new ChatReadEvent(conversationId, me.Id, mine.LastReadAtUtc!.Value);
        foreach (var p in participants)
            hub.Publish(p.UserId, new ChatStreamEvent(ChatEventTypes.Read, evt));
        return evt;
    }

    /// <summary>Which conversation a share link the caller opened was put in, if any.</summary>
    public async Task<ChatShareContextDto?> GetShareContextAsync(CurrentUser me, string token, CancellationToken ct)
    {
        var share = await db.SongShares.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Token == token && s.RevokedAtUtc == null, ct);
        if (share is null || share.OwnerUserId == me.Id) return null;

        var conversationId = await VisibleMessages(me.Id)
            .Where(m => m.ShareId == share.Id)
            .OrderByDescending(m => m.Id)
            .Select(m => (Guid?)m.ConversationId)
            .FirstOrDefaultAsync(ct);
        if (conversationId is null) return null;

        var owner = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == share.OwnerUserId, ct);
        return owner is null ? null : new ChatShareContextDto(conversationId.Value, NameOf(owner));
    }

    // ── Share-link opens ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A signed-in account opened someone's public share link: put the song or album in their chat
    /// with the link's owner, with a line saying why it is there — the way Spotify, TikTok and
    /// YouTube file a link you opened under the person who sent it.
    ///
    /// <para>
    /// The message is shown to the person who opened the link and to nobody else. The owner is not
    /// told who opened it: a public link gets posted where anyone can click it, and clicking it
    /// should not announce you. The owner learns of the conversation only when the other person
    /// writes in it. Once per (link, person); a demo session, the owner's own open and disabled or
    /// demo owners are skipped. Never pushed — the person is looking at the page.
    /// </para>
    /// </summary>
    /// <returns>True when a message was added.</returns>
    public async Task<bool> RecordShareOpenedAsync(SongShare share, Guid visitorId, CancellationToken ct)
    {
        if (visitorId == share.OwnerUserId || visitorId == Guid.Empty) return false;

        var accounts = await db.Users.AsNoTracking()
            .Where(u => (u.Id == visitorId || u.Id == share.OwnerUserId) && !u.IsDisabled && u.Role != UserRole.Demo)
            .CountAsync(ct);
        if (accounts != 2) return false;

        var conversation = await GetOrCreateDirectAsync(visitorId, share.OwnerUserId, ct);
        var already = await db.ChatMessages.IgnoreQueryFilters().AnyAsync(
            m => m.ConversationId == conversation.Id
                && m.ShareId == share.Id
                && (m.VisibleToUserId == null || m.VisibleToUserId == visitorId),
            ct);
        if (already) return false;

        var now = Now();
        var message = new ChatMessage
        {
            ConversationId = conversation.Id,
            SenderUserId = share.OwnerUserId,
            Kind = ChatMessageKind.ShareOpened,
            ShareId = share.Id,
            VisibleToUserId = visitorId,
            CreatedAtUtc = now,
        };
        db.ChatMessages.Add(message);
        // Seen as it was made: the visitor is on the page it describes, so it is no unread badge.
        var visitor = await db.ChatParticipants.FirstAsync(p => p.ConversationId == conversation.Id && p.UserId == visitorId, ct);
        visitor.LastReadAtUtc = now;
        await db.SaveChangesAsync(ct);

        hub.Publish(visitorId, new ChatStreamEvent(ChatEventTypes.Message, new ChatMessageEvent(conversation.Id, message.Id)));
        return true;
    }

    // ── Building a message ──────────────────────────────────────────────────────────────────

    /// <summary>The parts of a message before it is addressed: validated once, sent to any number of conversations.</summary>
    internal sealed record Draft(
        ChatMessageKind Kind,
        string? Text,
        int? ShareId,
        ParsedChatLink? Link,
        LinkPreview? Preview)
    {
        public ChatMessage CreateFor(Guid conversationId, Guid senderId, DateTime nowUtc) => new()
        {
            ConversationId = conversationId,
            SenderUserId = senderId,
            Kind = Kind,
            Text = Text,
            ShareId = ShareId,
            LinkUrl = Link?.Url,
            LinkProvider = Link?.Provider,
            LinkKind = Link?.Kind,
            LinkTitle = Preview?.Title,
            LinkSubtitle = Preview?.Subtitle,
            LinkImageUrl = Preview?.ImageUrl,
            CreatedAtUtc = nowUtc,
        };
    }

    private async Task<ChatOutcome<Draft>> BuildAsync(
        CurrentUser me, string? rawText, string? rawUrl, int? songId, string? scope, CancellationToken ct)
    {
        var text = string.IsNullOrWhiteSpace(rawText) ? null : rawText.Trim();
        if (text is { Length: > MaxTextLength })
            return ChatError.Invalid("text_too_long", $"A message holds at most {MaxTextLength} characters.");

        if (songId is { } id)
        {
            // The query filter scopes songs to the caller, so this is also the ownership check: you
            // can send what is in your own library, and nobody else's.
            var owned = await db.Songs.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(s => s.Id == id && s.OwnerUserId == me.Id && s.DeletedAtUtc == null, ct);
            if (!owned) return ChatError.NotFound("Song");

            var share = await SongShareFactory.GetOrCreateAsync(db, me.Id, id, SongShareFactory.ParseScope(scope), Now(), ct);
            return new Draft(ChatMessageKind.Share, text, share.Id, null, null);
        }

        var url = string.IsNullOrWhiteSpace(rawUrl) ? ChatLinkParser.FindFirstUrl(text) : rawUrl.Trim();
        if (url is null)
        {
            return text is null
                ? ChatError.Invalid("empty_message", "Write something, or attach a song or a link.")
                : new Draft(ChatMessageKind.Text, text, null, null, null);
        }
        if (!ChatLinkParser.IsHttpUrl(url))
            return ChatError.Invalid("invalid_url", "That link is not a web address.");

        // A link that is the whole message is shown once, as its card.
        if (text is not null && string.Equals(text, url, StringComparison.Ordinal)) text = null;

        var parsed = ChatLinkParser.Classify(url, OwnHost());
        if (parsed is { Provider: "musichoarder", Id: { } token })
        {
            // One of this instance's own share links: sent as the playable card it stands for.
            var share = await db.SongShares.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(s => s.Token == token && s.RevokedAtUtc == null, ct);
            if (share is not null) return new Draft(ChatMessageKind.Share, text, share.Id, null, null);
            parsed = parsed with { Provider = null, Kind = null, Id = null };
        }

        parsed = await previews.ResolveAsync(parsed, ct);
        var preview = await previews.PreviewAsync(parsed, ct);
        return new Draft(ChatMessageKind.Link, text, null, parsed, preview);
    }

    // ── Projection ──────────────────────────────────────────────────────────────────────────

    private sealed record ShareRow(
        int Id,
        string Token,
        ShareScope Scope,
        int SongId,
        Guid OwnerUserId,
        DateTime? RevokedAtUtc,
        string Title,
        string? Artist,
        string? AlbumArtist,
        string? Album,
        int? Year,
        bool HasCover,
        DateTime? SongDeletedAtUtc);

    /// <summary>Messages as <paramref name="me"/> sees them. The caller has already checked they may see them.</summary>
    internal async Task<List<ChatMessageDto>> ProjectAsync(CurrentUser me, IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        if (messages.Count == 0) return [];

        var shareIds = messages.Where(m => m.ShareId != null).Select(m => m.ShareId!.Value).Distinct().ToList();
        var shares = shareIds.Count == 0
            ? new Dictionary<int, ShareRow>()
            : await db.SongShares.IgnoreQueryFilters().AsNoTracking()
                .Where(s => shareIds.Contains(s.Id))
                .Select(s => new ShareRow(
                    s.Id, s.Token, s.Scope, s.SongId, s.OwnerUserId, s.RevokedAtUtc,
                    s.Song!.Title ?? s.Song.FileName, s.Song.Artist, s.Song.AlbumArtist, s.Song.Album,
                    s.Song.Year, s.Song.HasCoverArt, s.Song.DeletedAtUtc))
                .ToDictionaryAsync(s => s.Id, ct);

        var userIds = messages.Select(m => m.SenderUserId).Concat(shares.Values.Select(s => s.OwnerUserId)).Distinct().ToList();
        var names = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, NameOf, ct);

        // A Spotify track the viewer already has plays from their own library instead.
        var spotifyIds = messages
            .Where(m => m is { Kind: ChatMessageKind.Link, LinkProvider: "spotify", LinkKind: "track", LinkUrl: not null })
            .Select(m => ChatLinkParser.Classify(m.LinkUrl!).Id)
            .OfType<string>()
            .Distinct()
            .ToList();
        var inLibrary = spotifyIds.Count == 0
            ? new Dictionary<string, int>()
            : (await db.Songs.IgnoreQueryFilters().AsNoTracking()
                    .Where(s => s.OwnerUserId == me.Id && s.DeletedAtUtc == null && !s.IsDuplicate
                        && s.SpotifyId != null && spotifyIds.Contains(s.SpotifyId))
                    .OrderByDescending(s => s.LibraryBuildStatus == LibraryBuildStatus.Done)
                    .ThenBy(s => s.Id)
                    .Select(s => new { s.SpotifyId, s.Id })
                    .ToListAsync(ct))
                .GroupBy(s => s.SpotifyId!)
                .ToDictionary(g => g.Key, g => g.First().Id);

        return messages.Select(m =>
        {
            ChatShareDto? share = null;
            if (m.ShareId is { } shareId && shares.TryGetValue(shareId, out var s))
            {
                var revoked = s.RevokedAtUtc != null || s.SongDeletedAtUtc != null;
                var isAlbum = s.Scope == ShareScope.Album && !string.IsNullOrWhiteSpace(s.Album);
                share = new ChatShareDto(
                    revoked ? null : s.Token,
                    isAlbum ? "Album" : "Song",
                    s.SongId,
                    isAlbum ? s.Album! : s.Title,
                    isAlbum ? s.AlbumArtist ?? s.Artist : s.Artist,
                    s.Album,
                    s.Year,
                    s.HasCover && !revoked,
                    revoked,
                    s.OwnerUserId == me.Id,
                    names.GetValueOrDefault(s.OwnerUserId));
            }

            ChatLinkDto? link = null;
            if (m.LinkUrl is not null)
            {
                int? librarySongId = null;
                if (m is { LinkProvider: "spotify", LinkKind: "track" }
                    && ChatLinkParser.Classify(m.LinkUrl).Id is { } trackId
                    && inLibrary.TryGetValue(trackId, out var songId))
                    librarySongId = songId;
                link = new ChatLinkDto(m.LinkUrl, m.LinkProvider, m.LinkKind, m.LinkTitle, m.LinkSubtitle, m.LinkImageUrl, librarySongId);
            }

            string? notice = m.Kind == ChatMessageKind.ShareOpened
                ? $"You’re seeing this message because you opened the link shared by {names.GetValueOrDefault(m.SenderUserId) ?? "someone"}."
                : null;

            return new ChatMessageDto(
                m.Id,
                m.ConversationId,
                m.SenderUserId,
                m.SenderUserId == me.Id,
                KindName(m.Kind),
                m.Text,
                share,
                link,
                notice,
                DateTime.SpecifyKind(m.CreatedAtUtc, DateTimeKind.Utc));
        }).ToList();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Messages <paramref name="userId"/> may see: in their conversations, and addressed to everyone or to them.</summary>
    private IQueryable<ChatMessage> VisibleMessages(Guid userId) =>
        db.ChatMessages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => (m.VisibleToUserId == null || m.VisibleToUserId == userId)
                && db.ChatParticipants.Any(p => p.ConversationId == m.ConversationId && p.UserId == userId));

    private Task<bool> IsParticipantAsync(Guid userId, Guid conversationId, CancellationToken ct) =>
        db.ChatParticipants.AnyAsync(p => p.ConversationId == conversationId && p.UserId == userId, ct);

    /// <summary>
    /// The direct conversation between two accounts, made on first use. The unique
    /// <see cref="ChatConversation.DirectKey"/> settles two first messages racing each other: the
    /// loser's insert fails and it reads the winner's.
    /// </summary>
    internal async Task<ChatConversation> GetOrCreateDirectAsync(Guid a, Guid b, CancellationToken ct)
    {
        var key = ChatConversation.DirectKeyFor(a, b);
        var existing = await db.ChatConversations.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.DirectKey == key, ct);
        if (existing is not null) return existing;

        var now = Now();
        var conversation = new ChatConversation
        {
            Id = Guid.NewGuid(),
            DirectKey = key,
            CreatedAtUtc = now,
            LastMessageAtUtc = now,
            Participants =
            [
                new ChatParticipant { UserId = a, JoinedAtUtc = now },
                new ChatParticipant { UserId = b, JoinedAtUtc = now },
            ],
        };
        db.ChatConversations.Add(conversation);
        try
        {
            await db.SaveChangesAsync(ct);
            return conversation;
        }
        catch (DbUpdateException)
        {
            db.Entry(conversation).State = EntityState.Detached;
            foreach (var p in conversation.Participants) db.Entry(p).State = EntityState.Detached;
            return await db.ChatConversations.IgnoreQueryFilters().FirstAsync(c => c.DirectKey == key, ct);
        }
    }

    private async Task TouchConversationAsync(Guid conversationId, DateTime at, CancellationToken ct)
    {
        var conversation = await db.ChatConversations.IgnoreQueryFilters().FirstAsync(c => c.Id == conversationId, ct);
        if (conversation.LastMessageAtUtc < at) conversation.LastMessageAtUtc = at;
    }

    /// <summary>Writing in a conversation reads it: your own message is never unread on your other devices.</summary>
    private static void MarkReadOnSend(ChatParticipant sender, DateTime at)
    {
        if (sender.LastReadAtUtc is null || sender.LastReadAtUtc < at) sender.LastReadAtUtc = at;
    }

    /// <summary>Tell every open stream of the conversation's members, and queue the push for the others.</summary>
    private void Announce(ChatMessage message, IEnumerable<Guid> participantIds)
    {
        var evt = new ChatStreamEvent(ChatEventTypes.Message, new ChatMessageEvent(message.ConversationId, message.Id));
        foreach (var userId in participantIds.Distinct())
        {
            if (message.VisibleToUserId is { } only && only != userId) continue;
            hub.Publish(userId, evt);
        }
        notifications.Enqueue(message.Id, message.CreatedAtUtc);
    }

    private string? OwnHost()
    {
        var origin = frontend.Value.PublicBaseUrl;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    private DateTime Now() => time.GetUtcNow().UtcDateTime;

    internal static ChatPersonDto ToPerson(User user, CurrentUser viewer) =>
        new(user.Id, NameOf(user), viewer.IsAdmin ? user.Email : null, user.Role == UserRole.Admin);

    /// <summary>What to call an account: its display name, else the part of its email before the @.</summary>
    public static string NameOf(User user)
    {
        if (!string.IsNullOrWhiteSpace(user.DisplayName)) return user.DisplayName.Trim();
        var at = user.Email.IndexOf('@');
        return at > 0 ? user.Email[..at] : user.Email;
    }

    public static string KindName(ChatMessageKind kind) => kind switch
    {
        ChatMessageKind.Share => "share",
        ChatMessageKind.Link => "link",
        ChatMessageKind.ShareOpened => "shareOpened",
        _ => "text",
    };
}

/// <summary>
/// Bounds how fast one account can send: a burst of <see cref="PermitsPerMinute"/> messages, refilled
/// every minute. Enough for any conversation, not enough to spam everyone on the instance with
/// "Send to…". A singleton: the limiter state outlives the request.
/// </summary>
public sealed class ChatSendLimiter : IDisposable
{
    public const int PermitsPerMinute = 60;

    private readonly PartitionedRateLimiter<Guid> _limiter = PartitionedRateLimiter.Create<Guid, Guid>(userId =>
        RateLimitPartition.GetFixedWindowLimiter(userId, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = PermitsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

    public bool TryAcquire(Guid userId, int permits = 1)
    {
        using var lease = _limiter.AttemptAcquire(userId, Math.Clamp(permits, 1, PermitsPerMinute));
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
