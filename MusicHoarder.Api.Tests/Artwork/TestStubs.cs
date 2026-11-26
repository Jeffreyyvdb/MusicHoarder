using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Auth;

namespace MusicHoarder.Api.Tests.Artwork;

/// <summary>A no-op <see cref="ICoverThumbnailService"/> for tests that only need the type.</summary>
internal sealed class StubCoverThumbnailService : ICoverThumbnailService
{
    public string CacheDirectory => "/cache";
    public int ClampToBucket(int requestedSize) => requestedSize;
    public string? GetCachedPath(string identityPath, long identityStamp, int requestedSize) => null;
    public Task<ResolvedCover?> GetThumbnailAsync(ResolvedCover source, string identityPath, int size, CancellationToken ct = default)
        => Task.FromResult<ResolvedCover?>(null);
    public void Prewarm(ResolvedCover source, string identityPath) { }

    public ResolvedCover? TryGetCachedThumbnail(string identityPath, int size) => null;
}

/// <summary>
/// An <see cref="IAuthService"/> that supports nothing — for endpoint tests whose path never
/// touches auth beyond the invalidation calls, which it records so a test can assert that a change
/// to an account drops its cached session resolution.
/// </summary>
internal sealed class NoOpAuthService : IAuthService
{
    public Task<RequestLinkResult?> RequestLinkAsync(string email, string frontendBaseUrl, string? client, string? ip, string? userAgent, CancellationToken ct)
        => Task.FromResult<RequestLinkResult?>(null);

    public Task<Session?> ConsumeLinkAsync(string rawToken, string? ip, string? userAgent, CancellationToken ct)
        => Task.FromResult<Session?>(null);

    public Task<Session?> StartDemoSessionAsync(string? ip, string? userAgent, CancellationToken ct)
        => Task.FromResult<Session?>(null);

    public Task<(Session Session, User User)?> ResolveSessionAsync(Guid sessionId, CancellationToken ct)
        => Task.FromResult<(Session, User)?>(null);

    public Task<IReadOnlyList<(Session Session, User User)>> ResolveSessionsAsync(IReadOnlyCollection<Guid> sessionIds, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<(Session, User)>>([]);

    public Task RevokeAsync(Guid sessionId, bool allForUser, CancellationToken ct) => Task.CompletedTask;

    public List<Guid> InvalidatedSessions { get; } = [];

    public List<Guid> InvalidatedUsers { get; } = [];

    public void InvalidateSession(Guid sessionId) => InvalidatedSessions.Add(sessionId);

    public void InvalidateUser(Guid userId) => InvalidatedUsers.Add(userId);

    public Task<InviteMintResult?> CreateOrRotateInviteAsync(Guid ownerUserId, string email, CancellationToken ct)
        => Task.FromResult<InviteMintResult?>(null);

    public Task<InvitePeekResult?> PeekInviteAsync(string rawToken, CancellationToken ct)
        => Task.FromResult<InvitePeekResult?>(null);

    public Task<Session?> AcceptInviteAsync(string rawToken, string? ip, string? userAgent, CancellationToken ct)
        => Task.FromResult<Session?>(null);
}
