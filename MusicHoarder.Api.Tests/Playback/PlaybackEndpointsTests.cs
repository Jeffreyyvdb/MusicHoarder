using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.Middleware;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Playback;
using MusicHoarder.Api.Tests.Auth;
using static MusicHoarder.Api.Tests.Playback.PlaybackTestKit;

namespace MusicHoarder.Api.Tests.Playback;

/// <summary>
/// The HTTP surface of playback sync: the SSE framing and JSON casing both clients parse, the
/// status codes they branch on, and the demo account being shut out of every route.
/// </summary>
public class PlaybackEndpointsTests
{
    private static readonly IServiceProvider ResultServices =
        new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();

    // ── The stream ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_stream_writes_named_events_in_camelCase_and_unregisters_when_it_ends()
    {
        var kit = new PlaybackTestKit();
        var body = new RecordingStream();
        using var aborted = new CancellationTokenSource();
        var http = NewContext(body);
        http.RequestAborted = aborted.Token;

        var result = PlaybackEndpoints.StreamPlayback(
            http, Caller(TestCurrentUserAccessor.OwnerUser), kit.Coordinator, new FakeSessions(), new FakeLifetime(),
            NullLoggerFactory.Instance,
            Mac, installId: "install-0001", name: "Safari on Mac", kind: "computer", client: "web");
        var running = result.ExecuteAsync(http);

        await body.WaitForAsync("event: snapshot");
        await kit.Claim(Alice, Mac, positionMs: 83_000);
        await body.WaitForAsync("event: session");
        await body.WaitForAsync("event: devices");

        aborted.Cancel();
        try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }

        Assert.Equal("text/event-stream", http.Response.ContentType);
        Assert.Equal("no", http.Response.Headers["X-Accel-Buffering"]);

        // The snapshot tells the client to come back within a second once the stream ends.
        Assert.StartsWith("event: snapshot\ndata: ", body.Text);
        Assert.Contains($"\nretry: {(int)PlaybackEndpoints.ReconnectAfter.TotalMilliseconds}\n", body.Text.Split("\n\n")[0] + "\n");

        var events = ParseSse(body.Text);
        Assert.Equal(PlaybackEventTypes.Snapshot, events[0].Event);

        using var snapshot = JsonDocument.Parse(events[0].Data);
        Assert.Equal(JsonValueKind.Null, snapshot.RootElement.GetProperty("session").ValueKind);
        var device = snapshot.RootElement.GetProperty("devices")[0];
        Assert.Equal(Mac, device.GetProperty("deviceId").GetString());
        Assert.Equal("install-0001", device.GetProperty("installId").GetString());
        Assert.Equal("computer", device.GetProperty("kind").GetString());
        Assert.Equal("web", device.GetProperty("client").GetString());
        Assert.True(device.GetProperty("online").GetBoolean());
        Assert.False(device.GetProperty("isActive").GetBoolean());

        var sessionEvent = Assert.Single(events, e => e.Event == PlaybackEventTypes.Session);
        using var session = JsonDocument.Parse(sessionEvent.Data);
        var s = session.RootElement.GetProperty("session");
        Assert.Equal(Mac, s.GetProperty("activeDeviceId").GetString());
        Assert.Equal(83_000, s.GetProperty("positionMs").GetInt64());
        Assert.True(s.GetProperty("live").GetBoolean());
        Assert.True(s.GetProperty("isPlaying").GetBoolean());
        Assert.Equal(1, s.GetProperty("queueIndex").GetInt32());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("lastCommandId").ValueKind);
        Assert.EndsWith("Z", s.GetProperty("updatedAtUtc").GetString());
        Assert.DoesNotContain("\"ActiveDeviceId\"", body.Text);

        var devicesEvent = events.Last(e => e.Event == PlaybackEventTypes.Devices);
        using var devices = JsonDocument.Parse(devicesEvent.Data);
        Assert.True(devices.RootElement.GetProperty("devices")[0].GetProperty("isActive").GetBoolean());

        // The finally ran: the Mac's only stream closed, so once it has not come back within the
        // grace, the session is no longer live.
        kit.PastReconnectGrace();
        var after = await kit.Snapshot(Alice);
        Assert.False(after.Session!.Live);
        Assert.Contains(after.Devices, d => d.DeviceId == Mac && !d.Online && d.IsActive);
    }

    [Fact]
    public async Task The_stream_pings_when_nothing_happens()
    {
        var kit = new PlaybackTestKit();
        await using var stream = Stream(kit, Phone, pingInterval: TimeSpan.FromMilliseconds(20)).GetAsyncEnumerator();

        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(PlaybackEventTypes.Snapshot, stream.Current.EventType);
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(PlaybackEventTypes.Ping, stream.Current.EventType);
        Assert.Equal("{}", stream.Current.Data);
    }

    [Fact]
    public async Task The_stream_ends_when_its_lifetime_is_up_and_the_client_is_told_to_come_back_at_once()
    {
        var kit = new PlaybackTestKit();
        using var mac = await kit.Connect(Alice, Mac);
        await using var stream = Stream(kit, Phone, lifetime: TimeSpan.FromMilliseconds(100)).GetAsyncEnumerator();

        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(PlaybackEndpoints.ReconnectAfter, stream.Current.ReconnectionInterval);
        Assert.True(PlaybackEndpoints.ReconnectAfter < PlaybackRules.ReconnectGrace);
        await Drain(mac);

        // A normal end, not an error: the enumeration simply completes.
        Assert.False(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        // Unregistered, but for the grace still counted as connected: no one is told anything.
        Assert.Empty(await Drain(mac));
        Assert.Contains((await kit.Snapshot(Alice)).Devices, d => d.DeviceId == Phone && d.Online);
    }

    [Fact]
    public async Task The_stream_ends_as_soon_as_the_app_starts_stopping()
    {
        var kit = new PlaybackTestKit();
        using var stopping = new CancellationTokenSource();
        await using var stream = Stream(kit, Phone, stopping: stopping.Token).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync()); // snapshot

        var next = stream.MoveNextAsync().AsTask();
        stopping.Cancel();

        Assert.False(await next.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task The_endpoint_ends_its_stream_when_the_app_stops_without_waiting_for_the_client()
    {
        // Kestrel's graceful stop waits for open requests: a stream must not hold the deploy.
        var kit = new PlaybackTestKit();
        using var stopping = new CancellationTokenSource();
        var body = new RecordingStream();
        var http = NewContext(body);
        var running = PlaybackEndpoints.StreamPlayback(
                http, Caller(TestCurrentUserAccessor.OwnerUser), kit.Coordinator, new FakeSessions(),
                new FakeLifetime(stopping.Token), NullLoggerFactory.Instance, Phone, null, "x", "phone", "web")
            .ExecuteAsync(http);
        await body.WaitForAsync("event: snapshot");

        stopping.Cancel();

        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(http.RequestAborted.IsCancellationRequested);
    }

    [Fact]
    public async Task The_stream_ends_once_the_session_that_opened_it_is_revoked()
    {
        var kit = new PlaybackTestKit();
        var sessions = new FakeSessions();
        using var mac = await kit.Connect(Alice, Mac);
        await kit.Claim(Alice, Mac);
        var check = PlaybackEndpoints.SessionCheck(NewContext(new MemoryStream()), sessions, Alice, NullLogger.Instance)!;
        await using var stream = Stream(kit, Phone, pingInterval: TimeSpan.FromMilliseconds(20), stillSignedIn: check)
            .GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync()); // snapshot
        Assert.True(await stream.MoveNextAsync()); // a ping: still signed in
        Assert.Equal(PlaybackEventTypes.Ping, stream.Current.EventType);

        sessions.Revoke(SessionId); // "log out everywhere"

        Assert.False(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        kit.PastReconnectGrace();
        Assert.DoesNotContain((await kit.Snapshot(Alice)).Devices, d => d.DeviceId == Phone);
        Assert.Equal(409, (await kit.Command(Alice, from: Mac, "transfer", target: Phone)).Error!.StatusCode);
    }

    [Fact]
    public async Task The_stream_rechecks_the_session_the_middleware_resolved()
    {
        var sessions = new FakeSessions();
        var check = PlaybackEndpoints.SessionCheck(
            NewContext(new MemoryStream()), sessions, Alice, NullLogger.Instance)!;

        Assert.True(await check(CancellationToken.None));
        Assert.Equal([SessionId], sessions.Checked);

        // Another account's id never passes, and a check that fails is not a revocation.
        var asBob = PlaybackEndpoints.SessionCheck(NewContext(new MemoryStream()), sessions, Bob, NullLogger.Instance)!;
        Assert.False(await asBob(CancellationToken.None));
        sessions.Unavailable = true;
        Assert.True(await check(CancellationToken.None));
        sessions.Unavailable = false;

        sessions.Revoke(SessionId); // "log out everywhere", an expiry or a disabled account alike
        Assert.False(await check(CancellationToken.None));
    }

    [Fact]
    public async Task A_stream_without_the_session_that_authenticated_it_is_unauthenticated()
    {
        var kit = new PlaybackTestKit();
        var http = NewContext(new MemoryStream());
        http.Items.Remove(AuthenticationMiddleware.SessionIdItemKey);

        var (status, _) = await Execute(PlaybackEndpoints.StreamPlayback(
            http, Caller(TestCurrentUserAccessor.OwnerUser), kit.Coordinator, new FakeSessions(), new FakeLifetime(),
            NullLoggerFactory.Instance, Phone, null, "x", "phone", "web"));

        Assert.Equal(401, status);
        Assert.Empty((await kit.Snapshot(Alice)).Devices);
    }

    [Fact]
    public async Task A_command_reaches_the_stream_as_a_command_event()
    {
        var kit = new PlaybackTestKit();
        using var phone = await kit.Connect(Alice, Phone);
        await using var mac = Stream(kit, Mac).GetAsyncEnumerator();
        Assert.True(await mac.MoveNextAsync()); // snapshot
        await kit.Claim(Alice, Mac);
        Assert.True(await mac.MoveNextAsync()); // session
        Assert.True(await mac.MoveNextAsync()); // devices

        var sent = await kit.Command(Alice, from: Phone, "pause");

        Assert.True(await mac.MoveNextAsync());
        Assert.Equal(PlaybackEventTypes.Command, mac.Current.EventType);
        using var command = JsonDocument.Parse(mac.Current.Data);
        Assert.Equal(sent.Value!.CommandId, command.RootElement.GetProperty("commandId").GetString());
        Assert.Equal("pause", command.RootElement.GetProperty("command").GetString());
        Assert.Equal(JsonValueKind.Null, command.RootElement.GetProperty("positionMs").ValueKind);
        Assert.Equal(Phone, command.RootElement.GetProperty("fromDeviceId").GetString());
        Assert.Equal("Safari on iPhone", command.RootElement.GetProperty("fromDeviceName").GetString());
    }

    [Theory]
    [InlineData(null, "web", "invalid_device_id")]
    [InlineData("short", "web", "invalid_device_id")]
    [InlineData(PlaybackTestKit.Mac, null, "invalid_client")]
    public async Task A_stream_without_a_valid_device_is_a_bad_request(string? deviceId, string? client, string code)
    {
        var kit = new PlaybackTestKit();
        var http = NewContext(new MemoryStream());

        var result = PlaybackEndpoints.StreamPlayback(
            http, Caller(TestCurrentUserAccessor.OwnerUser), kit.Coordinator, new FakeSessions(), new FakeLifetime(),
            NullLoggerFactory.Instance, deviceId, null, "x", "phone", client);

        var (status, json) = await Execute(result);
        Assert.Equal(400, status);
        Assert.Equal(code, json.RootElement.GetProperty("error").GetString());
        Assert.Empty((await kit.Snapshot(Alice)).Devices);
    }

    // ── Status codes ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_returns_the_session_and_devices()
    {
        var kit = new PlaybackTestKit();

        var (status, json) = await Execute(await PlaybackEndpoints.GetPlayback(
            Caller(TestCurrentUserAccessor.OwnerUser), kit.Coordinator, CancellationToken.None));

        Assert.Equal(200, status);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("session").ValueKind);
        Assert.Equal(0, json.RootElement.GetProperty("devices").GetArrayLength());
    }

    [Fact]
    public async Task A_report_answers_accepted_and_the_session()
    {
        var kit = new PlaybackTestKit();
        using var mac = await kit.Connect(Alice, Mac);

        var (status, json) = await Execute(await PlaybackEndpoints.ReportState(
            Report(Mac, claim: true, queue: [1, 2, 3], songId: 1), Caller(TestCurrentUserAccessor.OwnerUser),
            kit.Coordinator, CancellationToken.None));

        Assert.Equal(200, status);
        Assert.True(json.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal(Mac, json.RootElement.GetProperty("session").GetProperty("activeDeviceId").GetString());
    }

    [Fact]
    public async Task A_rejected_report_answers_its_error_code()
    {
        var kit = new PlaybackTestKit();

        var (status, json) = await Execute(await PlaybackEndpoints.ReportState(
            Report(Mac, claim: true, queue: null), Caller(TestCurrentUserAccessor.OwnerUser),
            kit.Coordinator, CancellationToken.None));

        Assert.Equal(400, status);
        Assert.Equal("queue_required", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Commands_answer_202_404_and_409()
    {
        var kit = new PlaybackTestKit();
        var caller = Caller(TestCurrentUserAccessor.OwnerUser);
        using var mac = await kit.Connect(Alice, Mac);
        using var phone = await kit.Connect(Alice, Phone);

        async Task<(int, JsonDocument)> Send(string command, string? target = null) =>
            await Execute(await PlaybackEndpoints.SendCommand(
                new PlaybackCommandRequest(Phone, target, command, null), caller, kit.Coordinator,
                CancellationToken.None));

        var (noSession, noSessionBody) = await Send("pause");
        Assert.Equal(404, noSession);
        Assert.Equal("no_session", noSessionBody.RootElement.GetProperty("error").GetString());

        await kit.Claim(Alice, Mac);

        var (accepted, acceptedBody) = await Send("pause");
        Assert.Equal(202, accepted);
        Assert.True(Guid.TryParse(acceptedBody.RootElement.GetProperty("commandId").GetString(), out _));

        var (notActive, notActiveBody) = await Send("next", target: Phone);
        Assert.Equal(409, notActive);
        Assert.Equal("not_active_device", notActiveBody.RootElement.GetProperty("error").GetString());

        var (offline, offlineBody) = await Send("transfer", target: Tablet);
        Assert.Equal(409, offline);
        Assert.Equal("device_offline", offlineBody.RootElement.GetProperty("error").GetString());

        var (unknown, unknownBody) = await Send("rewind");
        Assert.Equal(400, unknown);
        Assert.Equal("invalid_command", unknownBody.RootElement.GetProperty("error").GetString());
    }

    // ── Who may call it ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_route_is_closed_to_the_demo_account_and_open_to_members()
    {
        // Strangers share the demo account; they would see and pause each other. The GET too.
        foreach (var endpoint in MappedEndpoints(TestCurrentUserAccessor.DemoUser))
        {
            var (status, body) = await Invoke(endpoint.Endpoint, endpoint.Services);
            Assert.Equal(403, status);
            Assert.Contains("demo_read_only", body);
        }

        foreach (var endpoint in MappedEndpoints(TestCurrentUserAccessor.FriendUser))
        {
            var (status, _) = await Invoke(endpoint.Endpoint, endpoint.Services);
            Assert.NotEqual(403, status);
        }
    }

    [Fact]
    public async Task A_report_binds_from_the_json_a_browser_sends()
    {
        // camelCase in, and a position straight from audio.currentTime * 1000 (fractional).
        var endpoint = MappedEndpoints(TestCurrentUserAccessor.OwnerUser)
            .Single(e => e.Endpoint.RoutePattern.RawText == "/api/playback/state");
        const string json = """
            {
              "deviceId": "mac-0000-device", "installId": null, "deviceName": "Safari on Mac",
              "deviceKind": "computer", "client": "web", "claim": true, "inResponseTo": null,
              "songId": 123, "title": "Nightswim", "artist": "R.E.M.", "album": "Automatic for the People",
              "queue": [120, 123, 131], "queueIndex": 1, "positionMs": 83000.7, "durationMs": 215000.25,
              "isPlaying": false, "playbackRate": 1.0, "radioSeedId": 120, "shuffle": false
            }
            """;

        var (status, body) = await Invoke(endpoint.Endpoint, endpoint.Services, json);

        Assert.Equal(200, status);
        using var response = JsonDocument.Parse(body);
        Assert.True(response.RootElement.GetProperty("accepted").GetBoolean());
        var session = response.RootElement.GetProperty("session");
        Assert.Equal(83_001, session.GetProperty("positionMs").GetInt64());
        Assert.Equal(215_000, session.GetProperty("durationMs").GetInt64());
        Assert.Equal(120, session.GetProperty("radioSeedId").GetInt32());
        Assert.Equal("Safari on Mac", session.GetProperty("activeDeviceName").GetString());
    }

    [Fact]
    public void The_four_routes_are_mapped_where_the_clients_expect_them()
    {
        var routes = MappedEndpoints(TestCurrentUserAccessor.OwnerUser)
            .Select(e => $"{Method(e.Endpoint)} {e.Endpoint.RoutePattern.RawText!.TrimEnd('/')}")
            .Order();

        Assert.Equal(
            ["GET /api/playback", "GET /api/playback/stream", "POST /api/playback/command", "POST /api/playback/state"],
            routes);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The session <see cref="AuthenticationMiddleware"/> resolved for every request here.</summary>
    private static readonly Guid SessionId = Guid.NewGuid();

    private static ICurrentUserAccessor Caller(CurrentUser user) => new TestCurrentUserAccessor(user);

    private static DefaultHttpContext NewContext(Stream body)
    {
        var http = new DefaultHttpContext { RequestServices = ResultServices };
        http.Items[AuthenticationMiddleware.SessionIdItemKey] = SessionId;
        http.Response.Body = body;
        return http;
    }

    private static IAsyncEnumerable<System.Net.ServerSentEvents.SseItem<string>> Stream(
        PlaybackTestKit kit,
        string deviceId,
        TimeSpan? pingInterval = null,
        TimeSpan? lifetime = null,
        Func<CancellationToken, Task<bool>>? stillSignedIn = null,
        CancellationToken stopping = default) =>
        PlaybackEndpoints.StreamEventsAsync(
            kit.Coordinator, Alice, Device(deviceId),
            pingInterval ?? TimeSpan.FromSeconds(30),
            lifetime ?? TimeSpan.FromMinutes(5),
            stillSignedIn ?? (_ => Task.FromResult(true)),
            stopping,
            CancellationToken.None);

    private static async Task<(int Status, JsonDocument Json)> Execute(IResult result)
    {
        var body = new MemoryStream();
        var http = NewContext(body);
        await result.ExecuteAsync(http);
        body.Position = 0;
        return (http.Response.StatusCode, await JsonDocument.ParseAsync(body));
    }

    private static List<(RouteEndpoint Endpoint, IServiceProvider Services)> MappedEndpoints(CurrentUser user)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(new PlaybackTestKit().Coordinator);
        builder.Services.AddSingleton<ICurrentUserAccessor>(new TestCurrentUserAccessor(user));
        builder.Services.AddSingleton<IAuthService>(new FakeSessions());
        var app = builder.Build();
        app.MapPlaybackEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => (endpoint, app.Services))
            .ToList();
        return endpoints;
    }

    private static string Method(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single();

    private static async Task<(int Status, string Body)> Invoke(
        RouteEndpoint endpoint, IServiceProvider services, string postBody = "{}")
    {
        using var scope = services.CreateScope();
        using var aborted = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var body = new MemoryStream();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider, RequestAborted = aborted.Token };
        http.Items[AuthenticationMiddleware.SessionIdItemKey] = SessionId;
        http.Features.Set<IHttpRequestBodyDetectionFeature>(new HasBody());
        http.Request.Method = Method(endpoint);
        http.Response.Body = body;
        if (HttpMethods.IsPost(http.Request.Method))
        {
            http.Request.ContentType = "application/json";
            http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(postBody));
        }
        else
        {
            http.Request.QueryString = new QueryString($"?deviceId={Phone}&client=web");
        }

        try { await endpoint.RequestDelegate!(http); }
        catch (OperationCanceledException) { } // a member's stream, ended by the timeout

        return (http.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }

    private static List<(string Event, string Data)> ParseSse(string text) =>
        text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block =>
            {
                var lines = block.Split('\n');
                var evt = lines.Single(l => l.StartsWith("event: ")).Substring("event: ".Length);
                var data = string.Join("\n", lines.Where(l => l.StartsWith("data: ")).Select(l => l.Substring("data: ".Length)));
                return (evt, data);
            })
            .ToList();

    private sealed class HasBody : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class FakeLifetime(CancellationToken stopping = default) : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    /// <summary>
    /// The one thing a stream asks of <see cref="IAuthService"/>: whether its session still stands.
    /// <see cref="SessionId"/> is Alice's and live until revoked.
    /// </summary>
    private sealed class FakeSessions : IAuthService
    {
        private readonly HashSet<Guid> _revoked = [];

        public List<Guid> Checked { get; } = [];
        public bool Unavailable { get; set; }

        public void Revoke(Guid sessionId)
        {
            lock (_revoked) _revoked.Add(sessionId);
        }

        public Task<IReadOnlyList<(Session Session, User User)>> ResolveSessionsAsync(
            IReadOnlyCollection<Guid> sessionIds, CancellationToken ct)
        {
            if (Unavailable) throw new InvalidOperationException("database unavailable");
            lock (_revoked)
            {
                Checked.AddRange(sessionIds);
                var owner = new User { Id = Alice, Email = "owner@test.local", EmailNormalized = "owner@test.local" };
                IReadOnlyList<(Session, User)> live =
                [
                    .. sessionIds
                        .Where(id => id == SessionId && !_revoked.Contains(id))
                        .Select(id => (new Session { Id = id, UserId = Alice }, owner)),
                ];
                return Task.FromResult(live);
            }
        }

        public Task<(Session Session, User User)?> ResolveSessionAsync(Guid sessionId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<RequestLinkResult?> RequestLinkAsync(string email, string frontendBaseUrl, string? client, string? ip, string? userAgent, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<Session?> ConsumeLinkAsync(string rawToken, string? ip, string? userAgent, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<Session?> StartDemoSessionAsync(string? ip, string? userAgent, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<Session?> CreateDeviceSessionAsync(Guid userId, string? ip, string? userAgent, CancellationToken ct)
            => throw new NotSupportedException();

        public Task RevokeAsync(Guid sessionId, bool allForUser, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<InviteMintResult?> CreateOrRotateInviteAsync(Guid ownerUserId, string email, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<InvitePeekResult?> PeekInviteAsync(string rawToken, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<Session?> AcceptInviteAsync(string rawToken, string? ip, string? userAgent, CancellationToken ct)
            => throw new NotSupportedException();
    }

    /// <summary>A response body another thread can wait on while the stream is still being written.</summary>
    private sealed class RecordingStream : Stream
    {
        private readonly object _gate = new();
        private readonly MemoryStream _inner = new();

        public string Text
        {
            get { lock (_gate) return Encoding.UTF8.GetString(_inner.ToArray()); }
        }

        public async Task WaitForAsync(string fragment)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!Text.Contains(fragment))
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"'{fragment}' was never written. Got: {Text}");
                await Task.Delay(10);
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_gate) _inner.Write(buffer, offset, count);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            lock (_gate) _inner.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
