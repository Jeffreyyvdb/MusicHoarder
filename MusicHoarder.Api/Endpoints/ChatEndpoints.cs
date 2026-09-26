using System.Diagnostics;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Chat;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// Chat between the accounts of this instance. The rules live in <see cref="ChatService"/>; this file
/// is the HTTP surface.
///
/// <para>
/// Every route is <c>RequireNonDemo</c>: strangers share the demo account, and they would be reading
/// each other's messages. Members may chat — every route acts only on the caller's own
/// conversations — which is why <c>MemberWriteGuardMiddleware</c> allows the writes by exact shape.
/// </para>
/// </summary>
public static class ChatEndpoints
{
    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/chat").WithTags("Chat");

        group.MapGet("/people", ListPeople)
            .WithName("ListChatPeople")
            .WithSummary("The accounts the caller can start a conversation with.")
            .RequireNonDemo();

        group.MapGet("/conversations", ListConversations)
            .WithName("ListChatConversations")
            .WithSummary("The caller's conversations, newest first, with their last message and unread counts.")
            .RequireNonDemo();

        group.MapPost("/conversations", StartConversation)
            .WithName("StartChatConversation")
            .WithSummary("Open (or return) the direct conversation with one person.")
            .RequireNonDemo();

        group.MapGet("/conversations/{id:guid}", GetConversation)
            .WithName("GetChatConversation")
            .RequireNonDemo();

        group.MapGet("/conversations/{id:guid}/messages", ListMessages)
            .WithName("ListChatMessages")
            .WithSummary("A page of messages, oldest first: the newest page, the page before `before`, or everything after `after`.")
            .RequireNonDemo();

        group.MapPost("/conversations/{id:guid}/messages", SendMessage)
            .WithName("SendChatMessage")
            .WithSummary("Send text, a song or album of yours (songId + scope), or a link. A link in the text is attached on its own.")
            .RequireNonDemo();

        group.MapPost("/conversations/{id:guid}/read", MarkRead)
            .WithName("MarkChatRead")
            .WithSummary("Mark the conversation read up to now, on every device.")
            .RequireNonDemo();

        group.MapPost("/send", SendToPeople)
            .WithName("SendChatToPeople")
            .WithSummary("Send the same message to several people, each in their direct conversation (Send to…, the share sheet).")
            .RequireNonDemo();

        group.MapGet("/unread", GetUnread)
            .WithName("GetChatUnread")
            .RequireNonDemo();

        group.MapGet("/share-links/{token}", GetShareContext)
            .WithName("GetChatShareContext")
            .WithSummary("For a share link the caller opened: the conversation it was put in and who shared it.")
            .RequireNonDemo();

        group.MapGet("/stream", Stream)
            .WithName("StreamChat")
            .WithSummary("Server-sent events: `ready` on connect, then `message` and `read` as they happen, and `ping`. Events say what changed, not what it is: re-read on each. The server ends each stream after a few minutes; clients reconnect at once.")
            .RequireNonDemo();

        return app;
    }

    internal static async Task<IResult> ListPeople(ICurrentUserAccessor accessor, ChatService chat, CancellationToken ct) =>
        accessor.User is { } me ? Results.Ok(await chat.ListPeopleAsync(me, ct)) : Unauthenticated();

    internal static async Task<IResult> ListConversations(ICurrentUserAccessor accessor, ChatService chat, CancellationToken ct) =>
        accessor.User is { } me ? Results.Ok(await chat.ListConversationsAsync(me, ct)) : Unauthenticated();

    internal static async Task<IResult> StartConversation(
        StartConversationRequest body, ICurrentUserAccessor accessor, ChatService chat, CancellationToken ct) =>
        accessor.User is { } me ? ToResult(await chat.StartAsync(me, body.UserId, ct)) : Unauthenticated();

    internal static async Task<IResult> GetConversation(Guid id, ICurrentUserAccessor accessor, ChatService chat, CancellationToken ct)
    {
        if (accessor.User is not { } me) return Unauthenticated();
        var conversation = await chat.GetConversationAsync(me, id, ct);
        return conversation is null ? ErrorResult(ChatError.NotFound()) : Results.Ok(conversation);
    }

    internal static async Task<IResult> ListMessages(
        Guid id, long? before, long? after, int? limit, ICurrentUserAccessor accessor, ChatService chat, CancellationToken ct) =>
        accessor.User is { } me ? ToResult(await chat.ListMessagesAsync(me, id, before, after, limit, ct)) : Unauthenticated();

    internal static async Task<IResult> SendMessage(
        Guid id, SendChatMessageRequest body, ICurrentUserAccessor accessor, ChatService chat, CancellationToken ct) =>
        accessor.User is { } me ? ToResult(await chat.SendAsync(me, id, body, ct)) : Unauthenticated();

    internal static async Task<IResult> MarkRead(Guid id, ICurrentUserAccessor accessor, ChatService chat, CancellationToken ct) =>
        accessor.User is { } me ? ToResult(await chat.MarkReadAsync(me, id, ct)) : Unauthenticated();

    internal static async Task<IResult> SendToPeople(
        SendToPeopleRequest body, ICurrentUserAccessor accessor, ChatService chat, CancellationToken ct) =>
        accessor.User is { } me ? ToResult(await chat.SendToPeopleAsync(me, body, ct)) : Unauthenticated();

    internal static async Task<IResult> GetUnread(ICurrentUserAccessor accessor, ChatService chat, CancellationToken ct) =>
        accessor.User is { } me ? Results.Ok(await chat.GetUnreadAsync(me, ct)) : Unauthenticated();

    internal static async Task<IResult> GetShareContext(
        string token, ICurrentUserAccessor accessor, ChatService chat, CancellationToken ct)
    {
        if (accessor.User is not { } me) return Unauthenticated();
        var context = await chat.GetShareContextAsync(me, token, ct);
        return context is null ? ErrorResult(ChatError.NotFound("Conversation")) : Results.Ok(context);
    }

    internal static IResult Stream(
        HttpContext http,
        ICurrentUserAccessor accessor,
        ChatHub hub,
        IAuthService auth,
        IHostApplicationLifetime lifetime,
        ILoggerFactory loggerFactory)
    {
        if (accessor.User is not { } me) return Unauthenticated();

        var logger = loggerFactory.CreateLogger("MusicHoarder.Api.Endpoints.ChatEndpoints");
        if (PlaybackEndpoints.SessionCheck(http, auth, me.Id, logger) is not { } stillSignedIn) return Unauthenticated();

        http.Response.Headers.Append("X-Accel-Buffering", "no");
        return TypedResults.ServerSentEvents(StreamEventsAsync(
            hub, me.Id, PlaybackEndpoints.PingInterval, PlaybackEndpoints.StreamLifetime, stillSignedIn,
            lifetime.ApplicationStopping, http.RequestAborted));
    }

    /// <summary>
    /// One device's chat stream. Like the playback stream it ends itself after
    /// <paramref name="lifetime"/> (a write to the frontend proxy succeeds whether or not the browser
    /// behind it is still there) and re-checks the session that opened it on every ping.
    /// </summary>
    internal static async IAsyncEnumerable<SseItem<string>> StreamEventsAsync(
        ChatHub hub,
        Guid userId,
        TimeSpan pingInterval,
        TimeSpan lifetime,
        Func<CancellationToken, Task<bool>> stillSignedIn,
        CancellationToken stopping,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var ended = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping);
        ended.CancelAfter(lifetime);
        using var subscription = hub.Subscribe(userId);

        yield return PlaybackEndpoints.ToSseItem(ChatEventTypes.Ready, new { }) with
        {
            ReconnectionInterval = PlaybackEndpoints.ReconnectAfter,
        };

        var checkedAt = Stopwatch.GetTimestamp();
        while (true)
        {
            ChatStreamEvent? next;
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ended.Token);
                wait.CancelAfter(pingInterval);
                next = await subscription.Reader.ReadAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!ended.IsCancellationRequested)
            {
                next = null; // nothing within the ping interval
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                yield break;
            }

            if (next is null || Stopwatch.GetElapsedTime(checkedAt) >= pingInterval)
            {
                if (!await stillSignedIn(ended.Token)) yield break;
                checkedAt = Stopwatch.GetTimestamp();
            }

            yield return next is null
                ? PlaybackEndpoints.ToSseItem(ChatEventTypes.Ping, new { })
                : PlaybackEndpoints.ToSseItem(next.Type, next.Payload);
        }
    }

    private static IResult ToResult<T>(ChatOutcome<T> outcome) =>
        outcome.Error is { } error ? ErrorResult(error) : Results.Ok(outcome.Value);

    private static IResult ErrorResult(ChatError error) =>
        Results.Json(new { error = error.Code, message = error.Message }, statusCode: error.StatusCode);

    private static IResult Unauthenticated() =>
        Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
}
