using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Matching;

/// <summary>
/// Exercises the shared <see cref="CatalogCandidateScorer"/> in isolation — the scoring algorithm the
/// Spotify, Deezer and Apple Music providers used to each carry a near-verbatim copy of. These pin the
/// signal-by-signal behaviour (identity → ISRC → duration → version → album → track-number) directly,
/// without standing up a provider, a catalog stub, or options.
/// </summary>
public class CatalogCandidateScorerTests
{
    private static readonly CatalogCandidateScorer.ScoringTuning SpotifyLikeTuning = new(
        DurationDeltaThresholdSeconds: 5,
        DurationMismatchPenalty: 0.5,
        VersionMismatchPenalty: 0.6,
        AlbumAgreementConfidenceBoost: 0.05,
        Isrc: new CatalogCandidateScorer.IsrcScoring(
            ConfidenceBoost: 0.1, MismatchPenalty: 0.65, NotOnCandidateWarning: "isrc_not_on_spotify_track"),
        TrackNumberBoost: 0.02);

    private static readonly CatalogCandidateScorer.ScoringTuning NoIsrcTuning = SpotifyLikeTuning with { Isrc = null };

    [Fact]
    public void ExactMatch_ScoresHighWithNoWarnings()
    {
        var song = Song(artist: "Juice WRLD", title: "Lucid Dreams", album: "Goodbye & Good Riddance", durationSec: 239);
        var source = Source("Juice WRLD", "Goodbye & Good Riddance", "Lucid Dreams");
        var candidate = Candidate("Juice WRLD", "Lucid Dreams", "Goodbye & Good Riddance", durationMs: 239_000);

        var (score, warnings) = CatalogCandidateScorer.Score(song, source, candidate, NoIsrcTuning);

        Assert.True(score > 0.95, $"expected a high score, got {score}");
        Assert.Empty(warnings);
    }

    [Fact]
    public void MatchingIsrc_BoostsScore_AndAddsNoWarning()
    {
        var song = Song("Juice WRLD", "Lucid Dreams", "Goodbye & Good Riddance", 239, isrc: "USUM71807840");
        var source = Source("Juice WRLD", "Goodbye & Good Riddance", "Lucid Dreams");
        var candidate = Candidate("Juice WRLD", "Lucid Dreams", "Goodbye & Good Riddance", 239_000, isrc: "us-um7-18-07840");

        var (score, warnings) = CatalogCandidateScorer.Score(song, source, candidate, SpotifyLikeTuning);

        // The hyphenated/lowercased candidate ISRC normalizes to the same value and boosts (clamped ≤ 1.0).
        Assert.True(score >= 1.0, $"expected ISRC boost to reach 1.0, got {score}");
        Assert.DoesNotContain("isrc_mismatch", warnings);
    }

    [Fact]
    public void ConflictingIsrc_PenalizesScore_AndWarns()
    {
        var song = Song("Juice WRLD", "Lucid Dreams", "Goodbye & Good Riddance", 239, isrc: "USUM71807840");
        var source = Source("Juice WRLD", "Goodbye & Good Riddance", "Lucid Dreams");
        var candidate = Candidate("Juice WRLD", "Lucid Dreams", "Goodbye & Good Riddance", 239_000, isrc: "GBAAA0000001");

        var (score, warnings) = CatalogCandidateScorer.Score(song, source, candidate, SpotifyLikeTuning);

        Assert.Contains("isrc_mismatch", warnings);
        Assert.True(score < 0.95, $"expected the mismatch penalty to drop the score, got {score}");
    }

    [Fact]
    public void FileIsrcAbsentFromCandidate_WarnsWithProviderSpecificMessage()
    {
        var song = Song("Juice WRLD", "Lucid Dreams", "Goodbye & Good Riddance", 239, isrc: "USUM71807840");
        var source = Source("Juice WRLD", "Goodbye & Good Riddance", "Lucid Dreams");
        var candidate = Candidate("Juice WRLD", "Lucid Dreams", "Goodbye & Good Riddance", 239_000, isrc: null);

        var (_, warnings) = CatalogCandidateScorer.Score(song, source, candidate, SpotifyLikeTuning);

        Assert.Contains("isrc_not_on_spotify_track", warnings);
        Assert.DoesNotContain("isrc_mismatch", warnings);
    }

    [Fact]
    public void DurationBeyondThreshold_WarnsAndPenalizes()
    {
        var song = Song("Juice WRLD", "Lucid Dreams", "Goodbye & Good Riddance", 239);
        var source = Source("Juice WRLD", "Goodbye & Good Riddance", "Lucid Dreams");
        var candidate = Candidate("Juice WRLD", "Lucid Dreams", "Goodbye & Good Riddance", durationMs: 60_000);

        var (_, warnings) = CatalogCandidateScorer.Score(song, source, candidate, NoIsrcTuning);

        Assert.Contains("duration_mismatch", warnings);
    }

    [Fact]
    public void DurationMismatch_WithConfirmedIsrc_IsAdvisoryNotBlocking()
    {
        // A 101Barz-style YouTube rip: long freestyle session, embedded ISRC that the catalog also
        // carries. The ISRC proves the recording, so the length delta must not block — it becomes the
        // non-blocking advisory and the score stays high enough to recommend a match.
        var song = Song("101Barz", "Nass Studiosessie 346", "Nass Studiosessie 346", durationSec: 444, isrc: "QZFYX2198474");
        var source = Source("101Barz", "Nass Studiosessie 346", "Nass Studiosessie 346");
        var candidate = Candidate("101Barz", "Nass Studiosessie 346", "Nass Studiosessie 346", durationMs: 300_000, isrc: "QZFYX2198474");

        var (score, warnings) = CatalogCandidateScorer.Score(song, source, candidate, SpotifyLikeTuning);

        Assert.Contains(MusicHoarder.Api.Matching.MatchWarnings.DurationMismatchIsrcConfirmed, warnings);
        Assert.DoesNotContain("duration_mismatch", warnings);
        Assert.False(MusicHoarder.Api.Matching.MatchWarnings.AnyBlocking(warnings), "ISRC-confirmed length delta must not block");
        Assert.True(score >= 0.95, $"identity is ISRC-proven, score should stay high, got {score}");
    }

    [Fact]
    public void DurationMismatch_WithConflictingIsrc_StillBlocks()
    {
        // ISRC present on both but different → the duration delta keeps the regular blocking warning
        // (the file ISRC didn't confirm this candidate).
        var song = Song("101Barz", "Nass Studiosessie 346", "Nass Studiosessie 346", durationSec: 444, isrc: "QZFYX2198474");
        var source = Source("101Barz", "Nass Studiosessie 346", "Nass Studiosessie 346");
        var candidate = Candidate("101Barz", "Nass Studiosessie 346", "Nass Studiosessie 346", durationMs: 300_000, isrc: "GBAAA0000001");

        var (_, warnings) = CatalogCandidateScorer.Score(song, source, candidate, SpotifyLikeTuning);

        Assert.Contains("duration_mismatch", warnings);
        Assert.Contains("isrc_mismatch", warnings);
        Assert.DoesNotContain(MusicHoarder.Api.Matching.MatchWarnings.DurationMismatchIsrcConfirmed, warnings);
    }

    [Fact]
    public void VersionQualifierMismatch_WarnsAndPenalizes()
    {
        // Studio source vs. a "Live" candidate — must not silently satisfy the request.
        var song = Song("Juice WRLD", "Lucid Dreams", "Goodbye & Good Riddance", 239);
        var source = Source("Juice WRLD", "Goodbye & Good Riddance", "Lucid Dreams");
        var candidate = Candidate("Juice WRLD", "Lucid Dreams (Live)", "Goodbye & Good Riddance", 239_000);

        var (_, warnings) = CatalogCandidateScorer.Score(song, source, candidate, NoIsrcTuning);

        Assert.Contains("version_mismatch", warnings);
    }

    [Fact]
    public void DifferentAlbum_WarnsButDoesNotPenalize()
    {
        var song = Song("Juice WRLD", "Lucid Dreams", "Goodbye & Good Riddance", 239);
        var source = Source("Juice WRLD", "Goodbye & Good Riddance", "Lucid Dreams");
        var candidate = Candidate("Juice WRLD", "Lucid Dreams", "Greatest Hits", 239_000);

        var (score, warnings) = CatalogCandidateScorer.Score(song, source, candidate, NoIsrcTuning);

        Assert.Contains("album_mismatch", warnings);
        // A different album is only a missing-confirmation signal: artist+title still score high.
        Assert.True(score > 0.9, $"album difference must not penalize, got {score}");
    }

    [Fact]
    public void TrackNumberBoost_OnlyAppliesWhenTuningOptsIn()
    {
        var song = Song("Juice WRLD", "Lucid Dreams", "Goodbye & Good Riddance", 239);
        var source = Source("Juice WRLD", "Goodbye & Good Riddance", "Lucid Dreams", trackNumber: 3);
        var candidate = Candidate("Juice WRLD", "Lucid Dreams", "Goodbye & Good Riddance", 239_000, trackNumber: 3);

        var withBoost = CatalogCandidateScorer.Score(song, source, candidate, SpotifyLikeTuning).Score;
        var withoutBoost = CatalogCandidateScorer.Score(song, source, candidate, NoIsrcTuning with { TrackNumberBoost = 0.0 }).Score;

        Assert.True(withBoost > withoutBoost, $"track-number boost should raise the score ({withBoost} vs {withoutBoost})");
    }

    private static CatalogCandidateScorer.CatalogCandidate Candidate(
        string? artist, string? title, string? album, int durationMs, string? isrc = null, int? trackNumber = null)
        => new(artist, title, album, isrc, durationMs, trackNumber);

    private static SongSearchText.Resolved Source(string? artist, string? album, string? title, int? trackNumber = null)
        => new(artist, album, title, trackNumber);

    private static SongMetadata Song(
        string? artist, string? title, string? album, int durationSec, string? isrc = null) => new()
    {
        OwnerUserId = WellKnownUsers.OwnerId,
        SourcePath = "/s/a.mp3",
        FileName = "a.mp3",
        Extension = ".mp3",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = artist,
        Title = title,
        Album = album,
        DurationSeconds = durationSec,
        Isrc = isrc,
        EnrichmentStatus = EnrichmentStatus.Pending,
    };
}
