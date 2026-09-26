using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Notifications;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// Browser notifications (Web Push): hand out the instance's VAPID key, and keep each account's
/// subscriptions. What gets sent is decided elsewhere (<see cref="Chat.ChatPushService"/>).
/// Non-demo accounts only; a member may manage their own subscriptions, and nothing here takes a
/// user id from the request.
/// </summary>
public static class PushEndpoints
{
    public static IEndpointRouteBuilder MapPushEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/push").WithTags("Push");

        group.MapGet("/config", GetConfig)
            .WithName("GetPushConfig")
            .WithSummary("Whether notifications are available, and the VAPID public key to subscribe with.")
            .RequireNonDemo();

        group.MapPost("/subscriptions", Subscribe)
            .WithName("SavePushSubscription")
            .WithSummary("Save this browser's push subscription for the caller (idempotent per endpoint).")
            .RequireNonDemo();

        group.MapPost("/unsubscribe", Unsubscribe)
            .WithName("RemovePushSubscription")
            .WithSummary("Forget this browser's subscription for the caller.")
            .RequireNonDemo();

        group.MapPost("/test", SendTest)
            .WithName("SendTestPush")
            .WithSummary("Send a test notification to the caller's subscribed browsers.")
            .RequireNonDemo();

        return app;
    }

    public sealed record PushKeys(string? P256dh, string? Auth);

    /// <summary>The shape of <c>PushSubscription.toJSON()</c> in the browser.</summary>
    public sealed record SubscribeRequest(string? Endpoint, PushKeys? Keys);

    public sealed record UnsubscribeRequest(string? Endpoint);

    internal static async Task<IResult> GetConfig(IVapidKeyStore keys, CancellationToken ct)
    {
        var identity = await keys.GetAsync(ct);
        return Results.Ok(new { Enabled = identity is not null, PublicKey = identity?.PublicKey });
    }

    internal static async Task<IResult> Subscribe(
        SubscribeRequest body,
        ICurrentUserAccessor accessor,
        MusicHoarderDbContext db,
        IOptions<WebPushOptions> options,
        CancellationToken ct)
    {
        if (accessor.User is not { } me) return Unauthenticated();
        if (!options.Value.Enabled)
            return Results.Json(new { error = "push_disabled", message = "Notifications are switched off on this server." }, statusCode: 409);

        var endpoint = body.Endpoint?.Trim();
        var p256dh = body.Keys?.P256dh?.Trim();
        var auth = body.Keys?.Auth?.Trim();
        if (!WebPushSender.IsAllowedEndpoint(endpoint))
            return Results.BadRequest(new { error = "unsupported_push_service", message = "This browser's push service is not supported." });
        if (string.IsNullOrEmpty(p256dh) || p256dh.Length > 128 || string.IsNullOrEmpty(auth) || auth.Length > 64)
            return Results.BadRequest(new { error = "invalid_subscription", message = "The subscription is missing its keys." });

        var existing = await db.WebPushSubscriptions.FirstOrDefaultAsync(s => s.UserId == me.Id && s.Endpoint == endpoint, ct);
        if (existing is null)
        {
            db.WebPushSubscriptions.Add(new WebPushSubscription
            {
                UserId = me.Id,
                Endpoint = endpoint!,
                P256dh = p256dh,
                Auth = auth,
                CreatedAtUtc = DateTime.UtcNow,
            });
        }
        else
        {
            existing.P256dh = p256dh;
            existing.Auth = auth;
            existing.FailureCount = 0;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The same browser saved it twice at once; the other request's row stands.
        }
        return Results.NoContent();
    }

    internal static async Task<IResult> Unsubscribe(
        UnsubscribeRequest body, ICurrentUserAccessor accessor, MusicHoarderDbContext db, CancellationToken ct)
    {
        if (accessor.User is not { } me) return Unauthenticated();
        var endpoint = body.Endpoint?.Trim();
        if (string.IsNullOrEmpty(endpoint)) return Results.NoContent();

        var rows = await db.WebPushSubscriptions.Where(s => s.UserId == me.Id && s.Endpoint == endpoint).ToListAsync(ct);
        db.WebPushSubscriptions.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    internal static async Task<IResult> SendTest(
        ICurrentUserAccessor accessor, MusicHoarderDbContext db, IWebPushSender sender, CancellationToken ct)
    {
        if (accessor.User is not { } me) return Unauthenticated();

        var subscriptions = await db.WebPushSubscriptions.Where(s => s.UserId == me.Id).ToListAsync(ct);
        var payload = new
        {
            Type = "test",
            UserId = me.Id,
            Title = "Notifications are on",
            Body = "You’ll hear from MusicHoarder here when someone sends you a message.",
            Url = "/chats",
            Tag = "test",
        };
        var delivered = 0;
        foreach (var subscription in subscriptions)
        {
            var outcome = await sender.SendAsync(
                subscription.Endpoint, subscription.P256dh, subscription.Auth,
                new WebPushMessage(payload, TimeSpan.FromMinutes(10), Urgent: true, Topic: null), ct);
            if (outcome == WebPushOutcome.Delivered) delivered++;
            else if (outcome == WebPushOutcome.Gone) db.WebPushSubscriptions.Remove(subscription);
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { Subscriptions = subscriptions.Count, Delivered = delivered });
    }

    private static IResult Unauthenticated() =>
        Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
}
