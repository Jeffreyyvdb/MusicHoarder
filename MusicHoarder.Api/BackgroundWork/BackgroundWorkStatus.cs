namespace MusicHoarder.Api.BackgroundWork;

/// <summary>What one job, merged over its services, last reported. Never persisted: it is the API's own memory.</summary>
public sealed record JobRuntime(
    string State,
    string? Reason,
    DateTime? UntilUtc,
    DateTime? NextRunAtUtc,
    DateTime ReportedAtUtc,
    DateTime? LastWorkedAtUtc);

/// <summary>
/// The one thing a job's rows cannot tell: what its service is doing this minute. Each background service
/// reports here at the points where it starts a pass, ends one, is told "not now", or throws; everything else
/// on the Background work page is read from the rows the jobs already write. A job nobody has reported on
/// since the API started is <see cref="BackgroundJobState.Unknown"/>, never guessed. "Off" is not reported:
/// it is read from options and runtime settings when the page is asked for.
/// </summary>
/// <remarks>
/// A job may have several services (album fill and the artist monitor), each its own <c>source</c>; they
/// merge by what matters most: failing, then working, then waiting, then idle.
/// </remarks>
public sealed class BackgroundWorkStatus(TimeProvider time)
{
    private const int MaxReasonLength = 200;

    private readonly Dictionary<(string Job, string Source), JobRuntime> _reports = [];
    private readonly object _lock = new();

    public void Working(string jobId, string source = "")
        => Set(jobId, source, now => (BackgroundJobState.Working, null, null, null));

    /// <param name="worked">The pass that just ended did useful work; moves "last worked".</param>
    public void Idle(string jobId, DateTime? nextRunAtUtc, bool worked = false, string source = "")
        => Set(jobId, source, now => (BackgroundJobState.Idle, null, null, nextRunAtUtc), worked);

    /// <param name="reason">A sentence without clock times; the moment goes in <paramref name="untilUtc"/>.</param>
    public void Waiting(string jobId, string reason, DateTime? untilUtc, string source = "")
        => Set(jobId, source, now => (BackgroundJobState.Waiting, Truncate(reason), untilUtc, untilUtc));

    /// <summary>Takes the exception's type, never its message: a message can carry a URL or a path.</summary>
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
