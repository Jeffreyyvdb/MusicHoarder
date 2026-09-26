namespace MusicHoarder.Api.Chat;

/// <summary>
/// Someone you can chat with. <paramref name="Email"/> is sent to administrators only (they manage
/// the accounts and already see it on the People page); everyone else gets the name.
/// </summary>
public sealed record ChatPersonDto(Guid Id, string Name, string? Email, bool IsAdmin);

/// <summary>One conversation as its viewer sees it.</summary>
/// <param name="Members">The other people in it (never the viewer).</param>
/// <param name="PeerLastReadAtUtc">In a direct chat, how far the other person has read — what a
/// "Seen" under your last message is decided from.</param>
public sealed record ChatConversationDto(
    Guid Id,
    IReadOnlyList<ChatPersonDto> Members,
    ChatMessageDto? LastMessage,
    int UnreadCount,
    DateTime LastMessageAtUtc,
    DateTime? LastReadAtUtc,
    DateTime? PeerLastReadAtUtc);

/// <summary>A song or album carried by a share link. <paramref name="Token"/> is null once revoked.</summary>
/// <param name="Scope"><c>Song</c> or <c>Album</c>.</param>
/// <param name="OwnedByViewer">The viewer owns the song, so their client can play it from their own
/// library instead of through the link.</param>
public sealed record ChatShareDto(
    string? Token,
    string Scope,
    int SongId,
    string Title,
    string? Artist,
    string? Album,
    int? Year,
    bool HasCover,
    bool Revoked,
    bool OwnedByViewer,
    string? OwnerName);

/// <param name="Provider"><c>spotify</c>, <c>youtube</c>, or null.</param>
/// <param name="LibrarySongId">A Spotify track the viewer already has: the song to play instead.</param>
public sealed record ChatLinkDto(
    string Url,
    string? Provider,
    string? Kind,
    string? Title,
    string? Subtitle,
    string? ImageUrl,
    int? LibrarySongId);

/// <param name="Kind"><c>text</c>, <c>share</c>, <c>link</c> or <c>shareOpened</c>.</param>
/// <param name="Notice">A server-written line shown with the message — for a share-link open, why it is
/// there — so every client says the same thing.</param>
public sealed record ChatMessageDto(
    long Id,
    Guid ConversationId,
    Guid SenderId,
    bool Mine,
    string Kind,
    string? Text,
    ChatShareDto? Share,
    ChatLinkDto? Link,
    string? Notice,
    DateTime CreatedAtUtc);

public sealed record ChatMessagePageDto(IReadOnlyList<ChatMessageDto> Messages, bool HasMore);

/// <summary>
/// What to send. Any combination of text and one attachment: a song or album of the sender's
/// (<paramref name="SongId"/> + <paramref name="Scope"/>), or a link (<paramref name="Url"/>). With
/// neither, the first link in the text becomes the attachment, the way a messaging app previews a
/// pasted URL.
/// </summary>
public sealed record SendChatMessageRequest(string? Text, string? Url, int? SongId, string? Scope);

/// <summary>Send the same thing to several people at once ("Send to…", the share sheet).</summary>
public sealed record SendToPeopleRequest(IReadOnlyList<Guid> RecipientIds, string? Text, string? Url, int? SongId, string? Scope);

public sealed record SendToPeopleResponse(IReadOnlyList<ChatMessageDto> Messages);

public sealed record StartConversationRequest(Guid UserId);

/// <summary>
/// A share link the viewer opened, and the conversation it was put in: what the share page shows
/// a signed-in visitor ("This is in your chat with …").
/// </summary>
public sealed record ChatShareContextDto(Guid ConversationId, string OwnerName);

public sealed record ChatUnreadDto(int Total, int Conversations);

/// <summary>SSE event names on <c>/api/chat/stream</c>.</summary>
public static class ChatEventTypes
{
    public const string Ready = "ready";
    public const string Message = "message";
    public const string Read = "read";
    public const string Ping = "ping";
}

public sealed record ChatMessageEvent(Guid ConversationId, long MessageId);

public sealed record ChatReadEvent(Guid ConversationId, Guid UserId, DateTime LastReadAtUtc);

/// <summary>A failure a chat request answers with.</summary>
public sealed record ChatError(int StatusCode, string Code, string Message)
{
    public static ChatError NotFound(string what = "Conversation") => new(404, "not_found", $"{what} not found.");
    public static ChatError Invalid(string code, string message) => new(400, code, message);
}

/// <summary>A chat operation's result: the value, or the error to answer with.</summary>
public readonly record struct ChatOutcome<T>(T? Value, ChatError? Error)
{
    public static implicit operator ChatOutcome<T>(T value) => new(value, null);
    public static implicit operator ChatOutcome<T>(ChatError error) => new(default, error);
}
