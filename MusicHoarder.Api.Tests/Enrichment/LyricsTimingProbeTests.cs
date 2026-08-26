using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

/// <summary>
/// The paid half of lyrics timing validation: one short transcribed window, and what it lets us conclude.
/// The transcriber is faked, so these tests are really about the reasoning — does a correct LRC survive,
/// does a uniformly shifted one get measured rather than condemned, and does a genuinely mistimed one get
/// refused rather than "repaired" into something equally wrong.
/// </summary>
public class LyricsTimingProbeTests : IDisposable
{
    private readonly DirectoryInfo _tmpDir = Directory.CreateTempSubdirectory("mh-probe-tests");

    public void Dispose()
    {
        try { _tmpDir.Delete(recursive: true); }
        catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private const double TrackSeconds = 220;
    private const double LrcGap = 5;
    private const double LrcFirstLineAt = 10;

    /// <summary>
    /// Forty five-word lines whose every word is unique. Distinct words on purpose: a real lyric repeats
    /// itself constantly and that ambiguity is a separate problem (the reason the probe confirms the LRC's
    /// own hypothesis before it goes looking), so these tests hold it fixed and exercise the timing logic.
    /// </summary>
    private static readonly string[] LineTexts = Enumerable.Range(0, 40)
        .Select(i => string.Join(' ', Enumerable.Range(0, 5).Select(w => $"w{i}x{w}")))
        .ToArray();

    private static string BuildLrc(double firstAt = LrcFirstLineAt, double gap = LrcGap)
        => string.Join('\n', LineTexts.Select((t, i) => Tag(firstAt + (i * gap)) + t));

    private static string Tag(double seconds)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"[{(int)(seconds / 60):00}:{seconds % 60:00.00}]");

    /// <summary>The words a perfect transcript of the window would contain, on the audio's true clock.</summary>
    private static List<TimedWord> WordsForWindow(double windowStart, double windowLength, double trueFirstAt, double gap)
    {
        var words = new List<TimedWord>();
        for (var i = 0; i < LineTexts.Length; i++)
        {
            var lineStart = trueFirstAt + (i * gap);
            var tokens = LineTexts[i].Split(' ');
            for (var t = 0; t < tokens.Length; t++)
            {
                var at = lineStart + (t * 0.4);
                if (at >= windowStart && at < windowStart + windowLength)
                    words.Add(new TimedWord(tokens[t], at, at + 0.4));
            }
        }
        return words;
    }

    private sealed class FakeTranscriber(Func<double, double, List<TimedWord>> words) : ILyricsTranscriptionService
    {
        public bool IsConfigured => true;

        public Task<TranscriptionResult> TranscribeAsync(SongMetadata song, string audioFilePath, CancellationToken ct = default)
            => throw new NotSupportedException("The probe never asks for a whole-song transcription.");

        public Task<IReadOnlyList<TimedWord>> TranscribeClipAsync(
            string audioFilePath, double startSeconds, double lengthSeconds, string? promptText = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TimedWord>>(words(startSeconds, lengthSeconds));
    }

    private static LyricsTimingOptions Options() => new();

    private LyricsTimingProbe NewProbe(Func<double, double, List<TimedWord>> words, LyricsTimingOptions? opts = null)
    {
        var options = new StaticOptionsMonitor<LyricsTimingOptions>(opts ?? Options());
        return new LyricsTimingProbe(
            new FakeTranscriber(words),
            new LyricsProbeBudget(options, NullLogger<LyricsProbeBudget>.Instance),
            options,
            NullLogger<LyricsTimingProbe>.Instance);
    }

    private SongMetadata NewSong(string lrc, int durationSeconds = (int)TrackSeconds)
    {
        var path = Path.Combine(_tmpDir.FullName, $"{Guid.NewGuid():N}.mp3");
        File.WriteAllBytes(path, [0x00]);
        return new SongMetadata
        {
            SourcePath = path,
            FileName = Path.GetFileName(path),
            Extension = ".mp3",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = "Test Artist",
            Title = "Test Song",
            DurationSeconds = durationSeconds,
            SyncedLyrics = lrc,
        };
    }

    [Fact]
    public async Task an_lrc_that_matches_the_audio_is_confirmed()
    {
        var song = NewSong(BuildLrc());
        var probe = NewProbe((start, len) => WordsForWindow(start, len, LrcFirstLineAt, LrcGap));

        var result = await probe.ProbeAsync(song, song.SourcePath!);

        Assert.NotNull(result);
        Assert.Equal(LyricsSyncStatus.Ok, result!.Status);
        Assert.True(Math.Abs(result.OffsetSeconds) <= 1.5);
    }

    [Fact]
    public async Task a_uniformly_late_lrc_is_measured_and_marked_repairable()
    {
        // The LRC says the first line lands at 0:10; the audio actually sings it at 0:25.
        var song = NewSong(BuildLrc());
        var probe = NewProbe((start, len) => WordsForWindow(start, len, trueFirstAt: 25, gap: LrcGap));

        var result = await probe.ProbeAsync(song, song.SourcePath!);

        Assert.NotNull(result);
        Assert.Equal(LyricsSyncStatus.Corrected, result!.Status);
        Assert.Equal(15, result.OffsetSeconds, precision: 0);
    }

    [Fact]
    public async Task the_measured_offset_repairs_the_lrc_without_touching_the_words()
    {
        var song = NewSong(BuildLrc());
        var probe = NewProbe((start, len) => WordsForWindow(start, len, trueFirstAt: 25, gap: LrcGap));

        var result = await probe.ProbeAsync(song, song.SourcePath!);
        LyricsTimingCheckService.ApplyProbeResult(song, result!, Options());

        Assert.Equal(LyricsSyncStatus.Corrected, song.LyricsSyncStatus);
        var repaired = LyricsTimingValidator.ParseLrc(song.SyncedLyrics);
        Assert.Equal(25, repaired[0].Start, precision: 0);
        // Same lines, same order, same text — only the clock moved.
        Assert.Equal(LineTexts, repaired.Select(l => l.Text));
        // And the song now reports itself as AI-enhanced rather than human-timed.
        Assert.Equal(LyricsProvenance.AiEnhanced, song.LyricsProvenance);
    }

    [Fact]
    public async Task an_lrc_that_runs_at_a_different_pace_is_refused_rather_than_shifted()
    {
        // A slower recording: its lines fall 9s apart where the LRC claims 5s, so the error grows line by
        // line inside the window and no single shift fixes it. Repairing this by shifting would trade one
        // wrong timing for another, so the probe must refuse rather than "fix" it.
        //
        // Note the scale that is needed: over a 30-second window a SMALL pace difference is arithmetically
        // indistinguishable from a constant offset, and the probe honestly reports it as one. That case is
        // caught earlier and for free — two recordings at different tempos have different lengths, which is
        // the LRCLIB duration check in LyricsTimingValidator.
        var song = NewSong(BuildLrc());
        var probe = NewProbe((start, len) => WordsForWindow(start, len, trueFirstAt: LrcFirstLineAt, gap: 9));

        var result = await probe.ProbeAsync(song, song.SourcePath!);

        Assert.NotNull(result);
        Assert.Equal(LyricsSyncStatus.Suspect, result!.Status);
        Assert.Contains("different edit", result.Issue);
    }

    [Fact]
    public async Task an_instrumental_window_says_nothing_either_way()
    {
        var song = NewSong(BuildLrc());
        var probe = NewProbe((_, _) => []);

        var result = await probe.ProbeAsync(song, song.SourcePath!);

        Assert.NotNull(result);
        Assert.Equal(LyricsSyncStatus.Unverifiable, result!.Status);
        // It still cost a window, so the caller must count the attempt.
        Assert.True(result.SpentBudget);
    }

    [Fact]
    public async Task the_probe_stops_once_the_audio_budget_is_spent()
    {
        var opts = Options();
        opts.AudioSecondsPerHour = 30;      // exactly one window
        opts.ProbeWindowSeconds = 30;
        var probe = NewProbe((start, len) => WordsForWindow(start, len, LrcFirstLineAt, LrcGap), opts);

        var first = await probe.ProbeAsync(NewSong(BuildLrc()), NewSong(BuildLrc()).SourcePath!);
        var second = await probe.ProbeAsync(NewSong(BuildLrc()), NewSong(BuildLrc()).SourcePath!);

        Assert.NotNull(first);
        // Null, not a verdict: out of budget is "we did not look", never "the lyrics are fine".
        Assert.Null(second);
    }

    [Fact]
    public async Task a_song_with_no_synced_lyrics_is_never_probed()
    {
        var probed = false;
        var song = NewSong(lrc: string.Empty);
        var probe = NewProbe((start, len) => { probed = true; return WordsForWindow(start, len, LrcFirstLineAt, LrcGap); });

        var result = await probe.ProbeAsync(song, song.SourcePath!);

        Assert.Equal(LyricsSyncStatus.Unverifiable, result!.Status);
        Assert.False(probed);
    }
}

/// <summary>Minimal <see cref="IOptionsMonitor{T}"/> over a fixed value, for tests that do not reconfigure.</summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
