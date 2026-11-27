using System.ComponentModel;
using System.Globalization;
using System.Web;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Import;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Catalog;

/// <summary>
/// Finds the same video the wishlist downloader would pick (<see cref="YtDlpSearch"/>) and asks
/// yt-dlp for its AAC audio URL without downloading anything. m4a/AAC is what Safari plays.
/// </summary>
public sealed class YtDlpAudioLocator(
    IOptions<MusicEnricherOptions> options,
    ILogger<YtDlpAudioLocator> logger) : ICatalogAudioLocator
{
    internal const string AudioFormat = "bestaudio[ext=m4a]/bestaudio[acodec^=mp4a]";
    private const string FallbackContentType = "audio/mp4";

    public async Task<ResolvedAudio> LocateAsync(DownloadRequest track, CancellationToken ct)
    {
        var opts = options.Value;
        var cookiesPath = YtDlpCookies.PrepareWritableCopy(opts.YtDlpCookiesPath, logger);
        try
        {
            var search = await YtDlpSearch.RunAsync(opts, track, cookiesPath, ct);
            switch (search.Status)
            {
                case YtDlpSearchStatus.NoResults:
                    throw new CatalogResolveException(CatalogResolveFailure.NotFound, "YouTube has nothing for this track.");
                case YtDlpSearchStatus.OnlyCleanEdits:
                    throw new CatalogResolveException(CatalogResolveFailure.NotFound, "YouTube only has clean edits of this track.");
                case YtDlpSearchStatus.Failed:
                    logger.LogWarning("yt-dlp search exited {Code}: {Error}", search.ExitCode, LogSanitizer.ForLog(YtDlpErrors.Tail(search.Stderr)));
                    throw new CatalogResolveException(CatalogResolveFailure.YtDlpFailed, YtDlpErrors.Describe(search.ExitCode, search.Stderr));
            }

            var picked = search.Picked!;
            var watchUrl = ImportUrlParser.YouTubeWatchUrl(picked.Id);
            var (exitCode, stdout, stderr) = await ExtractAsync(opts, watchUrl, cookiesPath, ct);

            // Same recovery as the downloader: cookies can cost every usable format, so try anonymously once.
            if (exitCode != 0 && cookiesPath is not null && YtDlpErrors.LooksLikeNoUsableFormats(stderr))
            {
                logger.LogInformation("yt-dlp had no usable formats for {VideoId} with cookies; retrying anonymously", picked.Id);
                var anonymous = await ExtractAsync(opts, watchUrl, null, ct);
                if (anonymous.ExitCode == 0)
                    (exitCode, stdout, stderr) = anonymous;
            }

            if (exitCode != 0)
            {
                logger.LogWarning("yt-dlp url extraction exited {Code} for {VideoId}: {Error}", exitCode, picked.Id, LogSanitizer.ForLog(YtDlpErrors.Tail(stderr)));
                throw new CatalogResolveException(CatalogResolveFailure.YtDlpFailed, YtDlpErrors.Describe(exitCode, stderr));
            }

            var streamUrl = FirstLine(stdout);
            if (!CatalogHosts.IsGoogleVideo(streamUrl))
                throw new CatalogResolveException(CatalogResolveFailure.YtDlpFailed, "yt-dlp did not return a YouTube audio URL.");

            return new ResolvedAudio(
                streamUrl!,
                ContentTypeOf(streamUrl!),
                picked.Id,
                picked.Title,
                picked.DurationSeconds is { } seconds ? (int)Math.Round(seconds * 1000) : null);
        }
        catch (Win32Exception ex)
        {
            logger.LogWarning(ex, "yt-dlp could not be started");
            throw new CatalogResolveException(CatalogResolveFailure.YtDlpMissing, "yt-dlp is not available on this server.");
        }
        finally
        {
            YtDlpCookies.Cleanup(cookiesPath, opts.YtDlpCookiesPath);
        }
    }

    private static Task<(int ExitCode, string Stdout, string Stderr)> ExtractAsync(
        MusicEnricherOptions opts, string watchUrl, string? cookiesPath, CancellationToken ct)
    {
        var psi = YtDlpProcess.Create(opts, cookiesPath, includeThrottle: false);
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(AudioFormat);
        psi.ArgumentList.Add("-g");
        psi.ArgumentList.Add(watchUrl);
        return YtDlpProcess.RunAsync(psi, ct);
    }

    internal static string? FirstLine(string stdout) =>
        stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    /// <summary>googlevideo URLs carry their media type as <c>mime=audio%2Fmp4</c>.</summary>
    internal static string ContentTypeOf(string streamUrl)
    {
        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri))
            return FallbackContentType;
        var mime = HttpUtility.ParseQueryString(uri.Query)["mime"];
        return mime is not null && mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            ? mime.ToLower(CultureInfo.InvariantCulture)
            : FallbackContentType;
    }
}
