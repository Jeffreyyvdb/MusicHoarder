using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Composition;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;

namespace MusicHoarder.Api.Tests.Endpoints;

/// <summary>
/// The two library lists as mapped, run through their endpoint filters: that <c>GET /songs</c> and
/// <c>GET /api/albums</c> really carry the conditional GET, that a 304 has no body, and that a
/// client which sends no <c>If-None-Match</c> — every shipped Android build — is answered exactly
/// as before. The stamp is a fake; the decisions are pinned in <c>LibraryConditionalGetFilterTests</c>.
/// </summary>
public class LibraryListsConditionalGetTests
{
    [Theory]
    [InlineData("/songs", "")]
    [InlineData("/api/albums", "")]
    [InlineData("/api/albums", "?builtOnly=false&merge=false")]
    public async Task The_list_is_sent_once_and_then_confirmed_with_304(string route, string query)
    {
        var app = await NewApp(() => "library-v1");

        var first = await Get(app, route, query);
        Assert.Equal(200, first.Status);
        Assert.NotNull(first.Etag);
        Assert.Equal(1, JsonDocument.Parse(first.Body).RootElement.GetProperty("count").GetInt32());

        var second = await Get(app, route, query, ifNoneMatch: first.Etag);
        Assert.Equal(304, second.Status);
        Assert.Equal(first.Etag, second.Etag);
        Assert.Empty(second.Body);
    }

    [Theory]
    [InlineData("/songs")]
    [InlineData("/api/albums")]
    public async Task A_change_to_the_library_sends_the_list_again(string route)
    {
        var stamp = "library-v1";
        var app = await NewApp(() => stamp);
        var first = await Get(app, route);

        stamp = "library-v2";
        var second = await Get(app, route, ifNoneMatch: first.Etag);

        Assert.Equal(200, second.Status);
        Assert.NotEqual(first.Etag, second.Etag);
        Assert.Equal(first.Body, second.Body);
    }

    [Fact]
    public async Task The_two_lists_do_not_answer_to_each_others_tag()
    {
        var app = await NewApp(() => "library-v1");
        var songs = await Get(app, "/songs");

        Assert.Equal(200, (await Get(app, "/api/albums", ifNoneMatch: songs.Etag)).Status);
        Assert.Equal(200, (await Get(app, "/songs", "?includeDeleted=true", ifNoneMatch: songs.Etag)).Status);
    }

    [Theory]
    [InlineData("/songs")]
    [InlineData("/api/albums")]
    public async Task A_client_that_sends_no_validator_gets_the_same_body_as_before(string route)
    {
        var plain = await Get(await NewApp(stamp: null), route);
        var tagged = await Get(await NewApp(() => "library-v1"), route);

        Assert.Equal(200, tagged.Status);
        Assert.Equal(plain.Body, tagged.Body);
        Assert.Null(plain.Etag);
    }

    [Theory]
    [InlineData("/songs")]
    [InlineData("/api/albums")]
    public async Task On_the_in_memory_provider_the_real_stamp_sends_no_tag(string route)
    {
        // No fake here: the registered LibraryChangeStamp, which has no row versions to read
        // outside Postgres. Every other endpoint test relies on this staying a plain 200.
        var app = await NewApp(stamp: null, useRealStamp: true);

        var call = await Get(app, route, ifNoneMatch: "W/\"anything\"");

        Assert.Equal(200, call.Status);
        Assert.Null(call.Etag);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private sealed record Call(int Status, string? Etag, string Body);

    private sealed class FakeStamp(Func<string?> value) : ILibraryChangeStamp
    {
        public Task<string?> ComputeAsync(CancellationToken ct) => Task.FromResult(value());
    }

    /// <summary>
    /// The production service registrations and the two real route tables, over an in-memory
    /// database holding one built song for the admin.
    /// </summary>
    private static async Task<WebApplication> NewApp(Func<string?>? stamp, bool useRealStamp = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MusicEnricher:SourceDirectory"] = "/source",
            ["MusicEnricher:DestinationDirectory"] = "/dest",
        });
        builder.Services.AddMusicHoarderServices();
        builder.Services.AddSingleton<ICurrentUserAccessor>(
            new TestCurrentUserAccessor(TestCurrentUserAccessor.OwnerUser));
        var database = Guid.NewGuid().ToString();
        builder.Services.AddDbContext<MusicHoarderDbContext>(options => options.UseInMemoryDatabase(database));
        if (!useRealStamp)
            builder.Services.AddSingleton<ILibraryChangeStamp>(new FakeStamp(stamp ?? (() => null)));

        var app = builder.Build();
        app.MapSongsEndpoints();
        app.MapAlbumsEndpoints();

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        db.Songs.Add(new SongMetadata
        {
            Id = 1,
            OwnerUserId = TestUsers.OwnerId,
            SourcePath = "/source/one-more-time.flac",
            FileName = "one-more-time.flac",
            Extension = ".flac",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UnixEpoch,
            IndexedAtUtc = DateTime.UnixEpoch,
            Artist = "Daft Punk",
            Album = "Discovery",
            Title = "One More Time",
            LibraryBuildStatus = LibraryBuildStatus.Done,
            DestinationPath = "/dest/Daft Punk/Discovery/01 One More Time.flac",
        });
        await db.SaveChangesAsync();
        return app;
    }

    private static async Task<Call> Get(
        WebApplication app, string route, string query = "", string? ifNoneMatch = null)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == route
                && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(HttpMethods.Get));

        using var scope = app.Services.CreateScope();
        var body = new MemoryStream();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Method = HttpMethods.Get;
        http.Request.Path = route;
        http.Request.QueryString = new QueryString(query);
        if (ifNoneMatch is not null)
            http.Request.Headers.IfNoneMatch = ifNoneMatch;
        http.Response.Body = body;

        await endpoint.RequestDelegate!(http);

        var etag = http.Response.Headers.ETag.ToString();
        return new Call(
            http.Response.StatusCode,
            etag.Length > 0 ? etag : null,
            Encoding.UTF8.GetString(body.ToArray()));
    }
}
