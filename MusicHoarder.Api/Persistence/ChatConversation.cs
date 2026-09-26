using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// A chat between accounts on this instance. Only direct (two-person) conversations exist today;
/// <see cref="DirectKey"/> makes "the conversation between A and B" unique, so starting a chat,
/// sending something with "Send to…" and opening someone's share link all land in the same thread.
///
/// <para>
/// Membership lives in <see cref="ChatParticipant"/> rather than two columns so a group chat can be
/// added later without migrating every row. Every read of a conversation names the participant it
/// is for (see <c>Chat.ChatService</c>); the messages carry the query filter.
/// </para>
/// </summary>
public class ChatConversation
{
    public Guid Id { get; set; }

    /// <summary>
    /// <c>"{lower id}:{higher id}"</c> of the two participants — see <see cref="DirectKeyFor"/>.
    /// Unique, which is what stops two racing first messages from opening two threads.
    /// </summary>
    [MaxLength(80)]
    public string? DirectKey { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the newest message was written; what the conversation list sorts by.</summary>
    public DateTime LastMessageAtUtc { get; set; }

    public List<ChatParticipant> Participants { get; set; } = [];

    public List<ChatMessage> Messages { get; set; } = [];

    public static string DirectKeyFor(Guid a, Guid b)
    {
        var (low, high) = a.CompareTo(b) <= 0 ? (a, b) : (b, a);
        return $"{low:N}:{high:N}";
    }
}

/// <summary>One account's membership of a <see cref="ChatConversation"/>, with how far it has read.</summary>
public class ChatParticipant
{
    public Guid ConversationId { get; set; }

    public ChatConversation? Conversation { get; set; }

    public Guid UserId { get; set; }

    public DateTime JoinedAtUtc { get; set; }

    /// <summary>
    /// Everything written at or before this is read. A timestamp rather than a message id because
    /// "read" follows the clock across devices: reading on the laptop quiets the phone.
    /// </summary>
    public DateTime? LastReadAtUtc { get; set; }
}

public enum ChatMessageKind
{
    /// <summary>Just text.</summary>
    Text = 0,

    /// <summary>A song or album from the sender's library, carried by a <see cref="SongShare"/>.</summary>
    Share = 1,

    /// <summary>A link from outside (Spotify, YouTube, anything), with a preview when one was found.</summary>
    Link = 2,

    /// <summary>
    /// The recipient opened a public share link while signed in, and the item was put in their
    /// chats with the link's owner. Written in the owner's name but visible only to the person who
    /// opened it (<see cref="ChatMessage.VisibleToUserId"/>): the owner is not told who opened it.
    /// </summary>
    ShareOpened = 3,
}

/// <summary>
/// One message. The optional parts are flat columns rather than an attachment table: a message
/// carries at most one thing, and the list query stays a single read.
/// </summary>
public class ChatMessage
{
    public long Id { get; set; }

    public Guid ConversationId { get; set; }

    public ChatConversation? Conversation { get; set; }

    public Guid SenderUserId { get; set; }

    public ChatMessageKind Kind { get; set; }

    /// <summary>What the sender typed. Optional alongside a share or a link.</summary>
    [MaxLength(4000)]
    public string? Text { get; set; }

    /// <summary>The share link a <see cref="ChatMessageKind.Share"/> or <see cref="ChatMessageKind.ShareOpened"/> carries.</summary>
    public int? ShareId { get; set; }

    public SongShare? Share { get; set; }

    [MaxLength(2048)]
    public string? LinkUrl { get; set; }

    /// <summary><c>spotify</c>, <c>youtube</c>, or null for any other site.</summary>
    [MaxLength(32)]
    public string? LinkProvider { get; set; }

    /// <summary>What the link is: <c>track</c>, <c>album</c>, <c>playlist</c>, <c>artist</c>, <c>video</c>…</summary>
    [MaxLength(32)]
    public string? LinkKind { get; set; }

    [MaxLength(512)]
    public string? LinkTitle { get; set; }

    [MaxLength(512)]
    public string? LinkSubtitle { get; set; }

    [MaxLength(2048)]
    public string? LinkImageUrl { get; set; }

    /// <summary>Null: everyone in the conversation. Otherwise the one account that may see it.</summary>
    public Guid? VisibleToUserId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
