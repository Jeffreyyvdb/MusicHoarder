using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

public class AcoustIdMatchValidatorTests
{
    private readonly AcoustIdMatchValidator _validator = new();

    [Fact]
    public void Validate_CleanMatch_ReturnsMatchedWithNoWarnings()
    {
        var match = CreateMatch(score: 0.95f, artist: "Juice WRLD", title: "Lucid Dreams",
            recordingDurationMs: 240_000);
        var track = CreateTrack(artist: "Juice WRLD", title: "Lucid Dreams", durationMs: 241_000);

        var result = _validator.Validate(match, track);

        Assert.Equal(EnrichmentStatus.Matched, result.RecommendedStatus);
        Assert.Empty(result.Warnings);
        Assert.Equal(0.95f, result.AdjustedScore);
        Assert.Null(result.WarningsJson);
    }

    [Fact]
    public void Validate_ArtistMismatch_PenalizesScoreBy50Percent()
    {
        var match = CreateMatch(score: 0.95f, artist: "Stevie Wonder", title: "Lucid Dreams",
            recordingDurationMs: 240_000);
        var track = CreateTrack(artist: "Juice WRLD", title: "Lucid Dreams", durationMs: 241_000);

        var result = _validator.Validate(match, track);

        Assert.Contains("artist_mismatch", result.Warnings);
        Assert.Equal(0.95f * 0.5f, result.AdjustedScore, precision: 3);
        Assert.Equal(EnrichmentStatus.NeedsReview, result.RecommendedStatus);
    }

    [Fact]
    public void Validate_DurationMismatch_PenalizesScoreBy30Percent()
    {
        var match = CreateMatch(score: 0.95f, artist: "Juice WRLD", title: "Lucid Dreams",
            recordingDurationMs: 261_000);
        var track = CreateTrack(artist: "Juice WRLD", title: "Lucid Dreams", durationMs: 240_000);

        var result = _validator.Validate(match, track);

        Assert.Contains("duration_mismatch", result.Warnings);
        Assert.Equal(0.95f * 0.7f, result.AdjustedScore, precision: 3);
        Assert.Equal(EnrichmentStatus.NeedsReview, result.RecommendedStatus);
    }

    [Fact]
    public void Validate_TitleMismatch_PenalizesScoreBy20Percent()
    {
        var match = CreateMatch(score: 0.95f, artist: "Juice WRLD", title: "All Girls Are The Same",
            recordingDurationMs: 240_000);
        var track = CreateTrack(artist: "Juice WRLD", title: "Lucid Dreams", durationMs: 241_000);

        var result = _validator.Validate(match, track);

        Assert.Contains("title_mismatch", result.Warnings);
        Assert.Equal(0.95f * 0.8f, result.AdjustedScore, precision: 3);
        Assert.Equal(EnrichmentStatus.NeedsReview, result.RecommendedStatus);
    }

    [Fact]
    public void Validate_LowRawScore_AddsWarning()
    {
        var match = CreateMatch(score: 0.60f, artist: "Juice WRLD", title: "Lucid Dreams",
            recordingDurationMs: 240_000);
        var track = CreateTrack(artist: "Juice WRLD", title: "Lucid Dreams", durationMs: 241_000);

        var result = _validator.Validate(match, track);

        Assert.Contains("low_acoustid_score", result.Warnings);
        Assert.Equal(EnrichmentStatus.NeedsReview, result.RecommendedStatus);
    }

    [Fact]
    public void Validate_MultipleCandidates_AddsWarning()
    {
        var match = CreateMatch(score: 0.95f, artist: "Juice WRLD", title: "Lucid Dreams",
            recordingDurationMs: 240_000, candidateCount: 3);
        var track = CreateTrack(artist: "Juice WRLD", title: "Lucid Dreams", durationMs: 241_000);

        var result = _validator.Validate(match, track);

        Assert.Contains("multiple_candidates", result.Warnings);
        Assert.Equal(EnrichmentStatus.Matched, result.RecommendedStatus);
    }

    [Fact]
    public void Validate_AllMismatches_StackPenalties()
    {
        var match = CreateMatch(score: 0.60f, artist: "Stevie Wonder", title: "Superstition",
            recordingDurationMs: 300_000, candidateCount: 2);
        var track = CreateTrack(artist: "Juice WRLD", title: "Lucid Dreams", durationMs: 240_000);

        var result = _validator.Validate(match, track);

        Assert.Contains("low_acoustid_score", result.Warnings);
        Assert.Contains("multiple_candidates", result.Warnings);
        Assert.Contains("artist_mismatch", result.Warnings);
        Assert.Contains("duration_mismatch", result.Warnings);
        Assert.Contains("title_mismatch", result.Warnings);
        Assert.Equal(5, result.Warnings.Count);

        var expectedScore = 0.60f * 0.5f * 0.7f * 0.8f;
        Assert.Equal(expectedScore, result.AdjustedScore, precision: 3);
        Assert.Equal(EnrichmentStatus.NeedsReview, result.RecommendedStatus);
    }

    [Fact]
    public void Validate_NullTrackArtist_SkipsArtistCheck()
    {
        var match = CreateMatch(score: 0.95f, artist: "Stevie Wonder", title: "Track",
            recordingDurationMs: 240_000);
        var track = CreateTrack(artist: null, title: "Track", durationMs: 241_000);

        var result = _validator.Validate(match, track);

        Assert.DoesNotContain("artist_mismatch", result.Warnings);
    }

    [Fact]
    public void Validate_NullTrackTitle_SkipsTitleCheck()
    {
        var match = CreateMatch(score: 0.95f, artist: "Juice WRLD", title: "Other Title",
            recordingDurationMs: 240_000);
        var track = CreateTrack(artist: "Juice WRLD", title: null, durationMs: 241_000);

        var result = _validator.Validate(match, track);

        Assert.DoesNotContain("title_mismatch", result.Warnings);
    }

    [Fact]
    public void Validate_NullDurations_SkipsDurationCheck()
    {
        var match = CreateMatch(score: 0.95f, artist: "Juice WRLD", title: "Lucid Dreams",
            recordingDurationMs: null);
        var track = CreateTrack(artist: "Juice WRLD", title: "Lucid Dreams", durationMs: null);

        var result = _validator.Validate(match, track);

        Assert.DoesNotContain("duration_mismatch", result.Warnings);
        Assert.Equal(EnrichmentStatus.Matched, result.RecommendedStatus);
    }

    [Fact]
    public void Validate_DurationWithinThreshold_NoPenalty()
    {
        var match = CreateMatch(score: 0.95f, artist: "Juice WRLD", title: "Lucid Dreams",
            recordingDurationMs: 244_000);
        var track = CreateTrack(artist: "Juice WRLD", title: "Lucid Dreams", durationMs: 240_000);

        var result = _validator.Validate(match, track);

        Assert.DoesNotContain("duration_mismatch", result.Warnings);
    }

    [Fact]
    public void Validate_ArtistPartialMatch_NoPenalty()
    {
        var match = CreateMatch(score: 0.95f, artist: "Juice WRLD; Marshmello", title: "Track",
            recordingDurationMs: 240_000);
        var track = CreateTrack(artist: "Juice WRLD", title: "Track", durationMs: 241_000);

        var result = _validator.Validate(match, track);

        Assert.DoesNotContain("artist_mismatch", result.Warnings);
    }

    [Fact]
    public void Validate_TitlePartialMatch_NoPenalty()
    {
        var match = CreateMatch(score: 0.95f, artist: "Juice WRLD", title: "Lucid Dreams (Remix)",
            recordingDurationMs: 240_000);
        var track = CreateTrack(artist: "Juice WRLD", title: "Lucid Dreams", durationMs: 241_000);

        var result = _validator.Validate(match, track);

        Assert.DoesNotContain("title_mismatch", result.Warnings);
    }

    [Fact]
    public void Validate_HighScoreWithWarnings_ForcesNeedsReview()
    {
        var match = CreateMatch(score: 0.99f, artist: "Juice WRLD", title: "Lucid Dreams",
            recordingDurationMs: 240_000);
        var track = CreateTrack(artist: "Other Artist", title: "Lucid Dreams", durationMs: 241_000);

        var result = _validator.Validate(match, track);

        Assert.Contains("artist_mismatch", result.Warnings);
        Assert.Equal(EnrichmentStatus.NeedsReview, result.RecommendedStatus);
    }

    [Fact]
    public void Validate_WarningsJson_SerializesCorrectly()
    {
        var match = CreateMatch(score: 0.60f, artist: "Stevie Wonder", title: "Lucid Dreams",
            recordingDurationMs: 240_000);
        var track = CreateTrack(artist: "Juice WRLD", title: "Lucid Dreams", durationMs: 241_000);

        var result = _validator.Validate(match, track);

        Assert.NotNull(result.WarningsJson);
        Assert.Contains("low_acoustid_score", result.WarningsJson);
        Assert.Contains("artist_mismatch", result.WarningsJson);
    }

    [Fact]
    public void Validate_CaseInsensitiveArtistMatch_NoPenalty()
    {
        var match = CreateMatch(score: 0.95f, artist: "juice wrld", title: "Lucid Dreams",
            recordingDurationMs: 240_000);
        var track = CreateTrack(artist: "Juice WRLD", title: "Lucid Dreams", durationMs: 241_000);

        var result = _validator.Validate(match, track);

        Assert.DoesNotContain("artist_mismatch", result.Warnings);
    }

    [Fact]
    public void Validate_ApostropheVariations_NoPenalty()
    {
        var match = CreateMatch(score: 0.95f, artist: "Juice WRLD", title: "Don't Stop",
            recordingDurationMs: 240_000);
        var track = CreateTrack(artist: "Juice WRLD", title: "Don\u2019t Stop", durationMs: 241_000);

        var result = _validator.Validate(match, track);

        Assert.DoesNotContain("title_mismatch", result.Warnings);
    }

    private static AcoustIdMatch CreateMatch(
        float score,
        string artist,
        string title,
        int? recordingDurationMs,
        int candidateCount = 1)
    {
        return new AcoustIdMatch(
            MusicBrainzRecordingId: "mb-123",
            Title: title,
            Artist: artist,
            AlbumArtist: artist,
            Score: score,
            RecordingDurationMs: recordingDurationMs,
            CandidateCount: candidateCount);
    }

    private static SongMetadata CreateTrack(
        string? artist,
        string? title,
        int? durationMs)
    {
        return new SongMetadata
        {
            SourcePath = "/source/track.mp3",
            FileName = "track.mp3",
            Extension = ".mp3",
            FileSizeBytes = 5000,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = artist,
            Title = title,
            DurationMs = durationMs,
            DurationSeconds = durationMs.HasValue ? durationMs.Value / 1000 : null
        };
    }
}
