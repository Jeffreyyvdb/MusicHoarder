using System.Text.Json;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.BackgroundWork.Charts;

namespace MusicHoarder.Api.BackgroundWork;

/// <summary>
/// <c>GET /api/background-work</c> (one row per job) and <c>GET /api/background-work/{id}</c> (a row and its
/// charts). Admin only: the transcription ledger, option values and runtime state are instance-wide, and the
/// counts are the library owner's.
/// </summary>
public static class BackgroundWorkEndpoints
{
    /// <summary>The wire options: the web defaults, so property names are camelCase and panels carry their <c>kind</c>.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapBackgroundWorkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/background-work", GetSummaries).WithName("GetBackgroundWork").RequireAdmin();
        app.MapGet("/api/background-work/{id}", GetDetail).WithName("GetBackgroundJob").RequireAdmin();
        return app;
    }

    internal static async Task<IResult> GetSummaries(
        IEnumerable<IBackgroundJobInsight> insights,
        ILogger<IBackgroundJobInsight> logger,
        CancellationToken ct)
    {
        var byId = insights.ToDictionary(i => i.Id);
        var jobs = new List<BackgroundJobSummaryDto>();
        foreach (var id in BackgroundJobIds.All)
        {
            if (!byId.TryGetValue(id, out var insight))
                continue;
            try
            {
                jobs.Add(await insight.SummarizeAsync(ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One job's broken query must not blank the whole section.
                logger.LogWarning(ex, "Background work: could not summarize job {JobId}", id);
                jobs.Add(Unreadable(id));
            }
        }

        return Results.Json(new BackgroundWorkSummaryDto(DateTime.UtcNow, jobs), Json);
    }

    internal static async Task<IResult> GetDetail(
        string id,
        IEnumerable<IBackgroundJobInsight> insights,
        CancellationToken ct)
    {
        var insight = insights.FirstOrDefault(i => i.Id == id);
        if (insight is null)
            return Results.NotFound();
        return Results.Json(await insight.DescribeAsync(ct), Json);
    }

    private static BackgroundJobSummaryDto Unreadable(string id)
    {
        var definition = BackgroundJobDefinitions.All.First(d => d.Id == id);
        return new BackgroundJobSummaryDto(
            id, definition.Title, definition.Description,
            BackgroundJobState.Unknown, "This job's figures could not be read.", null, null,
            new BackgroundHeadlineDto(0, null, BackgroundFormat.Count, "", null, null),
            new double[TimeBuckets.TrendDays], "");
    }
}
