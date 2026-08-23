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
        var group = app.MapGroup("/api/friends").WithTags("Friends").RequireOwner();

        group.MapPost("/invites", CreateInvite)
            .WithName("CreateFriendInvite")
            .WithSummary("Create (or rotate) the invite link for an email; the previous link stops working.");
        group.MapGet("/invites", ListInvites)
            .WithName("ListFriendInvites")
            .WithSummary("List pending (unconsumed, unexpired) invites.");
        group.MapDelete("/invites/{id:guid}", RevokeInvite)
            .WithName("RevokeFriendInvite")
            .WithSummary("Revoke a pending invite; the link stops working immediately.");

        group.MapGet("", ListFriends)
            .WithName("ListFriends")
            .WithSummary("List friend accounts with their active grants.");
        group.MapDelete("/{userId:guid}", RemoveFriend)
            .WithName("RemoveFriend")
            .WithSummary("Remove a friend: disables the account, kills its sessions, revokes its grants.");

        group.MapPost("/{userId:guid}/grants", CreateGrant)
            .WithName("CreateFriendGrant")
            .WithSummary("Grant a friend an album, an artist, or the whole library.");
        group.MapDelete("/{userId:guid}/grants/{grantId:int}", RevokeGrant)
            .WithName("RevokeFriendGrant")
            .WithSummary("Revoke a grant; the friend's view updates on their next fetch.");

        return app;
    }

    public sealed record CreateInviteRequest(string Email, bool? SendEmail);
    public sealed record CreateGrantRequest(string Scope, string? Artist, string? Album);

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
        var friends = await db.Users.AsNoTracking()
            .Where(u => u.Role == UserRole.Friend)
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
            .FirstOrDefaultAsync(u => u.Id == userId && u.Role == UserRole.Friend, ct);
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
            .FirstOrDefaultAsync(u => u.Id == userId && u.Role == UserRole.Friend && !u.IsDisabled, ct);
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
