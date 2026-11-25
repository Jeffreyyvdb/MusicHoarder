using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.BackgroundWork;

public readonly record struct LyricsFacts(
    LyricsStatus Status,
    LyricsOrigin Origin,
    bool? IsInstrumental,
    bool HasSynced,
    bool HasPlain,
    bool HasTranscribedSynced,
    bool HasTranscribedPlain,
    PreferredLyricsSource Preferred,
    TranscriptionStatus Transcription,
    bool AlignedToReference,
    bool HasTimingOffset,
    bool HasTitleAndArtist);

public readonly record struct LyricsOutcome(string Key, bool ShowsSyncedLyrics);

public sealed record LyricsOutcomeSegment(string Key, string Label, string Tone, string Note);

public static class LyricsOutcomes
{
    public const string LrclibSynced = "lrclib-synced";
    public const string GeniusSynced = "genius-synced";
    public const string AiTimedOfficial = "ai-timed-official";
    public const string AiWritten = "ai-written";
    public const string PlainOnly = "plain-only";
    public const string InstrumentalLrclib = "instrumental-lrclib";
    public const string NoSingingAi = "no-singing-ai";
    public const string AiFailed = "ai-failed";
    public const string NotFound = "not-found";
    public const string LookupFailed = "lookup-failed";
    public const string CantLookUp = "cant-look-up";
    public const string NotLookedUp = "not-looked-up";

    public static readonly IReadOnlyList<LyricsOutcomeSegment> Segments =
    [
        new(LrclibSynced, "Synced lyrics from LRCLIB", BackgroundTone.Good, "A community-made LRC, timing as published."),
        new(GeniusSynced, "Synced lyrics from Genius", BackgroundTone.Good, "Genius gave timed lines."),
        new(AiTimedOfficial, "Official words, timed by AI", BackgroundTone.Ai, "The published words, re-timed against the audio."),
        new(AiWritten, "Words written by AI", BackgroundTone.Ai, "Nobody published words, so the AI wrote down what it heard."),
        new(PlainOnly, "Plain text only", BackgroundTone.Neutral, "Words with no timing; the lyrics view cannot follow along."),
        new(InstrumentalLrclib, "Instrumental per LRCLIB", BackgroundTone.Muted, "LRCLIB marks it as having no words."),
        new(NoSingingAi, "No singing heard", BackgroundTone.Muted, "The AI listened and heard no words."),
        new(AiFailed, "AI sync failed", BackgroundTone.Warning, "The transcription errored; it waits for official words to arrive."),
        new(NotFound, "Not found", BackgroundTone.Neutral, "LRCLIB has nothing yet and is asked again on a back-off."),
        new(LookupFailed, "Lookup failed", BackgroundTone.Bad, "LRCLIB could not be reached for it; it is retried."),
        new(CantLookUp, "Cannot look up", BackgroundTone.Muted, "No title or artist to search with."),
        new(NotLookedUp, "Not looked up yet", BackgroundTone.Pending, "Waiting for the lyrics fetch."),
    ];

    public static LyricsOutcome Classify(LyricsFacts f)
    {
        if (f.Status == LyricsStatus.Instrumental)
            return new(InstrumentalLrclib, false);

        var hasTranscribed = f.HasTranscribedSynced || f.HasTranscribedPlain;
        if (SongMetadata.ComputeUseTranscribedForDisplay(f.Status, hasTranscribed, f.Preferred, f.HasSynced || f.HasPlain))
        {
            return f.HasTranscribedSynced && f.AlignedToReference
                ? new(AiTimedOfficial, true)
                : new(AiWritten, f.HasTranscribedSynced);
        }

        if (f.HasSynced)
        {
            if (f.HasTimingOffset)
                return new(AiTimedOfficial, true);
            return new(f.Origin == LyricsOrigin.Genius ? GeniusSynced : LrclibSynced, true);
        }

        if (f.HasPlain)
            return new(PlainOnly, false);

        if (SongMetadata.ComputeShowsAsInstrumental(f.Status, f.IsInstrumental, hasDisplayWords: false))
            return new(NoSingingAi, false);

        if (f.Transcription == TranscriptionStatus.Failed)
            return new(AiFailed, false);

        return f.Status switch
        {
            LyricsStatus.Failed => new(LookupFailed, false),
            LyricsStatus.NotFetched => new(f.HasTitleAndArtist ? NotLookedUp : CantLookUp, false),
            _ => new(NotFound, false),
        };
    }

    public static bool NeedsNoWords(string key) => key is InstrumentalLrclib or NoSingingAi;
}
