using MusicHoarder.Api.BackgroundWork;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.BackgroundWork;

public class LyricsOutcomeClassifierTests
{
    private static LyricsFacts Facts(
        LyricsStatus status = LyricsStatus.NotFetched,
        LyricsOrigin origin = LyricsOrigin.Lrclib,
        bool? instrumental = null,
        bool synced = false,
        bool plain = false,
        bool tSynced = false,
        bool tPlain = false,
        PreferredLyricsSource preferred = PreferredLyricsSource.Lrclib,
        TranscriptionStatus transcription = TranscriptionStatus.NotRequested,
        bool aligned = false,
        bool offset = false,
        bool named = true)
        => new(status, origin, instrumental, synced, plain, tSynced, tPlain, preferred, transcription, aligned, offset, named);

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_song_lands_in_the_segment_the_player_would_describe(string expected, LyricsFacts facts)
        => Assert.Equal(expected, LyricsOutcomes.Classify(facts).Key);

    public static IEnumerable<object[]> Cases() =>
    [
        [LyricsOutcomes.LrclibSynced, Facts(LyricsStatus.Fetched, synced: true, plain: true)],
        [LyricsOutcomes.GeniusSynced, Facts(LyricsStatus.Fetched, LyricsOrigin.Genius, synced: true)],
        // A probe shifted the LRC by a measured offset: official words, AI timing.
        [LyricsOutcomes.AiTimedOfficial, Facts(LyricsStatus.Fetched, synced: true, offset: true)],
        // The AI sync pass aligned a transcript to the official words and the player shows it.
        [LyricsOutcomes.AiTimedOfficial, Facts(LyricsStatus.Fetched, plain: true, tSynced: true, tPlain: true, preferred: PreferredLyricsSource.Transcribed, aligned: true)],
        // Nothing official, so the AI's own words are all there is.
        [LyricsOutcomes.AiWritten, Facts(LyricsStatus.NotFound, tSynced: true, tPlain: true, transcription: TranscriptionStatus.Completed)],
        [LyricsOutcomes.PlainOnly, Facts(LyricsStatus.Fetched, plain: true)],
        // The official words are plain only and the AI timed them, but nobody chose the AI version: the player shows plain.
        [LyricsOutcomes.PlainOnly, Facts(LyricsStatus.Fetched, plain: true, tSynced: true, aligned: true)],
        [LyricsOutcomes.InstrumentalLrclib, Facts(LyricsStatus.Instrumental, instrumental: true)],
        // LRCLIB's verdict outranks an earlier transcript.
        [LyricsOutcomes.InstrumentalLrclib, Facts(LyricsStatus.Instrumental, tSynced: true, preferred: PreferredLyricsSource.Transcribed)],
        [LyricsOutcomes.NoSingingAi, Facts(LyricsStatus.NotFound, instrumental: true, transcription: TranscriptionStatus.Completed)],
        [LyricsOutcomes.AiFailed, Facts(LyricsStatus.NotFound, transcription: TranscriptionStatus.Failed)],
        [LyricsOutcomes.NotFound, Facts(LyricsStatus.NotFound)],
        [LyricsOutcomes.LookupFailed, Facts(LyricsStatus.Failed)],
        [LyricsOutcomes.NotLookedUp, Facts(LyricsStatus.NotFetched)],
        [LyricsOutcomes.CantLookUp, Facts(LyricsStatus.NotFetched, named: false)],
    ];

    [Fact]
    public void Only_a_synced_lyric_the_player_shows_counts_as_synced()
    {
        Assert.True(LyricsOutcomes.Classify(Facts(LyricsStatus.Fetched, synced: true)).ShowsSyncedLyrics);
        Assert.True(LyricsOutcomes.Classify(Facts(LyricsStatus.NotFound, tSynced: true, tPlain: true)).ShowsSyncedLyrics);
        Assert.False(LyricsOutcomes.Classify(Facts(LyricsStatus.NotFound, tPlain: true)).ShowsSyncedLyrics);
        Assert.False(LyricsOutcomes.Classify(Facts(LyricsStatus.Fetched, plain: true)).ShowsSyncedLyrics);
    }

    [Fact]
    public void Every_combination_agrees_with_what_the_song_entity_displays()
    {
        var bools = new[] { false, true };
        var combos =
            from status in Enum.GetValues<LyricsStatus>()
            from origin in Enum.GetValues<LyricsOrigin>()
            from synced in bools
            from plain in bools
            from tSynced in bools
            from tPlain in bools
            from preferred in Enum.GetValues<PreferredLyricsSource>()
            from instrumental in new bool?[] { null, false, true }
            from aligned in bools
            from offset in bools
            from transcription in Enum.GetValues<TranscriptionStatus>()
            select Facts(status, origin, instrumental, synced, plain, tSynced, tPlain, preferred, transcription, aligned, offset);

        var count = 0;
        foreach (var facts in combos)
        {
            count++;
            var song = BackgroundWorkTestData.NewSong(1, s =>
            {
                s.LyricsStatus = facts.Status;
                s.LyricsOrigin = facts.Origin;
                s.IsInstrumental = facts.IsInstrumental;
                s.SyncedLyrics = facts.HasSynced ? "[00:01.00] a" : null;
                s.PlainLyrics = facts.HasPlain ? "a" : null;
                s.TranscribedSyncedLyrics = facts.HasTranscribedSynced ? "[00:01.00] b" : null;
                s.TranscribedPlainLyrics = facts.HasTranscribedPlain ? "b" : null;
                s.PreferredLyricsSource = facts.Preferred;
                s.TranscriptionStatus = facts.Transcription;
                s.TranscriptionAlignedToReference = facts.AlignedToReference;
                s.LyricsSyncOffsetMs = facts.HasTimingOffset ? 400 : null;
            });

            var outcome = LyricsOutcomes.Classify(facts);

            Assert.Contains(LyricsOutcomes.Segments, s => s.Key == outcome.Key);
            if (facts.Status != LyricsStatus.Instrumental)
                Assert.Equal(!string.IsNullOrWhiteSpace(song.DisplaySyncedLyrics), outcome.ShowsSyncedLyrics);
            var isAi = outcome.Key is LyricsOutcomes.AiTimedOfficial or LyricsOutcomes.AiWritten;
            if (outcome.ShowsSyncedLyrics)
            {
                // The chart's "AI" segments are exactly the player's AI badge.
                Assert.Equal(song.LyricsProvenance != LyricsProvenance.Human, isAi);
            }

            var instrumentalKey = outcome.Key is LyricsOutcomes.InstrumentalLrclib or LyricsOutcomes.NoSingingAi;
            Assert.Equal(song.ShowsAsInstrumental, instrumentalKey);
        }

        Assert.True(count > 2000);
    }

    [Fact]
    public void Every_segment_has_a_unique_key_and_a_label()
    {
        Assert.Equal(LyricsOutcomes.Segments.Count, LyricsOutcomes.Segments.Select(s => s.Key).Distinct().Count());
        Assert.All(LyricsOutcomes.Segments, s => Assert.False(string.IsNullOrWhiteSpace(s.Label)));
    }
}
