namespace MusicHoarder.Api.Auth;

/// <summary>
/// Stable, version-controlled identifiers for the two pre-seeded users. These GUIDs are
/// referenced both by the EF migration that creates the User rows (<c>HasData</c>) and by
/// the per-entity migrations that backfill <c>OwnerUserId</c> for existing rows. Do not
/// change them; doing so orphans every row in the database.
/// </summary>
public static class WellKnownUsers
{
    public static readonly Guid OwnerId = new("9c0f1e3d-7b6a-4d2e-9c8f-0a1b2c3d4e5f");
    public static readonly Guid DemoId  = new("d0e1f2a3-b4c5-4d6e-9f80-112233445566");

    public const string OwnerPlaceholderEmail = "owner@unconfigured.local";
    public const string DemoPlaceholderEmail  = "demo@unconfigured.local";
}
