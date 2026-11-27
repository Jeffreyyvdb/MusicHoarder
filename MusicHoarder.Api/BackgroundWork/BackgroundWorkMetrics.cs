using System.Diagnostics.Metrics;
using MusicHoarder.Api.Auth;

namespace MusicHoarder.Api.BackgroundWork;

/// <summary>
/// The Pipeline page's job rows as OpenTelemetry gauges. They read one snapshot that a collection
/// refreshes in the background when it is older than <see cref="MaxAge"/>, so an instance with no OTLP
/// reader (nothing collects) runs no extra queries. Tags are the job's wire id and, for the state gauge,
/// the state: never a label, caption or reason, which are free text. The job tag is not named <c>job</c>
/// because Prometheus's OTLP ingestion sets its own <c>job</c> label.
/// </summary>
public sealed class BackgroundWorkMetrics
{
    /// <summary>Meter name; must match the <c>AddMeter</c> registration in ServiceDefaults.</summary>
    public const string MeterName = "MusicHoarder.BackgroundWork";

    public const string JobTag = "background_job";
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    private sealed record Snapshot(IReadOnlyList<BackgroundJobSummaryDto> Jobs, DateTimeOffset TakenAt);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly ILogger<BackgroundWorkMetrics> _logger;
    private volatile Snapshot? _snapshot;
    private int _refreshing;

    public BackgroundWorkMetrics(
        IMeterFactory meterFactory, IServiceScopeFactory scopes, TimeProvider clock, ILogger<BackgroundWorkMetrics> logger)
    {
        _scopes = scopes;
        _clock = clock;
        _logger = logger;
        var meter = meterFactory.Create(MeterName);

        meter.CreateObservableGauge("mh.background.job.state", StateMeasurements,
            description: "1 for the state a background job is in and 0 for each other state.");
        meter.CreateObservableGauge("mh.background.job.headline", () => PerJob(j => j.Headline.Value),
            description: "The figure a background job's row leads with.");
        meter.CreateObservableGauge("mh.background.job.headline_of", () => PerJob(j => j.Headline.Of),
            description: "The whole the headline figure is part of, when it is a part.");
        meter.CreateObservableGauge("mh.background.job.done_today", () => PerJob(j => j.Trend.Count == 0 ? null : j.Trend[^1]),
            description: "Work a background job has done so far today (UTC).");
        meter.CreateObservableGauge("mh.background.job.last_worked_timestamp_seconds", () => PerJob(j => UnixSeconds(j.LastWorkedAtUtc)),
            unit: "s", description: "When a background job last did useful work, as Unix time.");
        meter.CreateObservableGauge("mh.background.job.eta_timestamp_seconds", () => PerJob(j => UnixSeconds(j.Headline.EtaUtc)),
            unit: "s", description: "When a background job's backlog runs out at the recent rate, as Unix time.");
    }

    /// <summary>The latest refresh, running or finished. Tests await it.</summary>
    internal Task? Refresh { get; private set; }

    private IReadOnlyList<BackgroundJobSummaryDto>? Jobs()
    {
        var snapshot = _snapshot;
        var stale = snapshot is null || _clock.GetUtcNow() - snapshot.TakenAt > MaxAge;
        if (stale && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
            Refresh = Task.Run(RefreshAsync);
        return snapshot?.Jobs;
    }

    private async Task RefreshAsync()
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<HttpContextCurrentUserAccessor>()
                .ActAs(new CurrentUser(WellKnownUsers.OwnerId, "", UserRole.Admin, null));
            var jobs = await BackgroundWorkEndpoints.ReadSummariesAsync(
                scope.ServiceProvider.GetServices<IBackgroundJobInsight>(), _logger, CancellationToken.None);
            _snapshot = new Snapshot(jobs, _clock.GetUtcNow());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background work: could not refresh the metrics snapshot");
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    private IEnumerable<Measurement<double>> StateMeasurements()
    {
        foreach (var job in Jobs() ?? [])
            foreach (var state in BackgroundJobState.All)
                yield return new Measurement<double>(
                    state == job.State ? 1 : 0,
                    new KeyValuePair<string, object?>(JobTag, job.Id),
                    new KeyValuePair<string, object?>("state", state));
    }

    private IEnumerable<Measurement<double>> PerJob(Func<BackgroundJobSummaryDto, double?> value)
    {
        foreach (var job in Jobs() ?? [])
            if (value(job) is { } v)
                yield return new Measurement<double>(v, new KeyValuePair<string, object?>(JobTag, job.Id));
    }

    private static double? UnixSeconds(DateTime? utc) =>
        utc is { } t ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeSeconds() : null;
}
