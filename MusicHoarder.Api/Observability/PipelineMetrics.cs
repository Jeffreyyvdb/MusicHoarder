using System.Diagnostics.Metrics;

namespace MusicHoarder.Api.Observability;

/// <summary>
/// OpenTelemetry instruments for the ingest-pipeline domain: enrichment queue depth, stage-cycle
/// durations, and terminal song outcomes. Exported only when an OTLP endpoint is configured (the Aspire
/// dashboard in dev); a no-op otherwise. Tags are strictly low-cardinality (<c>stage</c>, <c>outcome</c>)
/// — never per-song or per-owner, to keep cardinality bounded and avoid leaking tenant data.
/// </summary>
public sealed class PipelineMetrics
{
    /// <summary>Meter name — must match the <c>AddMeter</c> registration in ServiceDefaults.</summary>
    public const string MeterName = "MusicHoarder.Pipeline";

    private readonly Counter<long> _terminal;
    private readonly Histogram<double> _stageDuration;
    private Func<int> _queueDepthProvider = static () => 0;

    public PipelineMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        meter.CreateObservableGauge(
            "mh.enrich.queue_depth",
            () => _queueDepthProvider(),
            unit: "{songs}",
            description: "Songs currently in flight in the enrichment cycle.");

        _terminal = meter.CreateCounter<long>(
            "mh.song.terminal",
            unit: "{songs}",
            description: "Songs reaching a terminal pipeline outcome, tagged by outcome.");

        _stageDuration = meter.CreateHistogram<double>(
            "mh.stage.duration_seconds",
            unit: "s",
            description: "Duration of a pipeline stage cycle, tagged by stage.");
    }

    /// <summary>Wire the observable queue-depth gauge to a live source (the enrichment channel's in-flight count).</summary>
    public void SetQueueDepthProvider(Func<int> provider) => _queueDepthProvider = provider;

    /// <summary>Record <paramref name="count"/> songs reaching a terminal outcome (e.g. matched, needs_review, failed, built, build_failed).</summary>
    public void RecordTerminal(string outcome, long count = 1)
    {
        if (count <= 0) return;
        _terminal.Add(count, new KeyValuePair<string, object?>("outcome", outcome));
    }

    /// <summary>Record the wall-clock duration of one stage cycle (e.g. stage=build, stage=enrich).</summary>
    public void RecordStageDuration(string stage, double seconds) =>
        _stageDuration.Record(seconds, new KeyValuePair<string, object?>("stage", stage));
}
