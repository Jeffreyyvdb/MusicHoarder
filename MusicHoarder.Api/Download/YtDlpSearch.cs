using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Download;

internal enum YtDlpSearchStatus
{
    Picked,
    /// <summary>yt-dlp ran and YouTube listed nothing (or its failure reads like "no results").</summary>
    NoResults,
    /// <summary>Every listed hit is a clean edit nobody asked for.</summary>
    OnlyCleanEdits,
    Failed,
}

internal sealed record YtDlpSearchOutcome(
    YtDlpSearchStatus Status,
    MusicVideoDownloader.SearchCandidate? Picked,
    IReadOnlyList<MusicVideoDownloader.SearchCandidate> Skipped,
    int ExitCode,
    string Stderr);

/// <summary>
/// The YouTube search the wishlist downloader and the catalog's full-song audition share, so both
/// land on the same video for the same track: query building, a flat yt-dlp search, and the pick
/// that skips unrequested clean edits (<see cref="CleanEditLabel"/>).
/// </summary>
internal static class YtDlpSearch
{
    /// <summary>How many YouTube results are considered before giving up on finding a non-clean one.</summary>
    internal const int ResultCount = 5;

    /// <summary>
    /// The yt-dlp search argument: artist and title with the title's clean-edit label dropped, since
    /// asking for the label would steer YouTube toward the very edit we are avoiding. The
    /// <c>ytsearchN:</c> prefix means the value never starts with '-', so it cannot be read as a flag.
    /// </summary>
    internal static string BuildQuery(DownloadRequest req)
    {
        var title = CleanEditLabel.Strip(req.Title);
        if (title.Length == 0)
            title = req.Title.Trim();
        var terms = string.IsNullOrWhiteSpace(req.Artist) ? title : $"{req.Artist} {title}";
        return $"ytsearch{ResultCount}:{terms.Trim()}";
    }

    /// <summary>
    /// The first of <paramref name="candidates"/>, in YouTube's order, that is not an unrequested clean
    /// edit of <paramref name="req"/>, plus the ones skipped on the way. Null when every hit is a clean edit.
    /// </summary>
    internal static (MusicVideoDownloader.SearchCandidate? Picked, List<MusicVideoDownloader.SearchCandidate> Skipped) Pick(
        IEnumerable<MusicVideoDownloader.SearchCandidate> candidates, DownloadRequest req)
    {
        var skipped = new List<MusicVideoDownloader.SearchCandidate>();
        foreach (var candidate in candidates)
        {
            if (!CleanEditLabel.IsUnrequestedCleanEdit(candidate.Title, req.Artist, req.Title))
                return (candidate, skipped);
            skipped.Add(candidate);
        }
        return (null, skipped);
    }

    /// <summary>Flat search (no per-video fetch) and pick. The caller logs and maps the outcome.</summary>
    internal static async Task<YtDlpSearchOutcome> RunAsync(
        MusicEnricherOptions opts, DownloadRequest req, string? cookiesPath, CancellationToken ct)
    {
        var psi = YtDlpProcess.Create(opts, cookiesPath, includeThrottle: false);
        psi.ArgumentList.Add("--flat-playlist");
        psi.ArgumentList.Add("-J");
        psi.ArgumentList.Add(BuildQuery(req));

        var (exitCode, stdout, stderr) = await YtDlpProcess.RunAsync(psi, ct);
        var candidates = MusicVideoDownloader.ParseFlatSearch(stdout);

        if (candidates.Count == 0)
        {
            var status = exitCode == 0 || YtDlpDownloadProvider.LooksLikeNoResults(stderr)
                ? YtDlpSearchStatus.NoResults
                : YtDlpSearchStatus.Failed;
            return new YtDlpSearchOutcome(status, null, [], exitCode, stderr);
        }

        var (picked, skipped) = Pick(candidates, req);
        return new YtDlpSearchOutcome(
            picked is null ? YtDlpSearchStatus.OnlyCleanEdits : YtDlpSearchStatus.Picked,
            picked, skipped, exitCode, stderr);
    }
}
