namespace MusicHoarder.Api.Auth;

public interface ICurrentUserAccessor
{
    /// <summary>The signed-in user for the current request, or null when anonymous.</summary>
    CurrentUser? User { get; }

    /// <summary>The current user's id, or <see cref="Guid.Empty"/> when anonymous. Used by EF query filters.</summary>
    Guid UserId { get; }
}

public sealed class HttpContextCurrentUserAccessor : ICurrentUserAccessor
{
    public const string HttpContextItemKey = "__mh_current_user";

    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextCurrentUserAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private CurrentUser? _actingAs;

    /// <summary>
    /// Lets a scope with no request (a background sampler) read as one account. Ignored whenever there is
    /// a request, signed in or not, so it can never change who a request is.
    /// </summary>
    public void ActAs(CurrentUser user) => _actingAs = user;

    public CurrentUser? User => _httpContextAccessor.HttpContext is { } http
        ? http.Items[HttpContextItemKey] as CurrentUser
        : _actingAs;

    public Guid UserId => User?.Id ?? Guid.Empty;
}
