using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Options;

/// <summary>
/// Configuration for lyrics timing validation — deciding whether a stored LRC's timestamps actually belong
/// to the audio we hold, and correcting them when they are merely shifted.
///
/// The free arithmetic checks (<c>LyricsTimingValidator</c>) always run and are not configurable. What this
/// section governs is the paid half: a short Whisper window per suspect song, and the budget that keeps it
/// inside a free-tier plan. The defaults are sized for Groq's free tier, whose transcription quota is
/// measured in <b>audio-seconds</b> (7,200/hour and 28,800/day for whisper-large-v3-turbo) rather than
/// requests — which is exactly why the probe listens to 30 seconds of a song instead of all of it.
/// </summary>
public class LyricsTimingOptions
{
    public const string SectionName = "LyricsTiming";

    /// <summary>Master switch for the paid probe. The free checks run regardless.</summary>
    public bool EnableAiProbe { get; set; } = true;

    /// <summary>
    /// Whether the background sweep may spend probe budget on its own. Off means only the per-song endpoint
    /// (the "verify timing" button) can spend it.
    /// </summary>
    public bool EnableProbeSweep { get; set; } = true;

    /// <summary>How much audio each probe listens to. Longer is more reliable and costs proportionally more.</summary>
    [Range(10, 120)]
    public double ProbeWindowSeconds { get; set; } = 30;

    /// <summary>
    /// Where in the track the window opens, as a fraction of its length. Mid-song is the safest bet for
    /// finding sung words: intros and outros are where the instrumental stretches live.
    /// </summary>
    [Range(0.05, 0.9)]
    public double ProbeWindowPosition { get; set; } = 0.45;

    /// <summary>Audio-seconds the probe may spend per rolling hour. Groq's free whisper tier allows 7,200.</summary>
    [Range(0, 1_000_000)]
    public int AudioSecondsPerHour { get; set; } = 6000;

    /// <summary>Audio-seconds per rolling day. Groq's free whisper tier allows 28,800; leave headroom for
    /// full transcriptions the user asks for by hand.</summary>
    [Range(0, 10_000_000)]
    public int AudioSecondsPerDay { get; set; } = 20000;

    /// <summary>Songs the sweep probes per pass, so one pass can never drain the whole hour's budget at once.</summary>
    [Range(1, 500)]
    public int SweepBatchSize { get; set; } = 20;

    /// <summary>Pause between sweep passes when there was nothing to do.</summary>
    [Range(10, 86400)]
    public int SweepIdleSeconds { get; set; } = 300;

    /// <summary>
    /// How far a line may sit from where the audio says it is and still count as correct. Karaoke tolerance:
    /// a listener notices about a second, and Whisper's own word boundaries are not sharper than that.
    /// </summary>
    [Range(0.2, 10.0)]
    public double OkToleranceSeconds { get; set; } = 1.5;

    /// <summary>
    /// How tightly the per-line errors must agree before we call the drift a single constant offset and
    /// simply shift the LRC. Errors that scatter more widely than this mean the LRC does not merely start
    /// late, it runs at a different pace (a sped-up or extended edit), and shifting it would not fix it.
    /// </summary>
    [Range(0.2, 10.0)]
    public double ConstantOffsetSpreadSeconds { get; set; } = 2.0;

    /// <summary>Below this many matched words the window said nothing useful and no verdict is recorded.</summary>
    [Range(3, 100)]
    public int MinMatchedWords { get; set; } = 8;

    /// <summary>Give up probing a song after this many attempts, so an unfixable row stops costing budget.</summary>
    [Range(1, 10)]
    public int MaxProbeAttempts { get; set; } = 2;
}
