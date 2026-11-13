using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MusicHoarder.Api.Library;

namespace MusicHoarder.Api.Tests.Library;

/// <summary>
/// The decisions the conditional GET makes, against a stamp the test controls. The one outcome that
/// must never happen is a 304 for a copy that is not the current one, so most of these are about
/// when it does <i>not</i> answer 304.
/// </summary>
public class LibraryConditionalGetFilterTests
{
    [Fact]
    public async Task A_first_request_gets_the_list_and_a_weak_tag()
    {
        var call = await Run(stamp: "s1");

        Assert.True(call.HandlerRan);
        Assert.Equal(200, call.Status);
        Assert.StartsWith("W/\"", call.Etag);
        Assert.EndsWith("\"", call.Etag);
    }

    [Fact]
    public async Task Sending_the_tag_back_gets_304_without_running_the_handler()
    {
        var first = await Run(stamp: "s1");

        var second = await Run(stamp: "s1", ifNoneMatch: first.Etag);

        Assert.False(second.HandlerRan);
        Assert.Equal(304, second.Status);
        // The 304 names the tag it confirmed, so a client that lost track of it can pick it up again.
        Assert.Equal(first.Etag, second.Etag);
    }

    [Fact]
    public async Task A_changed_stamp_gets_the_list_again_under_a_new_tag()
    {
        var first = await Run(stamp: "s1");

        var second = await Run(stamp: "s2", ifNoneMatch: first.Etag);

        Assert.True(second.HandlerRan);
        Assert.Equal(200, second.Status);
        Assert.NotEqual(first.Etag, second.Etag);
    }

    [Theory]
    [InlineData("/songs", "?includeDeleted=true")]
    [InlineData("/api/albums", "")]
    [InlineData("/api/albums", "?builtOnly=false&merge=false")]
    public async Task A_tag_from_one_list_never_matches_another_variant(string path, string query)
    {
        var songs = await Run(stamp: "s1", path: "/songs", query: "");

        var other = await Run(stamp: "s1", path: path, query: query, ifNoneMatch: songs.Etag);

        Assert.True(other.HandlerRan);
        Assert.Equal(200, other.Status);
        Assert.NotEqual(songs.Etag, other.Etag);
    }

    [Fact]
    public async Task The_tag_is_matched_weakly_and_anywhere_in_a_list()
    {
        var first = await Run(stamp: "s1");
        var strongForm = first.Etag!["W/".Length..];

        Assert.Equal(304, (await Run(stamp: "s1", ifNoneMatch: strongForm)).Status);
        Assert.Equal(304, (await Run(stamp: "s1", ifNoneMatch: $"\"other\", {first.Etag}")).Status);
    }

    [Theory]
    [InlineData("*")] // says "I hold a copy", not which one
    [InlineData("\"some-other-tag\"")]
    [InlineData("not a tag at all")]
    [InlineData("")]
    public async Task Anything_but_the_current_tag_gets_the_list(string ifNoneMatch)
    {
        var call = await Run(stamp: "s1", ifNoneMatch: ifNoneMatch);

        Assert.True(call.HandlerRan);
        Assert.Equal(200, call.Status);
    }

    [Fact]
    public async Task Without_a_stamp_the_response_carries_no_tag_and_is_never_304()
    {
        var first = await Run(stamp: "s1");

        var call = await Run(new FakeStamp(() => null), ifNoneMatch: first.Etag);

        Assert.True(call.HandlerRan);
        Assert.Equal(200, call.Status);
        Assert.Null(call.Etag);
    }

    [Fact]
    public async Task The_stamp_is_read_before_the_handler_reads_the_library()
    {
        // The order is the safety property: a tag read after the rows could be newer than them, and
        // the next request would then be told that stale rows are current.
        var order = new List<string>();
        var stamp = new FakeStamp(() =>
        {
            order.Add("stamp");
            return "s1";
        });

        await Run(stamp, handler: () =>
        {
            order.Add("handler");
            return Results.Ok(new { Count = 0 });
        });

        Assert.Equal(["stamp", "handler"], order);
    }

    [Fact]
    public async Task A_response_that_is_not_the_list_carries_no_tag()
    {
        var call = await Run(new FakeStamp(() => "s1"), handler: () => Results.NotFound());

        Assert.Equal(404, call.Status);
        Assert.Null(call.Etag);
    }

    [Fact]
    public async Task A_handler_that_throws_leaves_no_tag_on_the_response()
    {
        var http = NewContext("/songs", "", ifNoneMatch: null, new FakeStamp(() => "s1"));
        var filter = new LibraryConditionalGetFilter();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await filter.InvokeAsync(
                new DefaultEndpointFilterInvocationContext(http),
                _ => throw new InvalidOperationException("database is down")));

        Assert.False(http.Response.Headers.ContainsKey("ETag"));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private sealed record Call(int Status, string? Etag, bool HandlerRan);

    private sealed class FakeStamp(Func<string?> value) : ILibraryChangeStamp
    {
        public Task<string?> ComputeAsync(CancellationToken ct) => Task.FromResult(value());
    }

    private static Task<Call> Run(
        string stamp, string path = "/songs", string query = "", string? ifNoneMatch = null) =>
        Run(new FakeStamp(() => stamp), path, query, ifNoneMatch);

    private static async Task<Call> Run(
        ILibraryChangeStamp stamp,
        string path = "/songs",
        string query = "",
        string? ifNoneMatch = null,
        Func<IResult>? handler = null)
    {
        var http = NewContext(path, query, ifNoneMatch, stamp);
        var handlerRan = false;

        var result = await new LibraryConditionalGetFilter().InvokeAsync(
            new DefaultEndpointFilterInvocationContext(http),
            _ =>
            {
                handlerRan = true;
                return ValueTask.FromResult<object?>((handler ?? (() => Results.Ok(new { Count = 0 })))());
            });

        await Assert.IsAssignableFrom<IResult>(result).ExecuteAsync(http);
        var etag = http.Response.Headers.ETag.ToString();
        return new Call(http.Response.StatusCode, etag.Length > 0 ? etag : null, handlerRan);
    }

    private static DefaultHttpContext NewContext(
        string path, string query, string? ifNoneMatch, ILibraryChangeStamp stamp)
    {
        var services = new ServiceCollection().AddLogging().AddOptions();
        services.AddSingleton(stamp);
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Request.Method = HttpMethods.Get;
        http.Request.Path = path;
        http.Request.QueryString = new QueryString(query);
        if (ifNoneMatch is not null)
            http.Request.Headers.IfNoneMatch = ifNoneMatch;
        http.Response.Body = new MemoryStream();
        return http;
    }
}
