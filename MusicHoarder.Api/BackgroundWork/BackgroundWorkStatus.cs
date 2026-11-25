namespace MusicHoarder.Api.BackgroundWork;

public sealed record JobRuntime(
    string State,
    string? Reason,
    DateTime? UntilUtc,
    DateTime? NextRunAtUtc,
    DateTime ReportedAtUtc,
    DateTime? LastWorkedAtUtc);

public sealed class BackgroundWorkStatus(TimeProvider time)
{
    private const int MaxReasonLength = 200;

    private readonly Dictionary<(string Job, string Source), JobRuntime> _reports = [];
    private readonly object _lock = new();

    public void Working(string jobId, string source = "")
        => Set(jobId, source, now => (BackgroundJobState.Working, null, null, null));

    public void Idle(string jobId, DateTime? nextRunAtUtc, bool worked = false, string source = "")
        => Set(jobId, source, now => (BackgroundJobState.Idle, null, null, nextRunAtUtc), worked);

    public void Waiting(string jobId, string reason, DateTime? untilUtc, string source = "")
        => Set(jobId, source, now => (BackgroundJobState.Waiting, Truncate(reason), untilUtc, untilUtc));

    public void Failing(string jobId, Exception error, DateTime? nextRunAtUtc, string source = "")
        => Set(jobId, source, now => (BackgroundJobState.Failing, error.GetType().Name, null, nextRunAtUtc));

    public JobRuntime? Get(string jobId)
    {
        List<JobRuntime> reports;
        lock (_lock)
            reports = [.. _reports.Where(kv => kv.Key.Job == jobId).Select(kv => kv.Value)];
        if (reports.Count == 0)
            return null;

        var lead = reports
            .OrderBy(r => Rank(r.State))
            .ThenByDescending(r => r.ReportedAtUtc)
            .First();
        var next = lead.State == BackgroundJobState.Idle
            ? reports.Where(r => r.State == BackgroundJobState.Idle).Min(r => r.NextRunAtUtc)
            : lead.NextRunAtUtc;
        return lead with
        {
            NextRunAtUtc = next,
            LastWorkedAtUtc = reports.Max(r => r.LastWorkedAtUtc),
        };
    }

    private static int Rank(string state) => state switch
    {
        BackgroundJobState.Failing => 0,
        BackgroundJobState.Working => 1,
        BackgroundJobState.Waiting => 2,
        _ => 3,
    };

    private void Set(
        string jobId,
        string source,
        Func<DateTime, (string State, string? Reason, DateTime? Until, DateTime? Next)> report,
        bool worked = false)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var (state, reason, until, next) = report(now);
        lock (_lock)
        {
            _reports.TryGetValue((jobId, source), out var previous);
            _reports[(jobId, source)] = new JobRuntime(
                state, reason, until, next, now, worked ? now : previous?.LastWorkedAtUtc);
        }
    }

    private static string Truncate(string text)
        => text.Length <= MaxReasonLength ? text : text[..MaxReasonLength];
}
