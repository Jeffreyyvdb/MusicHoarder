using Microsoft.Extensions.Options;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Soulseek;

/// <summary>
/// Downloads a track from the Soulseek network via a user-operated slskd instance. Searches, elects
/// the best candidates (<see cref="SlskdCandidateSelector"/>), and fetches them via
/// <see cref="SlskdFileFetcher"/> — trying the next-best peer when one stalls or errors. The
/// finished file lands in the request's destination (the normal download staging dir), so the
/// caller's tag-stamp + scan pipeline applies unchanged.
/// <para>
/// Unconfigured or no acceptable result ⇒ <see cref="DownloadResult.Missing"/> so the provider chain
/// falls through (e.g. to yt-dlp). Only a transport-level slskd failure returns
/// <see cref="DownloadResult.Failed"/>, which stops the chain — a flaky slskd shouldn't silently burn
/// the fallback provider's quota on every track.
/// </para>
/// </summary>
public class SlskdDownloadProvider(
    SlskdFileFetcher fetcher,
    IOptionsMonitor<SlskdOptions> options,
    ILogger<SlskdDownloadProvider> logger) : IDownloadProvider
{
    public string Name => "slskd";

    public async Task<DownloadResult> DownloadAsync(DownloadRequest req, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        if (!opts.IsConfigured)
            return DownloadResult.Missing("slskd not configured");

        try
        {
            var responses = await fetcher.SearchAsync(BuildSearchQuery(req.Artist, req.Title), ct);
            var candidates = SlskdCandidateSelector.Select(responses, req.Title, req.DurationMs, opts);

            // Retry pass: some peers only share album folders whose paths carry the album name but
            // abbreviate the track title. An artist+album search surfaces those; the selector's
            // title-token filter still picks the right file out of the folder.
            if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(req.Album))
            {
                responses = await fetcher.SearchAsync(BuildSearchQuery(req.Artist, req.Album!), ct);
                candidates = SlskdCandidateSelector.Select(responses, req.Title, req.DurationMs, opts);
            }

            if (candidates.Count == 0)
            {
                logger.LogInformation("slskd found no acceptable candidate for '{Artist} - {Title}'",
                    LogSanitizer.ForLog(req.Artist), LogSanitizer.ForLog(req.Title));
                return DownloadResult.Missing("no acceptable Soulseek result");
            }

            string? lastError = null;
            foreach (var candidate in candidates.Take(Math.Max(1, opts.MaxCandidateAttempts)))
            {
                ct.ThrowIfCancellationRequested();
                var outcome = await fetcher.FetchCandidateAsync(candidate, req.DestinationDirectory, ct);
                if (outcome.Success)
                {
                    logger.LogInformation("slskd downloaded '{Artist} - {Title}' from {Username} ({Size} bytes)",
                        LogSanitizer.ForLog(req.Artist), LogSanitizer.ForLog(req.Title),
                        LogSanitizer.ForLog(candidate.Username), candidate.File.Size);
                    return outcome;
                }
                lastError = outcome.Error;
            }

            // Candidates existed but none delivered (busy queues, dead peers). NotFound —
            // not a slskd fault — so the chain may fall through and the sweep can retry later.
            return DownloadResult.Missing(lastError ?? "all Soulseek candidates failed");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "slskd download failed for '{Artist} - {Title}'",
                LogSanitizer.ForLog(req.Artist), LogSanitizer.ForLog(req.Title));
            return DownloadResult.Failed(ex.Message);
        }
    }

    internal static string BuildSearchQuery(string artist, string term) =>
        SoulseekSearchQuery.Build(artist, term);
}
