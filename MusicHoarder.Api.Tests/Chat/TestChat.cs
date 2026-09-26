using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Chat;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Chat;

internal static class TestChat
{
    public const string PublicBaseUrl = "https://music.example";

    public static ChatService Service(
        MusicHoarderDbContext db,
        ChatHub? hub = null,
        RecordingNotificationQueue? queue = null,
        ILinkPreviewService? previews = null,
        TimeProvider? time = null) =>
        new(
            db,
            hub ?? new ChatHub(),
            queue ?? new RecordingNotificationQueue(),
            previews ?? new StubLinkPreviews(),
            new ChatSendLimiter(),
            Microsoft.Extensions.Options.Options.Create(new FrontendOptions { PublicBaseUrl = PublicBaseUrl }),
            time ?? TimeProvider.System);

    public static DbContextOptions<MusicHoarderDbContext> NewOptions() =>
        new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

    /// <summary>
    /// The context tests run the service through. One model for every call: the InMemory provider
    /// cannot update a row through a different model from the one that inserted it, and the context
    /// builds a model per signed-in user. ChatService scopes every read itself, so it behaves the same
    /// on this as on the request's own context.
    /// </summary>
    public static MusicHoarderDbContext Db(DbContextOptions<MusicHoarderDbContext> options) => new(options);

    /// <summary>A context as <paramref name="user"/> would get it — for asserting what the query filters show. Read-only.</summary>
    public static MusicHoarderDbContext As(DbContextOptions<MusicHoarderDbContext> options, CurrentUser? user) =>
        new(options, new TestCurrentUserAccessor(user));

    public static CurrentUser Member(Guid id, string name) => new(id, $"{name.ToLowerInvariant()}@test.local", UserRole.Member, name);

    public static User UserRow(Guid id, string email, UserRole role, string? displayName = null, bool disabled = false) => new()
    {
        Id = id,
        Email = email,
        EmailNormalized = User.Normalize(email),
        DisplayName = displayName,
        Role = role,
        IsDisabled = disabled,
        CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };
}

internal sealed class RecordingNotificationQueue : IChatNotificationQueue
{
    public List<long> Enqueued { get; } = [];

    public void Enqueue(long messageId, DateTime createdAtUtc) => Enqueued.Add(messageId);
}

/// <summary>Never touches the network: a known card for Spotify and YouTube links, nothing otherwise.</summary>
internal sealed class StubLinkPreviews : ILinkPreviewService
{
    public Task<ParsedChatLink> ResolveAsync(ParsedChatLink link, CancellationToken ct) => Task.FromResult(link);

    public Task<LinkPreview?> PreviewAsync(ParsedChatLink link, CancellationToken ct) =>
        Task.FromResult<LinkPreview?>(link.Provider is "spotify" or "youtube"
            ? new LinkPreview("Harder, Better, Faster, Stronger", "Daft Punk", "https://i.scdn.co/image/abc")
            : null);
}
