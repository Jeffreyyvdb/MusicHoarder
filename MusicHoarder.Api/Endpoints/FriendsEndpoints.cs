using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// Owner-only management of friend accounts: minting/rotating/revoking email-bound invites,
/// listing friends, removing one (disable + kill sessions + revoke grants), and granting/revoking
/// what each friend can see. The friend-facing counterparts live in
/// <see cref="InviteEndpoints"/> (anonymous acceptance) and <see cref="SharedLibraryEndpoints"/>
/// (authenticated reads).
/// </summary>
public static class FriendsEndpoints
{
    public static IEndpointRouteBuilder MapFriendsEndpoints(this IEndpointRouteBuilder app)
    {
        // "/api/people" is the name that matches the model — these are accounts, not a second-class
        // "friend" construct. "/api/friends" stays mapped for one release so a browser holding
        // cached JS does not start 404ing mid-session; delete it with the rest of the aliases.
        MapPeopleGroup(app, "/api/people");
        MapPeopleGroup(app, "/api/friends");
        return app;
    }

    private static void MapPeopleGroup(IEndpointRouteBuilder app, string prefix)
    {
        var isAlias = prefix == "/api/friends";
        var group = app.MapGroup(prefix).WithTags("People").RequireAdmin();
        string Name(string name) => isAlias ? name + "Legacy" : name;

        group.MapPatch("/{userId:guid}/capabilities", UpdateCapabilities)
            .WithName(Name("UpdateMemberCapabilities"))
            .WithSummary("Set what a member may do: download requests, listening state, re-sharing, admin.");

        group.MapPost("/invites", CreateInvite)
            .WithName(Name("CreateFriendInvite"))
            .WithSummary("Create (or rotate) the invite link for an email; the previous link stops working.");
        group.MapGet("/invites", ListInvites)
            .WithName(Name("ListFriendInvites"))
            .WithSummary("List pending (unconsumed, unexpired) invites.");
        group.MapDelete("/invites/{id:guid}", RevokeInvite)
            .WithName(Name("RevokeFriendInvite"))
            .WithSummary("Revoke a pending invite; the link stops working immediately.");

        group.MapGet("", ListFriends)
            .WithName(Name("ListFriends"))
            .WithSummary("List friend accounts with their active grants.");
        group.MapDelete("/{userId:guid}", RemoveFriend)
            .WithName(Name("RemoveFriend"))
            .WithSummary("Remove a friend: disables the account, kills its sessions, revokes its grants.");

        group.MapPost("/{userId:guid}/grants", CreateGrant)
            .WithName(Name("CreateFriendGrant"))
            .WithSummary("Grant a friend an album, an artist, or the whole library.");
        group.MapDelete("/{userId:guid}/grants/{grantId:int}", RevokeGrant)
            .WithName(Name("RevokeFriendGrant"))
            .WithSummary("Revoke a grant; the member's view updates on their next fetch.");
    }

    public sealed record CreateInviteRequest(string Email, bool? SendEmail);
    public sealed record CreateGrantRequest(string Scope, string? Artist, string? Album);

    /// <param name="Capabilities">
    /// The complete desired set, by name — not a delta. Sending the whole set makes the request
    /// idempotent and means two admins toggling different switches cannot interleave into a state
    /// neither asked for.
    /// </param>
    public sealed record UpdateCapabilitiesRequest(string[] Capabilities);

    // ── Capabilities ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Set what one account may do. Granting <see cref="Capability.Administer"/> promotes the
    /// account to <see cref="UserRole.Admin"/>; withdrawing it demotes back to
    /// <see cref="UserRole.Member"/>.
    /// </summary>
    internal static async Task<IResult> UpdateCapabilities(
        Guid userId,
        UpdateCapabilitiesRequest body,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        // Self-demotion is refused rather than handled: an admin who clears their own Administer
        // bit would immediately 403 out of this very endpoint, with no way back short of the
        // database. Refusing is kinder than a one-way door.
        if (userId == currentUser.UserId)
            return Results.BadRequest(new { error = "cannot_change_own_capabilities" });

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || user.Role == UserRole.Demo)
            return Results.NotFound(new { message = "Account not found." });

        if (!TryParseCapabilities(body.Capabilities, out var requested, out var unknown))
            return Results.BadRequest(new { error = "unknown_capability", capability = unknown });

        var shouldBeAdmin = (requested & Capability.Administer) == Capability.Administer;

        // A disabled account cannot sign in, so making it an admin would manufacture an admin that
        // can never act — and the last-admin guard below would then happily count it as cover for
        // demoting the only real one.
        if (shouldBeAdmin && user.IsDisabled)
            return Results.BadRequest(new { error = "cannot_promote_disabled_account" });

        // Demotion is the one path that can brick the instance, so it runs serializably.
        //
        // Under READ COMMITTED two admins demoting each other concurrently would each count the
        // other, both commit, and leave zero admins — recoverable only through the database.
        // Serializable makes one of them fail instead.
        //
        // It MUST go through CreateExecutionStrategy: this context is configured with Npgsql
        // connection resiliency, and a retrying strategy refuses a user-initiated transaction
        // outright (a 500, not a subtle bug). The strategy also re-runs the block on the
        // serialization failure that isolation level is there to produce, which is exactly the
        // behaviour we want. The change tracker is cleared per attempt so a retry re-reads rather
        // than re-applying a half-mutated entity.
        if (user.Role == UserRole.Admin && !shouldBeAdmin)
        {
            var strategy = db.Database.CreateExecutionStrategy();
            var lastAdmin = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx =
                    await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

                var target = await db.Users.FirstAsync(u => u.Id == userId, ct);
                var otherAdmins = await db.Users.CountAsync(
                    u => u.Role == UserRole.Admin && !u.IsDisabled && u.Id != userId, ct);
                if (otherAdmins == 0) return true;

                target.Role = UserRole.Member;
                target.Capabilities = requested;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return false;
            });

            if (lastAdmin)
                return Results.BadRequest(new { error = "last_admin" });

            // Re-read: the tracker was cleared inside the strategy, so `user` is detached.
            var saved = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
            return CapabilitiesResponse(saved);
        }

        user.Role = shouldBeAdmin ? UserRole.Admin : UserRole.Member;
        user.Capabilities = requested;
        await db.SaveChangesAsync(ct);

        return CapabilitiesResponse(user);
    }

    private static IResult CapabilitiesResponse(User user) => Results.Ok(new
    {
        user.Id,
        user.Email,
        user.DisplayName,
        Role = WireRole.ToWire(user.Role),
        IsAdmin = user.Role == UserRole.Admin,
        Capabilities = WireRole.ToWire(
            user.Role == UserRole.Admin ? CapabilityDefaults.All : user.Capabilities),
    });

    /// <summary>
    /// Parse capability names, rejecting anything unrecognized rather than ignoring it — a typo'd
    /// name must not silently read as "revoke that one".
    /// </summary>
    private static bool TryParseCapabilities(
        string[]? names, out Capability parsed, out string? unknown)
    {
        parsed = Capability.None;
        unknown = null;

        foreach (var name in names ?? [])
        {
            // Reject digits before parsing. Enum.TryParse happily reads "8" as Administer, so a
            // client could grant admin without ever naming it — and a typo'd number would land on
            // whatever flag happened to share that value. The wire contract is names only.
            if (string.IsNullOrWhiteSpace(name)
                || name.AsSpan().ContainsAnyInRange('0', '9')
                || !Enum.TryParse<Capability>(name, ignoreCase: true, out var value)
                || value == Capability.None
                || !Enum.IsDefined(value))
            {
                unknown = name;
                return false;
            }
            parsed |= value;
        }
        return true;
    }

    // ── Invites ─────────────────────────────────────────────────────────────────────────────

    internal static async Task<IResult> CreateInvite(
        CreateInviteRequest body,
        HttpContext ctx,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        IAuthService authService,
        IMagicLinkSender sender,
        IOptions<FrontendOptions> frontendOptions,
        CancellationToken ct)
    {
        var email = body.Email?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return Results.BadRequest(new { error = "email_required" });

        var frontendBase = FrontendUrlResolver.Resolve(ctx, frontendOptions.Value);
        if (string.IsNullOrEmpty(frontendBase))
            return Results.Json(new { error = "frontend_base_url_not_configured" }, statusCode: 500);

        var minted = await authService.CreateOrRotateInviteAsync(currentUser.UserId, email, ct);
        if (minted is null)
            return Results.BadRequest(new { error = "email_is_owner_or_demo" });

        // The raw token exists only in this response (the DB holds its hash), so the URL is
        // always returned — the caller is the authenticated owner, no enumeration concern.
        var inviteUrl = $"{frontendBase}/invite/{Uri.EscapeDataString(minted.RawToken)}";

        var emailSent = false;
        if (body.SendEmail == true && !sender.IsConsoleFallback)
        {
            var inviter = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == currentUser.UserId, ct);
            try
            {
                await sender.SendInviteAsync(inviter!, minted.Invite.Email, inviteUrl, ct);
                emailSent = true;
            }
            catch (Exception)
            {
                // The invite itself was minted; the owner still has the URL to hand over manually.
                return Results.Json(new { error = "send_failed", inviteUrl }, statusCode: 503);
            }
        }

        return Results.Ok(new
        {
            minted.Invite.Id,
            minted.Invite.Email,
            InviteUrl = inviteUrl,
            minted.Invite.ExpiresAtUtc,
            EmailSent = emailSent,
            EmailInLogs = body.SendEmail == true && sender.IsConsoleFallback,
        });
    }

    internal static async Task<IResult> ListInvites(MusicHoarderDbContext db, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        // Query filter scopes to the calling owner's invites.
        var invites = await db.Invites.AsNoTracking()
            .Where(i => i.ConsumedAtUtc == null && i.RevokedAtUtc == null && i.ExpiresAtUtc > nowUtc)
            .OrderByDescending(i => i.CreatedAtUtc)
            .Select(i => new { i.Id, i.Email, i.CreatedAtUtc, i.ExpiresAtUtc })
            .ToListAsync(ct);
        return Results.Ok(invites);
    }

    internal static async Task<IResult> RevokeInvite(Guid id, MusicHoarderDbContext db, CancellationToken ct)
    {
        var invite = await db.Invites
            .FirstOrDefaultAsync(i => i.Id == id && i.ConsumedAtUtc == null && i.RevokedAtUtc == null, ct);
        if (invite is null)
            return Results.NotFound(new { message = $"Invite with id {id} not found." });

        invite.RevokedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ── Friends + grants ────────────────────────────────────────────────────────────────────

    internal static async Task<IResult> ListFriends(
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        // Every real account except the caller's own and the shared demo login.
        //
        // Deliberately NOT filtered to Role == Member: promoting someone to admin would otherwise
        // drop them out of this list, leaving no way to demote them again short of the database.
        var friends = await db.Users.AsNoTracking()
            .Where(u => u.Role != UserRole.Demo && u.Id != currentUser.UserId)
            .OrderBy(u => u.CreatedAtUtc)
            .ToListAsync(ct);

        var friendIds = friends.Select(f => f.Id).ToList();
        var grants = await db.LibraryShareGrants.AsNoTracking()
            .Where(g => g.OwnerUserId == currentUser.UserId
                && g.RevokedAtUtc == null
                && friendIds.Contains(g.GranteeUserId))
            .OrderBy(g => g.CreatedAtUtc)
            .ToListAsync(ct);

        return Results.Ok(friends.Select(f => new
        {
            f.Id,
            f.Email,
            f.DisplayName,
            f.IsDisabled,
            f.CreatedAtUtc,
            f.LastLoginAtUtc,
            Role = WireRole.ToWire(f.Role),
            IsAdmin = f.Role == UserRole.Admin,
            // Effective, not stored: an admin holds everything regardless of their column, and the
            // toggles must show that rather than a misleading set of empty switches.
            Capabilities = WireRole.ToWire(
                f.Role == UserRole.Admin ? CapabilityDefaults.All : f.Capabilities),
            Grants = grants
                .Where(g => g.GranteeUserId == f.Id)
                .Select(g => new
                {
                    g.Id,
                    Scope = g.Scope.ToString(),
                    Artist = g.ArtistDisplay,
                    Album = g.AlbumDisplay,
                    g.CreatedAtUtc,
                }),
        }));
    }

    internal static async Task<IResult> RemoveFriend(
        Guid userId,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        // Role check first: this endpoint must never be able to touch the Owner or Demo rows.
        var friend = await db.Users
            .FirstOrDefaultAsync(u => u.Id == userId && u.Role == UserRole.Member, ct);
        if (friend is null)
            return Results.NotFound(new { message = $"Friend with id {userId} not found." });

        var nowUtc = DateTime.UtcNow;
        friend.IsDisabled = true;

        // Live sessions die on their next request: ResolveSessionAsync rejects disabled users,
        // but revoke the rows anyway so a later re-enable doesn't resurrect old cookies.
        var sessions = await db.Sessions
            .Where(s => s.UserId == userId && s.RevokedAtUtc == null)
            .ToListAsync(ct);
        foreach (var s in sessions) s.RevokedAtUtc = nowUtc;

        var grants = await db.LibraryShareGrants
            .Where(g => g.OwnerUserId == currentUser.UserId
                && g.GranteeUserId == userId
                && g.RevokedAtUtc == null)
            .ToListAsync(ct);
        foreach (var g in grants) g.RevokedAtUtc = nowUtc;

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    internal static async Task<IResult> CreateGrant(
        Guid userId,
        CreateGrantRequest body,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        var scope = body.Scope?.ToLowerInvariant() switch
        {
            "album" => (ShareGrantScope?)ShareGrantScope.Album,
            "artist" => ShareGrantScope.Artist,
            "library" => ShareGrantScope.Library,
            _ => null,
        };
        if (scope is null)
            return Results.BadRequest(new { error = "invalid_scope" });

        var artist = body.Artist?.Trim();
        var album = body.Album?.Trim();
        if (scope != ShareGrantScope.Library && string.IsNullOrWhiteSpace(artist))
            return Results.BadRequest(new { error = "artist_required" });
        if (scope == ShareGrantScope.Album && string.IsNullOrWhiteSpace(album))
            return Results.BadRequest(new { error = "album_required" });

        var friend = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId && u.Role == UserRole.Member && !u.IsDisabled, ct);
        if (friend is null)
            return Results.NotFound(new { message = $"Friend with id {userId} not found." });

        // Same key derivation as SharesEndpoints.LoadSongsInScopeAsync (deliberately .ToLower(),
        // matching what the membership predicate translates to in SQL).
        var artistKey = scope == ShareGrantScope.Library ? null : (artist ?? "").ToLower();
        var albumKey = scope == ShareGrantScope.Album ? album!.ToLower() : null;

        // Re-granting the same thing hands back the existing grant instead of stacking rows.
        var existing = await db.LibraryShareGrants.AsNoTracking()
            .FirstOrDefaultAsync(g => g.OwnerUserId == currentUser.UserId
                && g.GranteeUserId == userId
                && g.Scope == scope
                && g.ArtistKey == artistKey
                && g.AlbumKey == albumKey
                && g.RevokedAtUtc == null, ct);
        if (existing is not null)
            return Results.Ok(ToGrantView(existing));

        var grant = new LibraryShareGrant
        {
            OwnerUserId = currentUser.UserId,
            GranteeUserId = userId,
            Scope = scope.Value,
            ArtistKey = artistKey,
            AlbumKey = albumKey,
            ArtistDisplay = scope == ShareGrantScope.Library ? null : artist,
            AlbumDisplay = scope == ShareGrantScope.Album ? album : null,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.LibraryShareGrants.Add(grant);
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToGrantView(grant));
    }

    internal static async Task<IResult> RevokeGrant(
        Guid userId,
        int grantId,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        var grant = await db.LibraryShareGrants
            .FirstOrDefaultAsync(g => g.Id == grantId
                && g.OwnerUserId == currentUser.UserId
                && g.GranteeUserId == userId
                && g.RevokedAtUtc == null, ct);
        if (grant is null)
            return Results.NotFound(new { message = $"Grant with id {grantId} not found." });

        grant.RevokedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static object ToGrantView(LibraryShareGrant grant) => new
    {
        grant.Id,
        Scope = grant.Scope.ToString(),
        Artist = grant.ArtistDisplay,
        Album = grant.AlbumDisplay,
        grant.CreatedAtUtc,
    };
}
