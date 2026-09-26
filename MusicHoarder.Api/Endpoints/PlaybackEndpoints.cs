using System.Diagnostics;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Auth.Middleware;
using MusicHoarder.Api.Playback;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// Playback sync ("Connect"): every signed-in device of an account sees what is playing and where,
/// can pull it here, and can remote-control the device that holds it. The rules live in
/// <see cref="PlaybackCoordinator"/>; this file is the HTTP surface.
///
/// <para>
/// Every route is <c>RequireNonDemo</c>, the GET included: strangers share the demo account, and
/// they would otherwise see and pause each other. Members may use it — it only ever coordinates
/// the caller's own devices — which is why <c>MemberWriteGuardMiddleware</c> allows the two POSTs.
/// Shipped Android builds never call any of this, and nothing here needs a new header.
/// </para>
/// </summary>
public static class PlaybackEndpoints
{
    /// <summary>
    /// Keeps proxies and Node's fetch body timeout from dropping an idle stream. Also how often a
    /// stream re-checks that the session which opened it still stands.
    /// </summary>
    internal static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long one stream lives before the server ends it and the client reconnects. A write to
    /// the frontend proxy succeeds whether or not the browser behind it is still there, so this is
    /// what bounds how long a client that vanished without closing its connection (a closed laptop
    /// lid, a suspended phone) stays listed as online.
    /// </summary>
    internal static readonly TimeSpan StreamLifetime = TimeSpan.FromMinutes(3);

    /// <summary>The <c>retry:</c> sent with every snapshot: back well within the reconnect grace.</summary>
    internal static readonly TimeSpan ReconnectAfter = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The stream is serialised by hand so its casing does not depend on DI-configured JSON
    /// options: camelCase, like every other response of the API.
    /// </summary>
    internal static readonly JsonSerializerOptions StreamJsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapPlaybackEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/playback").WithTags("Playback");

        group.MapGet("/", GetPlayback)
            .WithName("GetPlayback")
            .WithSummary("The account's playback session and its online devices.")
            .RequireNonDemo();

        group.MapGet("/stream", StreamPlayback)
            .WithName("StreamPlayback")
            .WithSummary("Server-sent events for one device: a snapshot, then session, devices and ping events, plus the commands addressed to this device. The device counts as connected while the stream is open. The server ends each stream after a few minutes; clients reconnect at once.")
            .RequireNonDemo();

        group.MapPost("/state", ReportState)
            .WithName("ReportPlaybackState")
            .WithSummary("A device reports its player. A claim makes it the active device; otherwise only the active device's report is accepted.")
            .RequireNonDemo();

        group.MapPost("/command", SendCommand)
            .WithName("SendPlaybackCommand")
            .WithSummary("Remote-control the device holding the session (pause, resume, next, previous, seek), or ask another device to take over (transfer).")
            .RequireNonDemo();

        return app;
    }

    internal static async Task<IResult> GetPlayback(
        ICurrentUserAccessor accessor, PlaybackCoordinator coordinator, CancellationToken ct)
    {
        if (accessor.User is not { } user) return Unauthenticated();
        return Results.Ok(await coordinator.GetSnapshotAsync(user.Id, ct));
    }

    internal static IResult StreamPlayback(
        HttpContext http,
        ICurrentUserAccessor accessor,
        PlaybackCoordinator coordinator,
        IAuthService auth,
        IHostApplicationLifetime lifetime,
        ILoggerFactory loggerFactory,
        string? deviceId,
        string? installId,
        string? name,
        string? kind,
        string? client)
    {
        if (accessor.User is not { } user) return Unauthenticated();

        // The stream outlives the check that let it in, so it re-checks that same session as it goes.
        var logger = loggerFactory.CreateLogger("MusicHoarder.Api.Endpoints.PlaybackEndpoints");
        if (SessionCheck(http, auth, user.Id, logger) is not { } stillSignedIn) return Unauthenticated();

        var error = PlaybackRules.TryParseDevice(deviceId, installId, name, kind, client, out var device);
        if (error is not null) return ErrorResult(error);

        // Some reverse proxies buffer a response unless told not to, which would hold the first
        // event back past the frontend proxy's 10 s wait for headers.
        http.Response.Headers.Append("X-Accel-Buffering", "no");
        return TypedResults.ServerSentEvents(StreamEventsAsync(
            coordinator, user.Id, device, PingInterval, StreamLifetime, stillSignedIn,
            lifetime.ApplicationStopping, http.RequestAborted));
    }

    internal static async Task<IResult> ReportState(
        PlaybackStateReport report,
        ICurrentUserAccessor accessor,
        PlaybackCoordinator coordinator,
        CancellationToken ct)
    {
        if (accessor.User is not { } user) return Unauthenticated();

        var outcome = await coordinator.ReportAsync(user.Id, report, ct);
        return outcome.Error is { } error ? ErrorResult(error) : Results.Ok(outcome.Value);
    }

    internal static async Task<IResult> SendCommand(
        PlaybackCommandRequest request,
        ICurrentUserAccessor accessor,
        PlaybackCoordinator coordinator,
        CancellationToken ct)
    {
        if (accessor.User is not { } user) return Unauthenticated();

        var outcome = await coordinator.SendCommandAsync(user.Id, request, ct);
        return outcome.Error is { } error
            ? ErrorResult(error)
            : Results.Accepted(uri: null, value: outcome.Value);
    }

    /// <summary>
    /// One device's stream. Registration happens on the first read rather than when the result is
    /// built, so a request that never starts streaming never registers; the <c>using</c> is the
    /// finally that unregisters it, however the stream ends: the client went away, the app is
    /// stopping, its <paramref name="lifetime"/> is up, or the session that opened it is gone.
    /// </summary>
    internal static async IAsyncEnumerable<SseItem<string>> StreamEventsAsync(
        PlaybackCoordinator coordinator,
        Guid userId,
        PlaybackDeviceIdentity device,
        TimeSpan pingInterval,
        TimeSpan lifetime,
        Func<CancellationToken, Task<bool>> stillSignedIn,
        CancellationToken stopping,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Kestrel's graceful stop waits for open requests, so a stream that outlived the app's
        // stopping would hold every deploy, and the final session flush, until the host gives up.
        using var ended = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping);
        ended.CancelAfter(lifetime);

        using var subscription = await coordinator.SubscribeAsync(userId, device, ended.Token);

        // Written at once: the frontend proxy gives up if the headers take longer than 10 s. The
        // retry brings the client back well within the reconnect grace when this stream ends.
        yield return ToSseItem(PlaybackEventTypes.Snapshot, subscription.Snapshot) with
        {
            ReconnectionInterval = ReconnectAfter,
        };

        var checkedAt = Stopwatch.GetTimestamp();
        while (true)
        {
            var next = await subscription.NextAsync(pingInterval, ended.Token);
            if (next is null) yield break;

            // On every ping, and never much further apart however busy the stream is.
            if (ReferenceEquals(next, PlaybackStreamEvent.Ping) || Stopwatch.GetElapsedTime(checkedAt) >= pingInterval)
            {
                if (!await stillSignedIn(ended.Token)) yield break;
                checkedAt = Stopwatch.GetTimestamp();
            }

            yield return ToSseItem(next.Type, next.Payload);

            // Resumed only after the item above was written and flushed: the device is still there.
            subscription.Delivered();
        }
    }

    /// <summary>
    /// The re-check for the session <see cref="AuthenticationMiddleware"/> resolved for this
    /// request, whether it came from the cookie or a bearer token; null when there is none to
    /// re-check.
    /// </summary>
    internal static Func<CancellationToken, Task<bool>>? SessionCheck(
        HttpContext http, IAuthService auth, Guid userId, ILogger logger) =>
        http.Items[AuthenticationMiddleware.SessionIdItemKey] is Guid sessionId
            ? ct => StillSignedInAsync(auth, sessionId, userId, logger, ct)
            : null;

    /// <summary>
    /// Whether the session that opened a stream still stands: not revoked (a logout, "log out
    /// everywhere"), not expired, its account not disabled. Read-only, so a check per ping never
    /// writes. A check that fails is not a revocation: the stream stays, bounded by its lifetime,
    /// and its reconnect is authenticated afresh.
    /// </summary>
    private static async Task<bool> StillSignedInAsync(
        IAuthService auth, Guid sessionId, Guid userId, ILogger logger, CancellationToken ct)
    {
        try
        {
            var live = await auth.ResolveSessionsAsync([sessionId], ct);
            return live.Any(s => s.User.Id == userId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return true; // the stream is ending anyway
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not re-check the session behind a playback stream for user {UserId}", userId);
            return true;
        }
    }

    internal static SseItem<string> ToSseItem(string eventType, object payload) =>
        new(JsonSerializer.Serialize(payload, payload.GetType(), StreamJsonOptions), eventType);

    private static IResult ErrorResult(PlaybackError error) =>
        Results.Json(new { error = error.Code }, statusCode: error.StatusCode);

    private static IResult Unauthenticated() =>
        Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
}
