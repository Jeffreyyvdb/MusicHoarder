namespace MusicHoarder.Api.Auth;

/// <summary>
/// Provides the owner user's GUID to background services that need to filter or tag rows by
/// owner without going through the per-request <see cref="ICurrentUserAccessor"/> (which is
/// null in hosted-service contexts). The GUID is the well-known constant from
/// <see cref="WellKnownUsers.OwnerId"/>, but routed via an interface so tests can substitute.
/// </summary>
public interface IOwnerLookupService
{
    Guid OwnerUserId { get; }
}

public sealed class OwnerLookupService : IOwnerLookupService
{
    public Guid OwnerUserId => WellKnownUsers.OwnerId;
}
