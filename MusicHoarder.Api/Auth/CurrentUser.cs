namespace MusicHoarder.Api.Auth;

/// <summary>
/// A lightweight projection of <see cref="User"/> attached to <c>HttpContext.Items</c> by
/// <see cref="Middleware.AuthenticationMiddleware"/> and read elsewhere via
/// <see cref="ICurrentUserAccessor"/>.
/// </summary>
public sealed record CurrentUser(Guid Id, string Email, UserRole Role, string? DisplayName)
{
    public bool IsOwner => Role == UserRole.Owner;
    public bool IsDemo => Role == UserRole.Demo;
}
