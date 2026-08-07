using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

/// <summary>Result of transcribing a song's audio: a synced LRC + a plain transcript + the model used.</summary>
/// <param name="AlignedToReference">
/// True when the LRC carries the song's <b>official</b> lyric text re-timed against the audio (the
/// reference lyrics were resolved AND an aligner placed them successfully) — i.e. the same words as
/// LRCLIB with better timestamps. False when the lines are the transcript's own guess at the words,
/// which must never silently replace curated lyrics. Callers use this to decide whether promoting
/// the transcription to the display/file default is safe.
/// </param>
public record TranscriptionResult(string? SyncedLyrics, string? PlainLyrics, string Model, bool AlignedToReference = false);

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

            var refLines = LrcBuilder.SplitReferenceLines(referenceText);
            List<(double Start, string Text)>? lines = null;
            var alignedToReference = false;

            if (refLines is { Count: > 0 } && words.Count > 0)
            {
                // Tier B — we have the official lyrics. Deterministic forced alignment is robust to
                // repeated lines (an LLM can't tell which repetition is which and collapses the timing);
                // the LLM is only a fallback if the reference text doesn't match the audio well.
                lines = ForcedLyricsAligner.Align(refLines, words);
                if (LrcBuilder.IsDegenerate(lines))
                    lines = null;

                if (lines is null && opts.UseLlmAlignment && aligner.IsAvailable)
                {
                    lines = await aligner.AlignReferenceLinesAsync(refLines, words, ct);
                    if (LrcBuilder.IsDegenerate(lines))
                        lines = null;
                }

                // Only a *successful* alignment carries the official words — the fallbacks below emit
                // the transcript's own wording, which is not a re-sync of the curated lyrics.
                alignedToReference = lines is { Count: > 0 };
            }
            else if (opts.UseLlmAlignment && aligner.IsAvailable && words.Count > 0)
            {
                // Tier C — no official lyrics: let the LLM re-segment the raw transcript into clean lines.
                lines = await aligner.ResegmentAsync(words, ct);
                if (LrcBuilder.IsDegenerate(lines))
                    lines = null;
            }

            // Fallback: deterministic pause/word-cap split, then coarse Whisper segments.
            lines ??= LrcBuilder.BuildLinesFromWords(ToLineBuildWords(response.Words), opts.LineSplitPauseSeconds, opts.LineSplitMaxWords)
                      ?? LrcBuilder.BuildLinesFromSegments(ToTranscriptSegments(response.Segments));

            // Identical repeated lines (e.g. a "baby baby baby" hook) sometimes collapse onto one
            // timestamp during alignment — spread such runs evenly across their gap.
            if (lines is { Count: > 0 })
                LrcBuilder.SpreadRepeatedConsecutiveLines(lines);

            var synced = lines is { Count: > 0 } ? LrcBuilder.Format(lines) : null;
            var plain = NullIfBlank(referenceText) ?? transcriptText;
            return new TranscriptionResult(synced, plain, opts.Model, alignedToReference);
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

    /// <summary>
    /// Every transcript word — <b>including blanks</b> — as <see cref="TimedWord"/>s for the deterministic
    /// line splitter. Unlike <see cref="ToTimedWords"/> (which drops blanks for alignment), blanks are kept
    /// so their end time still advances the silence gap that drives line breaks.
    /// </summary>
    private static List<TimedWord> ToLineBuildWords(List<WhisperWord>? words)
        => words is null
            ? new List<TimedWord>()
            : words.Select(w => new TimedWord(w.Word ?? string.Empty, w.Start, w.End)).ToList();

    private static List<TranscriptSegment> ToTranscriptSegments(List<WhisperSegment>? segments)
        => segments is null
            ? new List<TranscriptSegment>()
            : segments.Select(s => new TranscriptSegment(s.Start, s.Text)).ToList();

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
