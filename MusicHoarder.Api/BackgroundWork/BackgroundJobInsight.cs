using System.Globalization;

namespace MusicHoarder.Api.BackgroundWork;

public interface IBackgroundJobInsight
{
    string Id { get; }

    /// <summary>The job's row: cheap enough to ask for every job on each poll.</summary>
    Task<BackgroundJobSummaryDto> SummarizeAsync(CancellationToken ct);

    Task<BackgroundJobDetailDto> DescribeAsync(CancellationToken ct);
}

public sealed record BackgroundJobDefinition(string Id, string Title, string Description);

public static class BackgroundJobDefinitions
{
    public static readonly BackgroundJobDefinition Lyrics = new(
        BackgroundJobIds.Lyrics,
        "Lyrics",
        "Looks up lyrics, checks that their timing fits the audio, and gives songs with no synced lyrics AI-timed ones.");

    public static readonly BackgroundJobDefinition TranscriptionAllowance = new(
        BackgroundJobIds.TranscriptionAllowance,
        "Transcription allowance",
        "The audio the lyrics sweeps may send to the transcription provider, against its hourly and daily limits.");

    public static readonly BackgroundJobDefinition CoverArt = new(
        BackgroundJobIds.CoverArt,
        "Cover art",
        "Fetches covers for built albums that have none, and upgrades Cover Art Archive covers to Spotify's.");

    public static readonly BackgroundJobDefinition MusicVideos = new(
        BackgroundJobIds.MusicVideos,
        "Music video search",
        "Searches YouTube for every song's music video, My tracks first, and again on a schedule, so choosing one opens on a measured list.");

    public static readonly BackgroundJobDefinition MusicVideoDownloads = new(
        BackgroundJobIds.MusicVideoDownloads,
        "Music video downloads",
        "Downloads the video the search is confident about, within a size limit and a free-space floor, and the ones you pick by hand.");

    public static readonly BackgroundJobDefinition QualityGrading = new(
        BackgroundJobIds.QualityGrading,
        "AI quality grading",
        "Has a language model judge whether each enriched song's metadata is right.");

    public static readonly BackgroundJobDefinition EnrichmentRetries = new(
        BackgroundJobIds.EnrichmentRetries,
        "Enrichment retries",
        "Asks providers again for songs they rate-limited or found nothing for.");

    public static readonly BackgroundJobDefinition AlbumFillAndFollows = new(
        BackgroundJobIds.AlbumFillAndFollows,
        "Album completion and follows",
        "Queues the missing tracks of albums you part-own, and the new releases of artists you follow.");

    public static readonly IReadOnlyList<BackgroundJobDefinition> All =
        [Lyrics, TranscriptionAllowance, CoverArt, MusicVideos, MusicVideoDownloads, QualityGrading, EnrichmentRetries, AlbumFillAndFollows];
}

public static class BackgroundJobSummaries
{
    public static BackgroundJobSummaryDto Build(
        BackgroundJobDefinition job,
        string? offReason,
        JobRuntime? runtime,
        DateTime? lastWorkedAtUtc,
        BackgroundHeadlineDto headline,
        IReadOnlyList<double> trend,
        string trendLabel,
        string idleLabel = "Nothing to do right now.")
    {
        var (state, label, nextRun) = offReason is not null
            ? (BackgroundJobState.Off, offReason, (DateTime?)null)
            : runtime is null
                ? (BackgroundJobState.Unknown, "Nothing reported since the API started.", null)
                : runtime.State switch
                {
                    BackgroundJobState.Working => (runtime.State, "Working through a pass now.", null),
                    BackgroundJobState.Waiting => (runtime.State, runtime.Reason ?? "Waiting.", runtime.UntilUtc),
                    BackgroundJobState.Failing => (runtime.State, $"The last pass failed ({runtime.Reason}).", runtime.NextRunAtUtc),
                    _ => (BackgroundJobState.Idle, idleLabel, runtime.NextRunAtUtc),
                };

        var worked = new[] { lastWorkedAtUtc, runtime?.LastWorkedAtUtc }.Max();
        return new BackgroundJobSummaryDto(
            job.Id, job.Title, job.Description, state, label, worked, nextRun, headline, trend, trendLabel);
    }
}

public static class BackgroundText
{
    public static string Count(double n) => ((long)Math.Round(n)).ToString("N0", CultureInfo.InvariantCulture);

    public static string Bytes(double bytes)
    {
        const double Kib = 1024;
        var b = Math.Max(0, bytes);
        if (b >= Kib * Kib * Kib * Kib)
            return $"{(b / (Kib * Kib * Kib * Kib)).ToString("0.0", CultureInfo.InvariantCulture)} TB";
        if (b >= Kib * Kib * Kib)
        {
            var gib = b / (Kib * Kib * Kib);
            return $"{gib.ToString(gib < 10 ? "0.0" : "0", CultureInfo.InvariantCulture)} GB";
        }
        if (b >= Kib * Kib)
            return $"{(b / (Kib * Kib)).ToString("0", CultureInfo.InvariantCulture)} MB";
        return $"{Math.Max(1, Math.Round(b / Kib)).ToString("0", CultureInfo.InvariantCulture)} KB";
    }

    public static string Duration(double seconds)
    {
        var s = (long)Math.Round(Math.Max(0, seconds));
        var h = s / 3600;
        var m = s % 3600 / 60;
        if (h > 0)
            return m > 0 ? $"{h}h {m}m" : $"{h}h";
        return m > 0 ? $"{m}m" : $"{s}s";
    }
}
