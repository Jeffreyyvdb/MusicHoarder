using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Snapshots;

namespace MusicHoarder.Api.Version;

/// <summary>Cached result of the most recent GitHub "latest release" lookup.</summary>
/// <param name="LatestTag">The release's tag as published (<c>v2.7.0</c>), for the commit comparison.</param>
/// <param name="RunningBuildHasLatest">
/// For an edge build only: whether its commit already holds the latest release (GitHub's compare
/// answered identical or ahead). Null when not asked or when GitHub could not answer.
/// </param>
public record ReleaseUpdateSnapshot(
    string? LatestVersion,
    string? ReleaseUrl,
    DateTime? PublishedAt,
    DateTime CheckedAtUtc,
    string? LatestTag = null,
    bool? RunningBuildHasLatest = null);

public interface IReleaseUpdateMonitor
{
    /// <summary>The most recent successful lookup. Never blocks; safe to read on a request thread.</summary>
    ReleaseUpdateSnapshot Current { get; }
}

/// <summary>
/// Periodically polls the GitHub Releases API for the project's latest release so the frontend can
/// surface a "new version available" banner to self-hosters whose deployment tooling doesn't auto-update
/// (e.g. Arcane on TrueNAS). The poll is process-wide and ETag-cached: one app instance hits GitHub at
/// most a few times a day regardless of how many users are connected, staying well under the 60/hr
/// unauthenticated rate limit. All network failures are swallowed (last good snapshot kept) so an
/// air-gapped or offline deploy degrades to "no banner" rather than erroring.
/// </summary>
public class ReleaseUpdateMonitor(
    IHttpClientFactory httpClientFactory,
    IOptions<MusicEnricherOptions> options,
    ILogger<ReleaseUpdateMonitor> logger)
    : BackgroundService, IReleaseUpdateMonitor
{
    public const string HttpClientName = "github-releases";

    private volatile ReleaseUpdateSnapshot _current = new(
        LatestVersion: null,
        ReleaseUrl: null,
        PublishedAt: null,
        CheckedAtUtc: DateTime.MinValue);

    private string? _etag;

    public ReleaseUpdateSnapshot Current => _current;

    /// <summary>The running build's version; settable so tests can stand in an edge build.</summary>
    internal string RunningVersion { get; init; } = EnrichmentSnapshotService.ResolveVersion(null);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.EnableUpdateCheck)
        {
            // Disabled: leave the snapshot empty so /api/version/latest reports no update, and never
            // touch the network. Useful for air-gapped deploys.
            return;
        }

        var interval = TimeSpan.FromHours(options.Value.UpdateCheckIntervalHours);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken);
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Release update check failed unexpectedly");
                try { await Task.Delay(interval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await RefreshLatestReleaseAsync(cancellationToken);
        await CheckRunningBuildAsync(cancellationToken);
    }

    private async Task RefreshLatestReleaseAsync(CancellationToken cancellationToken)
    {
        var repo = options.Value.UpdateCheckRepo;
        var client = httpClientFactory.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"repos/{repo}/releases/latest");
        if (!string.IsNullOrEmpty(_etag))
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(_etag));

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Network blip / DNS failure / timeout: keep the last good snapshot, try again next cycle.
            logger.LogWarning(ex, "Could not reach GitHub to check for a newer release of {Repo}", repo);
            return;
        }

        using (response)
        {
            // 304: nothing changed since the last successful poll — the cached snapshot still holds.
            if (response.StatusCode == HttpStatusCode.NotModified)
                return;

            if (!response.IsSuccessStatusCode)
            {
                // 404 = repo has no published release yet; 403 = rate limited. Either way keep last good.
                logger.LogWarning(
                    "GitHub release check for {Repo} returned {StatusCode}", repo, (int)response.StatusCode);
                return;
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (TryParseRelease(payload, out var snapshot))
            {
                _etag = response.Headers.ETag?.Tag;
                _current = snapshot;
                logger.LogInformation(
                    "Latest published release of {Repo} is {Version}", repo, snapshot.LatestVersion);
            }
        }
    }

    /// <summary>
    /// An edge build carries the previous release's number until the next build after a release, though
    /// the release may have been cut from the very commit it runs. Ask GitHub how the commit relates to
    /// the latest release's tag, once per release: the answer cannot change while this process runs.
    /// Any failure leaves the answer unknown (no banner) and is tried again on the next cycle.
    /// </summary>
    private async Task CheckRunningBuildAsync(CancellationToken cancellationToken)
    {
        var snapshot = _current;
        if (snapshot.RunningBuildHasLatest is not null || string.IsNullOrEmpty(snapshot.LatestTag)) return;
        var commit = SemVerComparer.EdgeCommit(RunningVersion);
        if (commit is null || !SemVerComparer.IsUpdateAvailable(RunningVersion, snapshot.LatestVersion)) return;

        var repo = options.Value.UpdateCheckRepo;
        var client = httpClientFactory.CreateClient(HttpClientName);
        var path = $"repos/{repo}/compare/{Uri.EscapeDataString(snapshot.LatestTag)}...{commit}?per_page=1";

        string? status;
        try
        {
            using var response = await client.GetAsync(path, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "GitHub compare of {Tag} with running commit {Commit} returned {StatusCode}",
                    snapshot.LatestTag, commit, (int)response.StatusCode);
                return;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            status = doc.RootElement.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not compare {Tag} with running commit {Commit}", snapshot.LatestTag, commit);
            return;
        }

        // "ahead" / "identical": the running commit descends from the tag. "behind" / "diverged": it does not.
        bool? hasLatest = status switch
        {
            "identical" or "ahead" => true,
            "behind" or "diverged" => false,
            _ => null,
        };
        if (hasLatest is null) return;

        // Only this loop writes _current, so the snapshot read above is still the current one.
        _current = snapshot with { RunningBuildHasLatest = hasLatest };
        logger.LogInformation(
            "Running edge commit {Commit} is {Status} of {Tag}", commit, status, snapshot.LatestTag);
    }

    private static bool TryParseRelease(string json, out ReleaseUpdateSnapshot snapshot)
    {
        snapshot = null!;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagEl)) return false;
            var tag = tagEl.GetString();
            if (string.IsNullOrWhiteSpace(tag)) return false;

            var latest = tag.Trim();
            if (latest.Length > 0 && (latest[0] == 'v' || latest[0] == 'V'))
                latest = latest[1..];

            string? releaseUrl = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() : null;
            DateTime? publishedAt =
                root.TryGetProperty("published_at", out var pubEl) && pubEl.TryGetDateTime(out var dt)
                    ? dt.ToUniversalTime()
                    : null;

            snapshot = new ReleaseUpdateSnapshot(latest, releaseUrl, publishedAt, DateTime.UtcNow, tag.Trim());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
