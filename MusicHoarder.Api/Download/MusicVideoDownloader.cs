using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Import;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Download;

/// <summary>
/// One music-video fetch. <paramref name="PinnedIdOrUrl"/> pins a specific YouTube video (id or any
/// parseable URL). <paramref name="PinIsExplicit"/> distinguishes a URL the owner typed into the
/// video field (always honored verbatim) from a provenance pin (the video the audio came from) —
/// provenance pins are probed first and REJECTED when they look like an audio-only upload
/// ("Official Audio", lyric videos, auto-generated "- Topic" tracks), falling back to a real
/// music-video search; the fingerprint aligner recovers sync for the swapped source.
/// <paramref name="DurationMs"/> (the song's length, when known) sanity-checks search candidates.
/// </summary>
public record MusicVideoFetchRequest(
    string? PinnedIdOrUrl,
    bool PinIsExplicit,
    string Artist,
    string Title,
    int? DurationMs = null);

/// <summary>
/// Outcome of a music-video download. <paramref name="NotFound"/> mirrors
/// <see cref="DownloadResult.NotFound"/>: "no result for this query" vs a transient error.
/// </summary>
public record MusicVideoDownloadResult(
    bool Success,
    string? FilePath,
    string? YouTubeVideoId,
    int? DurationSeconds,
    string? Error,
    bool NotFound)
{
    public static MusicVideoDownloadResult Ok(string filePath, string? videoId, int? durationSeconds) =>
        new(true, filePath, videoId, durationSeconds, null, false);
    public static MusicVideoDownloadResult Failed(string error) => new(false, null, null, null, error, false);
    public static MusicVideoDownloadResult Missing(string? error = null) => new(false, null, null, null, error, true);
}

public interface IMusicVideoDownloader
{
    /// <summary>Downloads a track's music video (mp4) from YouTube into the videos directory.</summary>
    Task<MusicVideoDownloadResult> DownloadAsync(MusicVideoFetchRequest request, CancellationToken ct);

    /// <summary>The resolved videos directory (creates nothing; empty when downloads are unconfigured).</summary>
    string ResolveVideoDirectory();
}

/// <summary>
/// Downloads music videos ("clips") from YouTube via yt-dlp, independent of the audio download — so
/// audio acquired from slskd/spotiflac (or already in the library) can still get a companion clip.
/// The mp4 keeps its own audio track (useful standalone) but the player plays it muted, slaved to
/// the library song's audio via the per-song sync offset on <c>SongMusicVideo</c>. Reuses the audio
/// provider's cookies/throttle/extra-args handling and degrades gracefully when the binary is
/// missing.
/// </summary>
public class MusicVideoDownloader(
    IOptions<MusicEnricherOptions> options,
    ILogger<MusicVideoDownloader> logger) : IMusicVideoDownloader
{
    private const int SearchCandidateCount = 6;

    public string ResolveVideoDirectory()
    {
        var opts = options.Value;
        if (!string.IsNullOrWhiteSpace(opts.MusicVideoDirectory))
            return opts.MusicVideoDirectory;
        return string.IsNullOrWhiteSpace(opts.DownloadDirectory)
            ? string.Empty
            : Path.Combine(opts.DownloadDirectory, "videos");
    }

    public async Task<MusicVideoDownloadResult> DownloadAsync(MusicVideoFetchRequest request, CancellationToken ct)
    {
        var opts = options.Value;
        var directory = ResolveVideoDirectory();
        if (string.IsNullOrWhiteSpace(directory))
            return MusicVideoDownloadResult.Failed("no video directory configured (MusicEnricher:DownloadDirectory / MusicVideoDirectory)");

        var cookiesPath = YtDlpCookies.PrepareWritableCopy(opts.YtDlpCookiesPath, logger);
        try
        {
            Directory.CreateDirectory(directory);

            var target = await ResolveTargetAsync(request, cookiesPath, ct);
            if (target is null)
                return MusicVideoDownloadResult.Missing("no music video found");

            return await DownloadTargetAsync(target, directory, cookiesPath, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Missing binary lands here — degrade gracefully, the audio is the product.
            logger.LogWarning(ex, "music video download failed for '{Artist} - {Title}'",
                LogSanitizer.ForLog(request.Artist), LogSanitizer.ForLog(request.Title));
            return MusicVideoDownloadResult.Failed(ex.Message);
        }
        finally
        {
            YtDlpCookies.Cleanup(cookiesPath, opts.YtDlpCookiesPath);
        }
    }

    /// <summary>
    /// Resolves what to download: an explicit pin verbatim; a provenance pin unless it looks like an
    /// audio-only upload (then a search); else the best-scoring search candidate.
    /// </summary>
    private async Task<string?> ResolveTargetAsync(
        MusicVideoFetchRequest request, string? cookiesPath, CancellationToken ct)
    {
        var pinned = CanonicalizePin(request.PinnedIdOrUrl);
        if (pinned is not null)
        {
            if (request.PinIsExplicit)
                return pinned;

            // Provenance pin: the audio's own source video guarantees offset-0 sync, but a wishlist
            // download's source is very often an "Official Audio"/topic upload — a static cover
            // image, useless as a backdrop. Probe cheaply; on any probe failure keep the pin.
            var probe = await ProbeTitleChannelAsync(pinned, cookiesPath, ct);
            if (probe is null || !LooksLikeAudioOnlyUpload(probe.Value.Title, probe.Value.Channel))
                return pinned;

            logger.LogInformation(
                "Music video pin for '{Artist} - {Title}' looks audio-only ('{VideoTitle}') — searching for a real video instead",
                LogSanitizer.ForLog(request.Artist), LogSanitizer.ForLog(request.Title), LogSanitizer.ForLog(probe.Value.Title));
        }

        var candidates = await SearchCandidatesAsync(request.Artist, request.Title, cookiesPath, ct);
        var best = PickBestCandidate(candidates, request.DurationMs, TitleTokens(request.Artist, request.Title));
        if (best is null)
        {
            // Nothing plausible for THIS song. If we rejected an audio-only provenance pin above,
            // fall back to it: a static cover in perfect sync beats no video and beats a random
            // other song's clip.
            if (pinned is not null)
            {
                logger.LogInformation(
                    "Music video search found nothing better for '{Artist} - {Title}' — keeping the audio-only source video",
                    LogSanitizer.ForLog(request.Artist), LogSanitizer.ForLog(request.Title));
                return pinned;
            }
            return null;
        }

        logger.LogInformation(
            "Music video search for '{Artist} - {Title}' picked '{VideoTitle}' ({VideoId})",
            LogSanitizer.ForLog(request.Artist), LogSanitizer.ForLog(request.Title),
            LogSanitizer.ForLog(best.Title), best.Id);
        return ImportUrlParser.YouTubeWatchUrl(best.Id);
    }

    private async Task<MusicVideoDownloadResult> DownloadTargetAsync(
        string target, string directory, string? cookiesPath, CancellationToken ct)
    {
        var opts = options.Value;
        var stem = Guid.NewGuid().ToString("N");
        var outputTemplate = Path.Combine(directory, stem + ".%(ext)s");

        var psi = NewYtDlp(cookiesPath, includeThrottle: true);
        // h264/mp4 first for broadest <video> playback support; height cap is the disk/traffic
        // relief valve. The /best tails accept whatever exists rather than failing the fetch.
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(BuildFormat(opts.MusicVideoMaxHeight));
        psi.ArgumentList.Add("--merge-output-format");
        psi.ArgumentList.Add("mp4");
        // Resolved id + duration on stdout (one per line); --no-simulate keeps downloading.
        psi.ArgumentList.Add("--no-simulate");
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("%(id)s");
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("%(duration)s");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outputTemplate);
        psi.ArgumentList.Add(target);

        var (exitCode, stdout, stderr) = await RunAsync(psi, ct);
        var produced = YtDlpDownloadProvider.LocateProducedFile(directory, stem);

        if (produced is not null)
        {
            var (videoId, duration) = ParsePrinted(stdout);
            return MusicVideoDownloadResult.Ok(produced, videoId, duration);
        }

        if (exitCode == 0 || YtDlpDownloadProvider.LooksLikeNoResults(stderr))
        {
            logger.LogInformation("yt-dlp found no music video for '{Target}': {Error}",
                LogSanitizer.ForLog(target), LogSanitizer.ForLog(Truncate(stderr)));
            return MusicVideoDownloadResult.Missing(stderr.Length == 0 ? "no results" : Truncate(stderr));
        }

        logger.LogWarning("yt-dlp video fetch exited {Code} for '{Target}': {Error}",
            exitCode, LogSanitizer.ForLog(target), LogSanitizer.ForLog(Truncate(stderr)));
        return MusicVideoDownloadResult.Failed($"exited {exitCode}: {Truncate(stderr)}");
    }

    /// <summary>Cheap metadata probe (no download): the pinned video's title + channel. Null on any failure.</summary>
    private async Task<(string Title, string Channel)?> ProbeTitleChannelAsync(
        string url, string? cookiesPath, CancellationToken ct)
    {
        try
        {
            var psi = NewYtDlp(cookiesPath, includeThrottle: false);
            psi.ArgumentList.Add("--skip-download");
            psi.ArgumentList.Add("--print");
            psi.ArgumentList.Add("%(title)s");
            psi.ArgumentList.Add("--print");
            psi.ArgumentList.Add("%(channel)s");
            psi.ArgumentList.Add(url);

            var (_, stdout, _) = await RunAsync(psi, ct);
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // Trust parseable stdout regardless of exit code (yt-dlp can print then crash on exit).
            return lines.Length >= 1 ? (lines[0], lines.Length >= 2 ? lines[1] : "") : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Music video pin probe failed — keeping the pin");
            return null;
        }
    }

    internal sealed record SearchCandidate(string Id, string Title, string Channel, double? DurationSeconds);

    /// <summary>Flat-playlist search: fast (no per-video page fetch), returns id/title/channel/duration.</summary>
    private async Task<List<SearchCandidate>> SearchCandidatesAsync(
        string artist, string title, string? cookiesPath, CancellationToken ct)
    {
        var terms = BuildSearchTerms(artist, title);
        var psi = NewYtDlp(cookiesPath, includeThrottle: false);
        psi.ArgumentList.Add("--flat-playlist");
        psi.ArgumentList.Add("-J");
        psi.ArgumentList.Add($"ytsearch{SearchCandidateCount}:{terms} official video");

        var (_, stdout, stderr) = await RunAsync(psi, ct);
        var candidates = ParseFlatSearch(stdout);
        if (candidates.Count == 0)
            logger.LogInformation("Music video search returned no candidates: {Error}", LogSanitizer.ForLog(Truncate(stderr)));
        return candidates;
    }

    private ProcessStartInfo NewYtDlp(string? cookiesPath, bool includeThrottle)
    {
        var opts = options.Value;
        var psi = new ProcessStartInfo(opts.YtDlpPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-progress");
        if (includeThrottle && opts.DownloadSleepSeconds > 0)
        {
            psi.ArgumentList.Add("--sleep-interval");
            psi.ArgumentList.Add(opts.DownloadSleepSeconds.ToString());
            if (opts.DownloadMaxSleepSeconds > opts.DownloadSleepSeconds)
            {
                psi.ArgumentList.Add("--max-sleep-interval");
                psi.ArgumentList.Add(opts.DownloadMaxSleepSeconds.ToString());
            }
        }
        if (!string.IsNullOrWhiteSpace(opts.FfmpegPath))
        {
            psi.ArgumentList.Add("--ffmpeg-location");
            psi.ArgumentList.Add(opts.FfmpegPath);
        }
        if (cookiesPath is not null)
        {
            psi.ArgumentList.Add("--cookies");
            psi.ArgumentList.Add(cookiesPath);
        }
        foreach (var extra in YtDlpDownloadProvider.SplitArgs(opts.YtDlpExtraArgs))
            psi.ArgumentList.Add(extra);
        return psi;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        ProcessStartInfo psi, CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi };
        process.Start();
        // Read both streams concurrently to avoid buffer-full deadlock.
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(ct));
        return (process.ExitCode, outputTask.Result, errorTask.Result.Trim());
    }

    /// <summary>Canonicalizes a pinned id/URL to a watch URL; null when unusable.</summary>
    internal static string? CanonicalizePin(string? videoIdOrUrl)
    {
        if (string.IsNullOrWhiteSpace(videoIdOrUrl))
            return null;
        var raw = videoIdOrUrl.Trim();
        if (ImportUrlParser.TryParse(raw, out var kind, out var id) && kind == ImportUrlKind.YouTube)
            return ImportUrlParser.YouTubeWatchUrl(id);
        // Not a URL — treat as a bare video id.
        if (!raw.Contains('/') && !raw.Contains(' '))
            return ImportUrlParser.YouTubeWatchUrl(raw);
        return null;
    }

    /// <summary>
    /// Search terms for the music-video lookup, robust to a not-yet-enriched song whose Title is
    /// still the raw YouTube upload title (e.g. "Lyrical Lemonade, Artist - Song [Official Audio]"):
    /// noise segments are stripped, and a leading "credit - " prefix that repeats the artist is
    /// dropped so the query doesn't carry the uploader/audio-upload phrasing into the search.
    /// </summary>
    internal static string BuildSearchTerms(string artist, string title)
    {
        var t = StripNoiseSegments(title);
        var dashIdx = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx > 0 && !string.IsNullOrWhiteSpace(artist)
            && t[..dashIdx].Contains(artist, StringComparison.OrdinalIgnoreCase))
        {
            t = t[(dashIdx + 3)..];
        }
        t = t.Trim();
        if (t.Length == 0) t = title.Trim();
        return string.IsNullOrWhiteSpace(artist) ? t : $"{artist} {t}".Trim();
    }

    /// <summary>Removes bracketed upload-noise segments — "[Official Audio]", "(Lyric Video)", "(4K)" — while keeping real subtitle brackets like "(Lunchbreak Freestyle)".</summary>
    internal static string StripNoiseSegments(string title) =>
        System.Text.RegularExpressions.Regex
            .Replace(
                title,
                @"[\[\(][^\]\)]*(official|audio|video|lyric|visuali[sz]|4k|\bhd\b|explicit|clean)[^\]\)]*[\]\)]",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Trim();

    internal static string BuildFormat(int maxHeight) =>
        $"bestvideo[height<=?{maxHeight}][ext=mp4]+bestaudio[ext=m4a]/best[height<=?{maxHeight}][ext=mp4]/best";

    /// <summary>
    /// Heuristic for uploads that are audio with a static cover image rather than a music video:
    /// "Official Audio" / lyric videos / visualizers, and YouTube's auto-generated "&lt;artist&gt; -
    /// Topic" art tracks.
    /// </summary>
    internal static bool LooksLikeAudioOnlyUpload(string title, string? channel)
    {
        if (channel is not null && channel.TrimEnd().EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase))
            return true;
        var t = title.ToLowerInvariant();
        return t.Contains("official audio")
            || t.Contains("(audio)") || t.Contains("[audio]")
            || t.Contains("lyric video") || t.Contains("lyrics video")
            || t.Contains("(lyrics)") || t.Contains("[lyrics]")
            || t.Contains("visualizer") || t.Contains("visualiser")
            || t.Contains("art track");
    }

    /// <summary>
    /// Distinctive tokens of the song's title (noise/credit-prefix stripped, artist + generic words
    /// removed) used to check that a search candidate is the SAME SONG — an artist's unrelated
    /// "(Official Music Video)" must never outrank the right track.
    /// </summary>
    internal static IReadOnlyCollection<string> TitleTokens(string artist, string title)
    {
        var t = StripNoiseSegments(title);
        var dashIdx = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx > 0 && !string.IsNullOrWhiteSpace(artist)
            && t[..dashIdx].Contains(artist, StringComparison.OrdinalIgnoreCase))
        {
            t = t[(dashIdx + 3)..];
        }

        var artistTokens = Tokenize(artist);
        return Tokenize(t)
            .Where(token => !artistTokens.Contains(token) && !GenericTokens.Contains(token))
            .ToHashSet();
    }

    private static readonly HashSet<string> GenericTokens =
        ["official", "video", "audio", "music", "feat", "featuring", "prod", "the", "and", "with"];

    private static HashSet<string> Tokenize(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]{3,}")
            .Select(m => m.Value)
            .ToHashSet();

    /// <summary>
    /// Scores a search candidate as "how likely is this the actual music video for THIS song".
    /// Song-title token overlap dominates (zero overlap ≈ a different song — heavy penalty); real
    /// videos score up; audio-only markers score hard down; duration is sanity-checked against the
    /// song when known (videos legitimately run longer — intros/outros — so the upper bound is
    /// looser). Candidates below zero are rejected outright by <see cref="PickBestCandidate"/>.
    /// </summary>
    internal static int ScoreCandidate(
        SearchCandidate candidate, int? songDurationMs, IReadOnlyCollection<string> titleTokens)
    {
        var score = 0;
        var t = candidate.Title.ToLowerInvariant();

        if (titleTokens.Count > 0)
        {
            var matched = titleTokens.Count(token => t.Contains(token));
            if (matched == 0) score -= 80; // wrong song
            else score += 30 * matched / titleTokens.Count;
        }

        if (t.Contains("official music video") || t.Contains("official video")) score += 30;
        else if (t.Contains("music video") || t.Contains("(video)") || t.Contains("[video]")) score += 20;

        if (LooksLikeAudioOnlyUpload(candidate.Title, candidate.Channel)) score -= 50;
        if (t.Contains("sped up") || t.Contains("slowed") || t.Contains("reverb")
            || t.Contains("8d audio") || t.Contains("karaoke")
            || t.Contains("instrumental") || t.Contains("reaction")) score -= 25;
        // Whole-word-ish checks: a bare Contains would penalize "Alive", "Discover", "Recovery"…
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"(^|[\s\(\[])(live|cover)([\s\)\]]|$)")) score -= 25;

        if (songDurationMs is > 0 && candidate.DurationSeconds is > 0)
        {
            var songSec = songDurationMs.Value / 1000.0;
            var videoSec = candidate.DurationSeconds.Value;
            // Within [-15s, +90s] of the song ≈ the same recording plus an intro/outro.
            if (videoSec >= songSec - 15 && videoSec <= songSec + 90) score += 15;
            else if (videoSec < songSec * 0.5 || videoSec > songSec * 2.5) score -= 25;
        }

        return score;
    }

    internal static SearchCandidate? PickBestCandidate(
        List<SearchCandidate> candidates, int? songDurationMs, IReadOnlyCollection<string> titleTokens) =>
        candidates
            .Select((c, i) => (Candidate: c, Score: ScoreCandidate(c, songDurationMs, titleTokens), Index: i))
            .Where(x => x.Score >= 0) // negative = wrong song / audio-only / degraded — not worth 100MB
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index) // ties → YouTube's own relevance order
            .Select(x => x.Candidate)
            .FirstOrDefault();

    /// <summary>Parses `--flat-playlist -J` output into candidates. Tolerates missing fields.</summary>
    internal static List<SearchCandidate> ParseFlatSearch(string json)
    {
        var result = new List<SearchCandidate>();
        if (string.IsNullOrWhiteSpace(json))
            return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var id = entry.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;
                var title = entry.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "";
                var channel = entry.TryGetProperty("channel", out var chEl) && chEl.ValueKind == JsonValueKind.String
                    ? chEl.GetString() ?? ""
                    : entry.TryGetProperty("uploader", out var upEl) && upEl.ValueKind == JsonValueKind.String
                        ? upEl.GetString() ?? ""
                        : "";
                double? duration = entry.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number
                    ? durEl.GetDouble()
                    : null;
                result.Add(new SearchCandidate(id!, title, channel, duration));
            }
        }
        catch (JsonException)
        {
            // Unparseable search output → no candidates; the caller reports Missing.
        }
        return result;
    }

    /// <summary>Parses the two `--print` lines: video id, then duration in seconds (may be "NA" or fractional).</summary>
    internal static (string? VideoId, int? DurationSeconds) ParsePrinted(string stdout)
    {
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var videoId = lines.Length > 0 && lines[0].Length > 0 ? lines[0] : null;
        int? duration = null;
        if (lines.Length > 1 && double.TryParse(lines[1], System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0)
            duration = (int)Math.Round(d);
        return (videoId, duration);
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];
}
