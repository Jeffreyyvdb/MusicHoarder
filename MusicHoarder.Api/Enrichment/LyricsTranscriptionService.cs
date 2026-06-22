using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

/// <summary>Result of transcribing a song's audio: a synced LRC + a plain transcript + the model used.</summary>
public record TranscriptionResult(string? SyncedLyrics, string? PlainLyrics, string Model);

public interface ILyricsTranscriptionService
{
    /// <summary>True when a key + base URL are configured (the transcribe endpoint 503s otherwise).</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Transcodes the file to a compact mono mp3 with ffmpeg, POSTs it to the configured
    /// OpenAI-compatible <c>/audio/transcriptions</c> endpoint for word-level timing, then builds the
    /// synced LRC — preferring LLM alignment of the song's official lyrics (LRCLIB plain) to Whisper's
    /// word clock, falling back to a deterministic split. Throws on a transcode or API failure.
    /// </summary>
    Task<TranscriptionResult> TranscribeAsync(SongMetadata song, string audioFilePath, CancellationToken ct = default);
}

/// <summary>
/// Talks to an OpenAI-compatible audio-transcriptions endpoint (OpenAI Whisper, Groq, a self-hosted
/// whisper) and turns the verbose_json segments into an LRC. Options are read per-call via
/// <see cref="IOptionsMonitor{T}"/> so the model/key can change at runtime without a restart.
/// </summary>
public sealed class LyricsTranscriptionService(
    HttpClient httpClient,
    ILrcLibService lrcLib,
    LlmLyricsAligner aligner,
    IOptionsMonitor<LyricsTranscriptionOptions> options,
    IOptions<MusicEnricherOptions> enricherOptions,
    ILogger<LyricsTranscriptionService> logger) : ILyricsTranscriptionService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsConfigured => options.CurrentValue.IsConfigured;

    public async Task<TranscriptionResult> TranscribeAsync(SongMetadata song, string audioFilePath, CancellationToken ct = default)
    {
        var opts = options.CurrentValue;
        if (!opts.IsConfigured)
            throw new InvalidOperationException("Lyrics transcription is not configured (missing BaseUrl/ApiKey).");

        if (!File.Exists(audioFilePath))
            throw new FileNotFoundException("Audio file not found on disk.", audioFilePath);

        var tempMp3 = Path.Combine(Path.GetTempPath(), $"mh-stt-{Guid.NewGuid():N}.mp3");
        try
        {
            await TranscodeToMp3Async(audioFilePath, tempMp3, ct);

            var response = await UploadWithRetryAsync(tempMp3, opts, ct);
            var words = ToTimedWords(response.Words);
            var transcriptText = NullIfBlank(response.Text);

            // Official lyric text (correct line breaks + punctuation) yields the best lines; prefer LRCLIB
            // plain. Whisper supplies the timing; the LLM only chooses where lines start.
            var referenceText = await ResolveReferenceLyricsAsync(song, ct);

            var refLines = SplitLyricLines(referenceText);
            List<(double Start, string Text)>? lines = null;

            if (refLines is { Count: > 0 } && words.Count > 0)
            {
                // Tier B — we have the official lyrics. Deterministic forced alignment is robust to
                // repeated lines (an LLM can't tell which repetition is which and collapses the timing);
                // the LLM is only a fallback if the reference text doesn't match the audio well.
                lines = ForcedLyricsAligner.Align(refLines, words);
                if (IsDegenerate(lines))
                    lines = null;

                if (lines is null && opts.UseLlmAlignment && aligner.IsAvailable)
                {
                    lines = await aligner.AlignReferenceLinesAsync(refLines, words, ct);
                    if (IsDegenerate(lines))
                        lines = null;
                }
            }
            else if (opts.UseLlmAlignment && aligner.IsAvailable && words.Count > 0)
            {
                // Tier C — no official lyrics: let the LLM re-segment the raw transcript into clean lines.
                lines = await aligner.ResegmentAsync(words, ct);
                if (IsDegenerate(lines))
                    lines = null;
            }

            // Fallback: deterministic pause/word-cap split, then coarse Whisper segments.
            lines ??= BuildLinesFromWords(response.Words, opts.LineSplitPauseSeconds, opts.LineSplitMaxWords)
                      ?? BuildLinesFromSegments(response.Segments);

            // Identical repeated lines (e.g. a "baby baby baby" hook) sometimes collapse onto one
            // timestamp during alignment — spread such runs evenly across their gap.
            if (lines is { Count: > 0 })
                SpreadRepeatedConsecutiveLines(lines);

            var synced = lines is { Count: > 0 } ? FormatLrc(lines) : null;
            var plain = NullIfBlank(referenceText) ?? transcriptText;
            return new TranscriptionResult(synced, plain, opts.Model);
        }
        finally
        {
            TryDelete(tempMp3);
        }
    }

    private static List<TimedWord> ToTimedWords(List<WhisperWord>? words)
        => words is null
            ? new List<TimedWord>()
            : words.Where(w => !string.IsNullOrWhiteSpace(w.Word))
                   .Select(w => new TimedWord(w.Word!.Trim(), w.Start, w.End))
                   .ToList();

    /// <summary>Official lyric text for the song: the stored LRCLIB plain lyrics, else a fresh LRCLIB fetch.</summary>
    private async Task<string?> ResolveReferenceLyricsAsync(SongMetadata song, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(song.PlainLyrics))
            return song.PlainLyrics;
        try
        {
            var result = await lrcLib.FetchLyricsAsync(song, ct);
            return NullIfBlank(result?.PlainLyrics);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "LRCLIB reference-lyrics fetch failed for SongId={SongId}; transcribing without it.", song.Id);
            return null;
        }
    }

    private static List<string>? SplitLyricLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var lines = text.Replace("\r\n", "\n").Split('\n')
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l.Trim(), @"^\[\d{1,2}:\d{2}(?:[.:]\d{1,3})?\]\s*", ""))
            .Where(l => l.Length > 0)
            .ToList();
        return lines.Count > 0 ? lines : null;
    }

    private static string FormatLrc(List<(double Start, string Text)> lines)
    {
        var sb = new StringBuilder();
        foreach (var (start, text) in lines)
            sb.Append(FormatLrcTimestamp(start)).Append(text).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Spreads runs of identical consecutive lines that collapsed onto the same start time (an alignment
    /// failure mode for repeated hooks) evenly across the gap up to the next distinct line. Only touches
    /// runs that actually share a timestamp, so genuinely distinct timings are left untouched.
    /// </summary>
    private static void SpreadRepeatedConsecutiveLines(List<(double Start, string Text)> lines)
    {
        var i = 0;
        while (i < lines.Count)
        {
            var j = i;
            while (j + 1 < lines.Count
                   && string.Equals(NormalizeLine(lines[j + 1].Text), NormalizeLine(lines[i].Text), StringComparison.OrdinalIgnoreCase)
                   && Math.Abs(lines[j + 1].Start - lines[i].Start) < 0.05)
            {
                j++;
            }

            var runLength = j - i + 1;
            if (runLength > 1)
            {
                var startT = lines[i].Start;
                // Spread up to the next distinct line; at song end, fall back to ~1s per line.
                var endT = j + 1 < lines.Count ? lines[j + 1].Start : startT + runLength;
                if (endT > startT)
                {
                    var step = (endT - startT) / runLength;
                    for (var k = 0; k < runLength; k++)
                        lines[i + k] = (startT + (k * step), lines[i + k].Text);
                }
            }

            i = j + 1;
        }
    }

    private static string NormalizeLine(string text)
        => System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");

    /// <summary>
    /// True when an alignment is unusable — null/empty, or so collapsed that &gt;40% of lines share a
    /// single timestamp (the LLM-on-repetitive-lyrics failure mode). The caller then falls back.
    /// </summary>
    private static bool IsDegenerate(List<(double Start, string Text)>? lines)
    {
        if (lines is null || lines.Count == 0)
            return true;
        if (lines.Count < 4)
            return false;
        var mostOnOneStamp = lines.GroupBy(l => Math.Round(l.Start, 1)).Max(g => g.Count());
        return mostOnOneStamp > lines.Count * 0.4;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// Down-mixes to mono 16 kHz mp3 via ffmpeg — Whisper resamples to 16 kHz mono internally anyway,
    /// so this is lossless to accuracy while keeping a full song well under OpenAI's 25 MB upload cap.
    /// Mirrors the concurrent-stream-read pattern used by <c>FpcalcService</c> to avoid pipe deadlock.
    /// </summary>
    private async Task TranscodeToMp3Async(string inputPath, string outputPath, CancellationToken ct)
    {
        var ffmpeg = string.IsNullOrWhiteSpace(enricherOptions.Value.FfmpegPath)
            ? "ffmpeg"
            : enricherOptions.Value.FfmpegPath;

        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputPath);
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("16000");
        psi.ArgumentList.Add("-b:a");
        psi.ArgumentList.Add("96k");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("mp3");
        psi.ArgumentList.Add(outputPath);

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(ct));

            if (process.ExitCode != 0)
            {
                var stderr = errorTask.Result.Trim();
                throw new InvalidOperationException(
                    $"ffmpeg transcode failed (exit {process.ExitCode}): {Truncate(stderr, 400)}");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                $"ffmpeg not found ('{ffmpeg}'). Put it on PATH or set MusicEnricher:FfmpegPath.", ex);
        }
    }

    /// <summary>Posts the mp3 to <c>/audio/transcriptions</c>, retrying transient failures with backoff.</summary>
    private async Task<WhisperVerboseResponse> UploadWithRetryAsync(string mp3Path, LyricsTranscriptionOptions opts, CancellationToken ct)
    {
        var url = $"{opts.BaseUrl.TrimEnd('/')}/audio/transcriptions";
        var maxAttempts = Math.Max(0, opts.MaxRetries) + 1;

        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // A fresh stream + form per attempt — request content can't be replayed once consumed.
            using var form = new MultipartFormDataContent();
            await using var fileStream = File.OpenRead(mp3Path);
            var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            form.Add(fileContent, "file", Path.GetFileName(mp3Path));
            form.Add(new StringContent(opts.Model), "model");
            form.Add(new StringContent("verbose_json"), "response_format");
            form.Add(new StringContent("segment"), "timestamp_granularities[]");
            form.Add(new StringContent("word"), "timestamp_granularities[]");

            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiKey);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(opts.TimeoutSeconds));

            try
            {
                using var resp = await httpClient.SendAsync(req, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    var parsed = await resp.Content.ReadFromJsonAsync<WhisperVerboseResponse>(Json, cts.Token);
                    return parsed ?? throw new InvalidOperationException("Transcription API returned an empty body.");
                }

                var body = await resp.Content.ReadAsStringAsync(cts.Token);
                // Never log the Authorization header; the URL/body here carry no secret.
                logger.LogWarning("Transcription failed: {Status} {Body}", (int)resp.StatusCode, Truncate(body, 512));

                if (attempt < maxAttempts - 1 && IsRetryableStatus(resp.StatusCode))
                {
                    await Task.Delay(ComputeBackoff(attempt), ct);
                    continue;
                }

                throw new HttpRequestException(
                    $"Transcription API returned {(int)resp.StatusCode}: {Truncate(body, 300)}", null, resp.StatusCode);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // caller cancellation — never retry
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
            {
                if (attempt >= maxAttempts - 1)
                {
                    if (ex is OperationCanceledException)
                        throw new TimeoutException(
                            $"Transcription timed out after {opts.TimeoutSeconds}s ({maxAttempts} attempt(s)).", ex);
                    throw;
                }

                logger.LogWarning(ex,
                    "Transient error calling transcription API (attempt {Attempt}/{Max}); retrying.",
                    attempt + 1, maxAttempts);
                await Task.Delay(ComputeBackoff(attempt), ct);
            }
        }
    }

    /// <summary>
    /// Re-chunks the flat word list into LRC lines: starts a new line whenever the silent gap before a
    /// word reaches <paramref name="pauseThresholdSeconds"/> or the current line hits
    /// <paramref name="maxWordsPerLine"/>. Each line is stamped with its first word's start time.
    /// </summary>
    private static List<(double Start, string Text)>? BuildLinesFromWords(
        List<WhisperWord>? words, double pauseThresholdSeconds, int maxWordsPerLine)
    {
        if (words is not { Count: > 0 })
            return null;

        var lines = new List<(double, string)>();
        var current = new List<string>();
        double lineStart = 0;
        double? prevEnd = null;

        void Flush()
        {
            if (current.Count == 0) return;
            var text = string.Join(' ', current).Trim();
            if (text.Length > 0) lines.Add((lineStart, text));
            current.Clear();
        }

        foreach (var w in words)
        {
            var token = w.Word?.Trim();
            if (string.IsNullOrEmpty(token))
            {
                prevEnd = w.End;
                continue;
            }

            var gap = prevEnd is { } pe ? w.Start - pe : 0;
            var shouldBreak = current.Count > 0
                && ((pauseThresholdSeconds > 0 && gap >= pauseThresholdSeconds) || current.Count >= maxWordsPerLine);
            if (shouldBreak)
                Flush();

            if (current.Count == 0)
                lineStart = w.Start;
            current.Add(token);
            prevEnd = w.End;
        }

        Flush();
        return lines.Count > 0 ? lines : null;
    }

    private static List<(double Start, string Text)>? BuildLinesFromSegments(List<WhisperSegment>? segments)
    {
        if (segments is not { Count: > 0 })
            return null;

        var lines = new List<(double, string)>();
        foreach (var segment in segments)
        {
            var text = segment.Text?.Trim();
            if (!string.IsNullOrEmpty(text))
                lines.Add((segment.Start, text));
        }

        return lines.Count > 0 ? lines : null;
    }

    /// <summary>Formats seconds as an LRC <c>[mm:ss.xx]</c> tag (centisecond precision).</summary>
    private static string FormatLrcTimestamp(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var minutes = (int)(seconds / 60);
        var secs = (int)(seconds % 60);
        var centis = (int)Math.Round((seconds - Math.Floor(seconds)) * 100);
        if (centis == 100) { centis = 0; secs++; }
        if (secs == 60) { secs = 0; minutes++; }
        return string.Format(CultureInfo.InvariantCulture, "[{0:00}:{1:00}.{2:00}]", minutes, secs, centis);
    }

    private static bool IsRetryableStatus(HttpStatusCode status) => (int)status switch
    {
        429 => true,
        >= 500 and <= 599 => true,
        _ => false,
    };

    private static TimeSpan ComputeBackoff(int attempt)
    {
        var baseMs = 750.0 * Math.Pow(2, attempt);
        var jittered = Random.Shared.NextDouble() * baseMs;
        var capped = Math.Min(jittered, 8000);
        return TimeSpan.FromMilliseconds(capped);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort temp cleanup */ }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    // --- verbose_json DTOs (OpenAI Whisper) ---

    public sealed class WhisperVerboseResponse
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("language")]
        public string? Language { get; set; }

        [JsonPropertyName("duration")]
        public double? Duration { get; set; }

        [JsonPropertyName("segments")]
        public List<WhisperSegment>? Segments { get; set; }

        [JsonPropertyName("words")]
        public List<WhisperWord>? Words { get; set; }
    }

    public sealed class WhisperSegment
    {
        [JsonPropertyName("start")]
        public double Start { get; set; }

        [JsonPropertyName("end")]
        public double End { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    public sealed class WhisperWord
    {
        [JsonPropertyName("word")]
        public string? Word { get; set; }

        [JsonPropertyName("start")]
        public double Start { get; set; }

        [JsonPropertyName("end")]
        public double End { get; set; }
    }
}
