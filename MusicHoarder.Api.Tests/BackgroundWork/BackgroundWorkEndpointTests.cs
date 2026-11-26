using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.BackgroundWork;

namespace MusicHoarder.Api.Tests.BackgroundWork;

public class BackgroundWorkEndpointTests
{
    private static async Task<JsonDocument> Read(IResult result)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(context.Response.Body);
    }

    [Fact]
    public void Every_job_id_has_exactly_one_insight()
    {
        using var harness = new BackgroundWorkHarness();
        using var scope = harness.Scope();

        var ids = scope.ServiceProvider.GetServices<IBackgroundJobInsight>().Select(i => i.Id).ToList();

        Assert.Equal(BackgroundJobIds.All.OrderBy(i => i), ids.OrderBy(i => i));
    }

    [Fact]
    public async Task The_list_answers_every_job_in_the_pipeline_pages_order_with_a_14_day_trend()
    {
        using var harness = new BackgroundWorkHarness();
        using var scope = harness.Scope();

        var result = await BackgroundWorkEndpoints.GetSummaries(
            scope.ServiceProvider.GetServices<IBackgroundJobInsight>(),
            NullLogger<IBackgroundJobInsight>.Instance,
            CancellationToken.None);

        using var doc = await Read(result);
        var jobs = doc.RootElement.GetProperty("jobs").EnumerateArray().ToList();
        Assert.Equal(BackgroundJobIds.All, jobs.Select(j => j.GetProperty("id").GetString()));
        Assert.All(jobs, j => Assert.Equal(14, j.GetProperty("trend").GetArrayLength()));
        Assert.All(jobs, j => Assert.False(string.IsNullOrWhiteSpace(j.GetProperty("stateLabel").GetString())));
    }

    [Theory]
    [InlineData(BackgroundJobIds.Lyrics)]
    [InlineData(BackgroundJobIds.TranscriptionAllowance)]
    [InlineData(BackgroundJobIds.CoverArt)]
    [InlineData(BackgroundJobIds.MusicVideos)]
    [InlineData(BackgroundJobIds.MusicVideoDownloads)]
    [InlineData(BackgroundJobIds.QualityGrading)]
    [InlineData(BackgroundJobIds.EnrichmentRetries)]
    [InlineData(BackgroundJobIds.AlbumFillAndFollows)]
    public async Task A_job_on_an_empty_library_still_describes_itself_with_panels(string id)
    {
        using var harness = new BackgroundWorkHarness();
        using var scope = harness.Scope();

        var result = await BackgroundWorkEndpoints.GetDetail(id, scope.ServiceProvider.GetServices<IBackgroundJobInsight>(), CancellationToken.None);

        using var doc = await Read(result);
        Assert.Equal(id, doc.RootElement.GetProperty("summary").GetProperty("id").GetString());
        var panels = doc.RootElement.GetProperty("panels").EnumerateArray().ToList();
        Assert.NotEmpty(panels);
        Assert.All(panels, p => Assert.Contains(p.GetProperty("kind").GetString(), new[] { "breakdown", "series", "library-map", "bars", "figures" }));
    }

    [Fact]
    public async Task An_unknown_job_is_a_404()
    {
        using var harness = new BackgroundWorkHarness();
        using var scope = harness.Scope();

        var result = await BackgroundWorkEndpoints.GetDetail("nope", scope.ServiceProvider.GetServices<IBackgroundJobInsight>(), CancellationToken.None);

        Assert.IsType<NotFound>(result);
    }

    private sealed class Broken(string id) : IBackgroundJobInsight
    {
        public string Id => id;
        public Task<BackgroundJobSummaryDto> SummarizeAsync(CancellationToken ct) => throw new InvalidOperationException("boom");
        public Task<BackgroundJobDetailDto> DescribeAsync(CancellationToken ct) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task One_job_that_cannot_be_read_does_not_blank_the_list()
    {
        using var harness = new BackgroundWorkHarness();
        using var scope = harness.Scope();
        var insights = scope.ServiceProvider.GetServices<IBackgroundJobInsight>()
            .Where(i => i.Id != BackgroundJobIds.CoverArt)
            .Append(new Broken(BackgroundJobIds.CoverArt));

        var result = await BackgroundWorkEndpoints.GetSummaries(
            insights, NullLogger<IBackgroundJobInsight>.Instance, CancellationToken.None);

        using var doc = await Read(result);
        var jobs = doc.RootElement.GetProperty("jobs").EnumerateArray().ToList();
        Assert.Equal(BackgroundJobIds.All.Count, jobs.Count);
        var cover = jobs.Single(j => j.GetProperty("id").GetString() == BackgroundJobIds.CoverArt);
        Assert.Equal("unknown", cover.GetProperty("state").GetString());
    }
}
