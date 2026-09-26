using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Import;

/// <summary>
/// One video on a YouTube playlist, as the flat listing reports it. The listing carries the raw video
/// title and length but no channel, so the artist usually has to come from a per-video probe.
/// </summary>
public sealed record YouTubePlaylistEntry(string VideoId, string Title, int DurationMs);

/// <summary>A YouTube playlist: its name and cover, how many videos YouTube says it holds, and the ones read.</summary>
public sealed record YouTubePlaylist(
    string Id,
    string Title,
    string? ThumbnailUrl,
    int VideoCount,
    IReadOnlyList<YouTubePlaylistEntry> Entries);

/// <summary>
/// Outcome of a playlist read: the <see cref="Playlist"/>, or a failure carrying an owner-facing
/// <see cref="Hint"/> ("That playlist does not exist or is private…") and the tail of yt-dlp's stderr.
/// </summary>
public sealed record YouTubePlaylistOutcome(YouTubePlaylist? Playlist, string? Hint, string? Detail)
{
    public bool Ok => Playlist is not null;
    public static YouTubePlaylistOutcome Success(YouTubePlaylist p) => new(p, null, null);
    public static YouTubePlaylistOutcome Failed(string? hint, string? detail) => new(null, hint, detail);
}

public interface IYouTubePlaylistReader
{
    /// <summary>
    /// Lists a YouTube playlist without downloading anything. <paramref name="maxEntries"/> null reads
    /// every video; a small number reads only the first page, which is enough for the name, cover and
    /// count (the link preview and a new source's row).
    /// </summary>
    Task<YouTubePlaylistOutcome> ReadAsync(string playlistId, int? maxEntries, CancellationToken ct);
}

/// <summary>
/// Reads a YouTube playlist with <c>yt-dlp --flat-playlist --dump-single-json</c>: one request per
/// hundred videos, none per video. Shares the downloader's yt-dlp path, cookies and extra args, so a
/// playlist the server can download from is one it can list — and a private playlist becomes readable
/// when the cookies belong to the account that owns it.
/// </summary>
public sealed class YouTubePlaylistReader(
    IOptions<MusicEnricherOptions> options,
    ILogger<YouTubePlaylistReader> logger) : IYouTubePlaylistReader
{
    // A long playlist pages in continuations of 100; give a big one room, but never hang a sync.
    private static readonly TimeSpan FullReadTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PreviewTimeout = TimeSpan.FromSeconds(45);

    public async Task<YouTubePlaylistOutcome> ReadAsync(string playlistId, int? maxEntries, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(playlistId)) return YouTubePlaylistOutcome.Failed(null, null);
        var opts = options.Value;
        var url = PlaylistUrl(playlistId.Trim());

        var cookiesPath = YtDlpCookies.PrepareWritableCopy(opts.YtDlpCookiesPath, logger);
        try
        {
            var psi = YtDlpProcess.Create(opts, cookiesPath, includeThrottle: false);
            psi.ArgumentList.Add("--flat-playlist");
            psi.ArgumentList.Add("--dump-single-json");
            psi.ArgumentList.Add("--no-warnings");
            if (maxEntries is { } max)
            {
                psi.ArgumentList.Add("--playlist-end");
                psi.ArgumentList.Add(Math.Max(1, max).ToString());
            }
            psi.ArgumentList.Add(url);

            var (timedOut, exitCode, stdout, stderr) = await YtDlpProcess.RunAsync(
                psi, maxEntries is null ? FullReadTimeout : PreviewTimeout, ct);
            if (timedOut)
            {
                logger.LogWarning("yt-dlp timed out listing YouTube playlist {PlaylistId}", LogSanitizer.ForLog(playlistId));
                return YouTubePlaylistOutcome.Failed("Timed out reading the playlist.", null);
            }

            // Trust parseable stdout whatever the exit code: yt-dlp can print the JSON and then crash
            // on exit (saving a read-only cookies file), exactly as in the single-video probe.
            var parsed = string.IsNullOrWhiteSpace(stdout) ? null : Parse(stdout);
            if (parsed is not null)
                return YouTubePlaylistOutcome.Success(parsed);

            logger.LogInformation(
                "yt-dlp could not list YouTube playlist {PlaylistId} (exit {Code}): {Error}",
                LogSanitizer.ForLog(playlistId), exitCode, LogSanitizer.ForLog(YtDlpErrors.Tail(stderr)));
            return YouTubePlaylistOutcome.Failed(YtDlpErrors.Classify(stderr), YtDlpErrors.Tail(stderr));
        }
        catch (Win32Exception ex)
        {
            logger.LogWarning(ex, "yt-dlp binary unavailable for playlist listing ({Path})", LogSanitizer.ForLog(opts.YtDlpPath));
            return YouTubePlaylistOutcome.Failed("yt-dlp is not installed on the server.", null);
        }
        finally
        {
            YtDlpCookies.Cleanup(cookiesPath, opts.YtDlpCookiesPath);
        }
    }

    /// <summary>Canonical playlist page URL for a <c>list=</c> id.</summary>
    public static string PlaylistUrl(string playlistId) =>
        $"https://www.youtube.com/playlist?list={Uri.EscapeDataString(playlistId)}";

    /// <summary>
    /// Parses the flat listing. Entries YouTube shows as "[Private video]" / "[Deleted video]" are
    /// dropped: they carry no metadata and cannot be downloaded, and the playlist page usually hides
    /// them anyway.
    /// </summary>
    internal static YouTubePlaylist? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (GetString(root, "_type") is { } type && type != "playlist") return null;

            var id = GetString(root, "id");
            if (string.IsNullOrWhiteSpace(id)) return null;

            var entries = new List<YouTubePlaylistEntry>();
            if (root.TryGetProperty("entries", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in list.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    var videoId = GetString(entry, "id");
                    var title = GetString(entry, "title")?.Trim() ?? "";
                    if (!ImportUrlParser.IsYouTubeVideoId(videoId) || IsUnavailablePlaceholder(title)) continue;

                    var durationMs = entry.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                        ? (int)Math.Round(d.GetDouble() * 1000)
                        : 0;
                    entries.Add(new YouTubePlaylistEntry(videoId!, title, durationMs));
                }
            }

            var count = root.TryGetProperty("playlist_count", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32()
                : entries.Count;

            return new YouTubePlaylist(
                id,
                GetString(root, "title")?.Trim() is { Length: > 0 } t ? t : id,
                PickThumbnail(root),
                Math.Max(count, entries.Count),
                entries);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsUnavailablePlaceholder(string title) =>
        title is "[Private video]" or "[Deleted video]" or "[Unavailable video]";

    /// <summary>
    /// The playlist cover: yt-dlp lists thumbnails worst → best, so the last real one. An empty
    /// playlist reports YouTube's generic "no_thumbnail" image, which is no cover at all.
    /// </summary>
    private static string? PickThumbnail(JsonElement root)
    {
        if (!root.TryGetProperty("thumbnails", out var thumbs) || thumbs.ValueKind != JsonValueKind.Array)
            return null;
        string? best = null;
        foreach (var thumb in thumbs.EnumerateArray())
        {
            if (thumb.ValueKind != JsonValueKind.Object) continue;
            var url = GetString(thumb, "url");
            if (string.IsNullOrWhiteSpace(url) || url.Contains("no_thumbnail", StringComparison.OrdinalIgnoreCase)) continue;
            best = url;
        }
        return best;
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
