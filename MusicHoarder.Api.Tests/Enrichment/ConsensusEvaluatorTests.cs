using System.Text.Json;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

public class ConsensusEvaluatorTests
{
    private static readonly IdentityMatchOptions Opts = IdentityMatchOptions.Default;

    [Fact]
    public void AcoustIdAlone_CleanUnambiguousMatch_Matched()
    {
        // A clean fingerprint match (validator recommended Matched, no warnings) stands alone
        // once it's the only enabled provider and has had its turn.
        var song = Song();
        Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
            Result("Artist", "Title", mbid: "mb-1", conf: 0.95, recommend: EnrichmentStatus.Matched));

        var r = ConsensusEvaluator.Evaluate(song, Enabled(EnrichmentProvider.AcoustID), Opts);

        Assert.Equal(EnrichmentStatus.Matched, r.Status);
        Assert.Equal(EnrichmentProvider.AcoustID, Assert.Single(r.AgreeingProviders));
    }

    [Fact]
    public void AcoustIdAlone_AmbiguousFingerprint_NeedsReview()
    {
        // multiple_candidates means the fingerprint resolved to several recordings — never auto-stand.
        var song = Song();
        Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
            Result("Artist", "Title", mbid: "mb-1", conf: 0.95, recommend: EnrichmentStatus.Matched,
                warnings: ["multiple_candidates"]));

        var r = ConsensusEvaluator.Evaluate(song, Enabled(EnrichmentProvider.AcoustID), Opts);

        Assert.Equal(EnrichmentStatus.NeedsReview, r.Status);
    }

    [Fact]
    public void AcoustIdAlone_RecommendNeedsReview_NeedsReview()
    {
        var song = Song();
        Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
            Result("Artist", "Title", mbid: "mb-1", conf: 0.7, recommend: EnrichmentStatus.NeedsReview));

        var r = ConsensusEvaluator.Evaluate(song, Enabled(EnrichmentProvider.AcoustID), Opts);

        Assert.Equal(EnrichmentStatus.NeedsReview, r.Status);
    }

    [Fact]
    public void AcoustIdCleanMatch_ButNameBasedProviderNotYetAttempted_Pending()
    {
        // The clean fingerprint must wait until every enabled provider has had its turn, so a
        // name-based corroborator/contradictor can weigh in before it stands alone.
        var song = Song();
        Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
            Result("Artist", "Title", mbid: "mb-1", conf: 0.95, recommend: EnrichmentStatus.Matched));
        // SpotifyAPI enabled but has no attempt yet.

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.AcoustID, EnrichmentProvider.SpotifyAPI), Opts);

        Assert.Equal(EnrichmentStatus.Pending, r.Status);
    }

    [Fact]
    public void AcoustIdPlusMusicBrainzAgree_Matched()
    {
        var song = Song();
        Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
            Result("Daft Punk", "One More Time", mbid: "mb-1", conf: 0.8, recommend: EnrichmentStatus.Matched));
        Add(song, EnrichmentProvider.MusicBrainzWeb, ProviderAttemptStatus.Matched,
            Result("Daft Punk", "One More Time", mbid: "mb-1", conf: 0.85, recommend: EnrichmentStatus.Matched));

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.AcoustID, EnrichmentProvider.MusicBrainzWeb), Opts);

        Assert.Equal(EnrichmentStatus.Matched, r.Status);
        Assert.Equal(2, r.AgreeingProviders.Count);
    }

    [Fact]
    public void DeezerPlusSpotifyAgree_Matched()
    {
        // Two name-based providers landing on the same identity (shared ISRC) corroborate each
        // other → Matched, even though neither would stand solo here.
        var song = Song();
        Add(song, EnrichmentProvider.Deezer, ProviderAttemptStatus.Matched,
            Result("Juice WRLD", "Lucid Dreams", isrc: "USUM71807840", conf: 0.8, recommend: EnrichmentStatus.NeedsReview));
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched,
            Result("Juice WRLD", "Lucid Dreams", spotifyId: "spot-1", isrc: "USUM71807840", conf: 0.82, recommend: EnrichmentStatus.NeedsReview));

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.Deezer, EnrichmentProvider.SpotifyAPI), Opts);

        Assert.Equal(EnrichmentStatus.Matched, r.Status);
        Assert.Equal(2, r.AgreeingProviders.Count);
    }

    [Fact]
    public void AcoustIdAndSpotifyDisagree_NoFalseConsensus_NeedsReview()
    {
        var song = Song();
        Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
            Result("Arctic Monkeys", "Balaclava", mbid: "mb-wrong", conf: 0.7, recommend: EnrichmentStatus.NeedsReview));
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched,
            Result("EsDeeKid", "4 Raws", spotifyId: "spot-1", conf: 0.7, recommend: EnrichmentStatus.NeedsReview));

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.AcoustID, EnrichmentProvider.SpotifyAPI), Opts);

        Assert.Equal(EnrichmentStatus.NeedsReview, r.Status);
    }

    [Fact]
    public void TwoProvidersAgreeViaIsrc_DespiteNameTypo_Matched()
    {
        var song = Song();
        Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
            Result("The Beatles", "Hey Jude", isrc: "GBAYE6800001", conf: 0.7, recommend: EnrichmentStatus.NeedsReview));
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched,
            Result("Beatles", "Hey Jude (typo)", isrc: "GB-AYE-68-00001", spotifyId: "s", conf: 0.7, recommend: EnrichmentStatus.NeedsReview));

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.AcoustID, EnrichmentProvider.SpotifyAPI), Opts);

        Assert.Equal(EnrichmentStatus.Matched, r.Status);
    }

    [Fact]
    public void VersionMismatch_DoesNotCorroborate_NeedsReview()
    {
        var song = Song();
        Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
            Result("Band", "Anthem", conf: 0.8, recommend: EnrichmentStatus.Matched));
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched,
            Result("Band", "Anthem (Live)", spotifyId: "s", conf: 0.8, recommend: EnrichmentStatus.NeedsReview));

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.AcoustID, EnrichmentProvider.SpotifyAPI), Opts);

        Assert.Equal(EnrichmentStatus.NeedsReview, r.Status);
    }

    [Fact]
    public void NameBasedSolo_RecommendedMatched_Matched()
    {
        var song = Song();
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched,
            Result("Artist", "Title", spotifyId: "s", conf: 0.92, recommend: EnrichmentStatus.Matched));

        var r = ConsensusEvaluator.Evaluate(song, Enabled(EnrichmentProvider.SpotifyAPI), Opts);

        Assert.Equal(EnrichmentStatus.Matched, r.Status);
    }

    [Fact]
    public void TwoNameBasedProvidersDisagree_NeedsReview()
    {
        // Two name-based providers each confident enough to recommend Matched, but landing on
        // *different* identities. Neither may stand alone — the genuine conflict goes to review
        // rather than silently auto-matching whichever happened to score higher.
        var song = Song();
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched,
            Result("Drake", "One Dance", spotifyId: "s", conf: 0.9, recommend: EnrichmentStatus.Matched));
        Add(song, EnrichmentProvider.Deezer, ProviderAttemptStatus.Matched,
            Result("Wizkid", "Come Closer", conf: 0.88, recommend: EnrichmentStatus.Matched));

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.SpotifyAPI, EnrichmentProvider.Deezer), Opts);

        Assert.Equal(EnrichmentStatus.NeedsReview, r.Status);
    }

    [Fact]
    public void NameBasedSolo_ContradictedOnlyByWeakCandidate_StillMatched()
    {
        // A sub-floor candidate on a different identity is too weak to vote, so it must not block
        // a confident name-based provider from standing alone (the guard only weighs strong votes).
        var song = Song();
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched,
            Result("Drake", "One Dance", spotifyId: "s", conf: 0.92, recommend: EnrichmentStatus.Matched));
        Add(song, EnrichmentProvider.Deezer, ProviderAttemptStatus.Matched,
            Result("Wizkid", "Come Closer", conf: 0.3, recommend: EnrichmentStatus.NeedsReview)); // below floor

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.SpotifyAPI, EnrichmentProvider.Deezer), Opts);

        Assert.Equal(EnrichmentStatus.Matched, r.Status);
        Assert.Equal(EnrichmentProvider.SpotifyAPI, Assert.Single(r.AgreeingProviders));
    }

    [Fact]
    public void TrackerSolo_RecommendedMatched_Matched()
    {
        // A confident community-tracker match stands alone: mainstream catalogs can't corroborate
        // an unreleased leak, so we trust the tracker's own tuned threshold.
        var song = Song();
        Add(song, EnrichmentProvider.Tracker, ProviderAttemptStatus.Matched,
            Result("Juice WRLD", "2MININHELL", conf: 0.95, recommend: EnrichmentStatus.Matched));

        var r = ConsensusEvaluator.Evaluate(song, Enabled(EnrichmentProvider.Tracker), Opts);

        Assert.Equal(EnrichmentStatus.Matched, r.Status);
        Assert.Equal(EnrichmentProvider.Tracker, Assert.Single(r.AgreeingProviders));
    }

    [Fact]
    public void TrackerSolo_RecommendedNeedsReview_NeedsReview()
    {
        var song = Song();
        Add(song, EnrichmentProvider.Tracker, ProviderAttemptStatus.Matched,
            Result("Juice WRLD", "2MININHELL", conf: 0.72, recommend: EnrichmentStatus.NeedsReview));

        var r = ConsensusEvaluator.Evaluate(song, Enabled(EnrichmentProvider.Tracker), Opts);

        Assert.Equal(EnrichmentStatus.NeedsReview, r.Status);
    }

    [Fact]
    public void TrackerAndSpotifyAgree_StillCorroboratesAsCluster()
    {
        var song = Song();
        Add(song, EnrichmentProvider.Tracker, ProviderAttemptStatus.Matched,
            Result("Juice WRLD", "Lucid Dreams", conf: 0.8, recommend: EnrichmentStatus.NeedsReview));
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched,
            Result("Juice WRLD", "Lucid Dreams", spotifyId: "s", conf: 0.8, recommend: EnrichmentStatus.NeedsReview));

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.Tracker, EnrichmentProvider.SpotifyAPI), Opts);

        Assert.Equal(EnrichmentStatus.Matched, r.Status);
        Assert.Equal(2, r.AgreeingProviders.Count);
    }

    [Fact]
    public void TrackerMatched_BeatsAgreeingMainstream_TrackerWinsSolo()
    {
        // Strong preference: even when a mainstream provider lands on the same identity, the
        // confident tracker match wins outright and supplies the tags (its catalog of leaks /
        // alternate versions is richer for the artists it covers).
        var song = Song();
        Add(song, EnrichmentProvider.Tracker, ProviderAttemptStatus.Matched,
            Result("Juice WRLD", "Lucid Dreams", conf: 0.9, recommend: EnrichmentStatus.Matched));
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched,
            Result("Juice WRLD", "Lucid Dreams", spotifyId: "s", conf: 0.95, recommend: EnrichmentStatus.Matched));

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.Tracker, EnrichmentProvider.SpotifyAPI), Opts);

        Assert.Equal(EnrichmentStatus.Matched, r.Status);
        Assert.Equal(EnrichmentProvider.Tracker, Assert.Single(r.AgreeingProviders));
    }

    [Fact]
    public void TrackerMatched_BeatsConflictingMainstream_TrackerWins()
    {
        // The tracker matched a leaked/alternate version; a mainstream provider matched a
        // different (official) identity. The tracker still wins for the songs it covers.
        var song = Song();
        Add(song, EnrichmentProvider.Tracker, ProviderAttemptStatus.Matched,
            Result("Juice WRLD", "Lucid Dreams (OG Demo)", conf: 0.9, recommend: EnrichmentStatus.Matched));
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched,
            Result("Juice WRLD", "Lucid Dreams", spotifyId: "s", conf: 0.97, recommend: EnrichmentStatus.Matched));

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.Tracker, EnrichmentProvider.SpotifyAPI), Opts);

        Assert.Equal(EnrichmentStatus.Matched, r.Status);
        Assert.Equal(EnrichmentProvider.Tracker, Assert.Single(r.AgreeingProviders));
        Assert.Equal("Lucid Dreams (OG Demo)", r.Winner!.Title);
    }

    [Fact]
    public void TrackerMatched_WhileMainstreamRateLimited_TrackerWinsNotPending()
    {
        // A rate-limited mainstream provider can't stall an already-authoritative tracker answer.
        var song = Song();
        Add(song, EnrichmentProvider.Tracker, ProviderAttemptStatus.Matched,
            Result("Juice WRLD", "2MININHELL", conf: 0.92, recommend: EnrichmentStatus.Matched));
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.RateLimited, null);

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.Tracker, EnrichmentProvider.SpotifyAPI), Opts);

        Assert.Equal(EnrichmentStatus.Matched, r.Status);
        Assert.Equal(EnrichmentProvider.Tracker, Assert.Single(r.AgreeingProviders));
    }

    [Fact]
    public void TwoProvidersAgreeOnName_ButDurationDiffers_NoConsensus_NeedsReview()
    {
        // Same artist/title and no shared identifier, but the two providers matched recordings of
        // very different length (e.g. a radio edit vs. an extended cut). Duration now participates
        // in identity, so they no longer falsely corroborate into a Matched consensus.
        var song = Song();
        Add(song, EnrichmentProvider.Deezer, ProviderAttemptStatus.Matched,
            Result("Artist", "Title", conf: 0.8, recommend: EnrichmentStatus.NeedsReview, durationSeconds: 180));
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched,
            Result("Artist", "Title", spotifyId: "s", conf: 0.8, recommend: EnrichmentStatus.NeedsReview, durationSeconds: 420));

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.Deezer, EnrichmentProvider.SpotifyAPI), Opts);

        Assert.Equal(EnrichmentStatus.NeedsReview, r.Status);
    }

    [Fact]
    public void TwoProvidersAgreeOnNameAndDuration_Matched()
    {
        // Control for the duration guard: same name, no shared identifier, durations within the
        // delta → they still corroborate into a Matched consensus.
        var song = Song();
        Add(song, EnrichmentProvider.Deezer, ProviderAttemptStatus.Matched,
            Result("Artist", "Title", conf: 0.8, recommend: EnrichmentStatus.NeedsReview, durationSeconds: 200));
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched,
            Result("Artist", "Title", spotifyId: "s", conf: 0.8, recommend: EnrichmentStatus.NeedsReview, durationSeconds: 203));

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.Deezer, EnrichmentProvider.SpotifyAPI), Opts);

        Assert.Equal(EnrichmentStatus.Matched, r.Status);
        Assert.Equal(2, r.AgreeingProviders.Count);
    }

    [Fact]
    public void SubFloorCandidate_DoesNotCorroborate()
    {
        var song = Song();
        Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
            Result("Artist", "Title", conf: 0.3, recommend: EnrichmentStatus.NeedsReview)); // below floor
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched,
            Result("Artist", "Title", spotifyId: "s", conf: 0.78, recommend: EnrichmentStatus.NeedsReview));

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.AcoustID, EnrichmentProvider.SpotifyAPI), Opts);

        // Spotify is name-based but recommended NeedsReview, AcoustID below floor → no match.
        Assert.Equal(EnrichmentStatus.NeedsReview, r.Status);
        Assert.Equal("SpotifyAPI", r.Winner!.MatchedBy);
        Assert.Equal(0.78, r.Confidence);
    }

    [Fact]
    public void RateLimitedAttempt_Pending()
    {
        var song = Song();
        Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.RateLimited, null);

        var r = ConsensusEvaluator.Evaluate(song, Enabled(EnrichmentProvider.AcoustID), Opts);

        Assert.Equal(EnrichmentStatus.Pending, r.Status);
    }

    [Fact]
    public void RateLimitedProvider_ButTwoOthersAgree_MatchedImmediately()
    {
        // The core fix: a rate-limited provider must not discard a match the others already
        // corroborate. Previously any rate-limited attempt forced the whole song to Pending.
        var now = DateTime.UtcNow;
        var song = Song();
        Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
            Result("Daft Punk", "One More Time", mbid: "mb-1", conf: 0.85, recommend: EnrichmentStatus.Matched));
        Add(song, EnrichmentProvider.MusicBrainzWeb, ProviderAttemptStatus.Matched,
            Result("Daft Punk", "One More Time", mbid: "mb-1", conf: 0.9, recommend: EnrichmentStatus.Matched));
        AddRateLimited(song, EnrichmentProvider.AppleMusic, since: now);

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.AcoustID, EnrichmentProvider.MusicBrainzWeb, EnrichmentProvider.AppleMusic),
            Opts, utcNow: now, maxRateLimitDeferral: TimeSpan.FromMinutes(30));

        Assert.Equal(EnrichmentStatus.Matched, r.Status);
        Assert.Equal(2, r.AgreeingProviders.Count);
    }

    [Fact]
    public void FreshRateLimit_WeakVerdict_DefersPending()
    {
        // Only a single sub-standalone candidate; a still-fresh rate-limited provider could yet
        // corroborate, so we keep waiting (Pending) within the deferral window.
        var now = DateTime.UtcNow;
        var song = Song();
        Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
            Result("Artist", "Title", conf: 0.7, recommend: EnrichmentStatus.NeedsReview));
        AddRateLimited(song, EnrichmentProvider.AppleMusic, since: now);

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.AcoustID, EnrichmentProvider.AppleMusic),
            Opts, utcNow: now, maxRateLimitDeferral: TimeSpan.FromMinutes(30));

        Assert.Equal(EnrichmentStatus.Pending, r.Status);
    }

    [Fact]
    public void StaleRateLimit_WeakVerdict_FinalizesAsNeedsReview()
    {
        // Once the rate-limit has outlived the deferral window, stop waiting on it and surface the
        // song for review instead of dead-ending in Pending forever.
        var now = DateTime.UtcNow;
        var song = Song();
        Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
            Result("Artist", "Title", conf: 0.7, recommend: EnrichmentStatus.NeedsReview));
        AddRateLimited(song, EnrichmentProvider.AppleMusic, since: now - TimeSpan.FromMinutes(31));

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.AcoustID, EnrichmentProvider.AppleMusic),
            Opts, utcNow: now, maxRateLimitDeferral: TimeSpan.FromMinutes(30));

        Assert.Equal(EnrichmentStatus.NeedsReview, r.Status);
    }

    [Fact]
    public void StaleRateLimit_TwoOthersAgree_StillMatched()
    {
        var now = DateTime.UtcNow;
        var song = Song();
        Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
            Result("Daft Punk", "One More Time", mbid: "mb-1", conf: 0.85, recommend: EnrichmentStatus.Matched));
        Add(song, EnrichmentProvider.MusicBrainzWeb, ProviderAttemptStatus.Matched,
            Result("Daft Punk", "One More Time", mbid: "mb-1", conf: 0.9, recommend: EnrichmentStatus.Matched));
        AddRateLimited(song, EnrichmentProvider.AppleMusic, since: now - TimeSpan.FromMinutes(31));

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.AcoustID, EnrichmentProvider.MusicBrainzWeb, EnrichmentProvider.AppleMusic),
            Opts, utcNow: now, maxRateLimitDeferral: TimeSpan.FromMinutes(30));

        Assert.Equal(EnrichmentStatus.Matched, r.Status);
    }

    [Fact]
    public void NotAllProvidersAttempted_Pending()
    {
        var song = Song();
        Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
            Result("Artist", "Title", conf: 0.7, recommend: EnrichmentStatus.NeedsReview));
        // SpotifyAPI enabled but has no attempt yet.

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.AcoustID, EnrichmentProvider.SpotifyAPI), Opts);

        Assert.Equal(EnrichmentStatus.Pending, r.Status);
    }

    [Fact]
    public void SameRecordingDifferentRelease_AlbumFieldsFromCorroboratedOriginal()
    {
        // Three providers agree on the recording (shared ISRC / name) but split on the release:
        // Deezer attributes it to a "Greatest Hits" compilation, Spotify + Apple to the original
        // "The Score". The recording identity winner may be Deezer, but the album-level fields must
        // come from the corroborated original, and those fields are reported as corroborated.
        var song = Song();
        song.Album = "The Score (Expanded Edition)";
        song.Year = 1996;
        Add(song, EnrichmentProvider.Deezer, ProviderAttemptStatus.Matched,
            FullResult("Deezer", "Fugees", "Ready or Not", isrc: "USSM19600051",
                album: "Greatest Hits", year: 2000, track: 3, conf: 0.99));
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched,
            FullResult("SpotifyAPI", "Fugees", "Ready or Not", isrc: "USSM19600051", spotifyId: "s-1",
                album: "The Score (Expanded Edition)", year: 1996, track: 3, conf: 0.99));
        Add(song, EnrichmentProvider.AppleMusic, ProviderAttemptStatus.Matched,
            FullResult("AppleMusic", "Fugees", "Ready or Not",
                album: "The Score", year: 1996, track: 3, conf: 0.99));

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.Deezer, EnrichmentProvider.SpotifyAPI, EnrichmentProvider.AppleMusic), Opts);

        Assert.Equal(EnrichmentStatus.Matched, r.Status);
        Assert.Equal("The Score (Expanded Edition)", r.Winner!.Album);
        Assert.Equal(1996, r.Winner.Year);
        Assert.NotNull(r.CorroboratedFields);
        Assert.Contains("Album", r.CorroboratedFields!);
        Assert.Contains("Year", r.CorroboratedFields!);
    }

    [Fact]
    public void Cluster_WinnerHasBlockingWarning_DowngradedToNeedsReview()
    {
        // AcoustID's fingerprint resolved to a wrong recording; MusicBrainzWeb landed on the same
        // (wrong) MBID, so they cluster via the shared identifier. But the winner carries
        // title_mismatch against the file's own title — the signature of a wrong recording several
        // providers echoed. A cluster is not a license to overwrite the file with a mismatch.
        var song = Song();
        Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
            Result("Travis Scott", "STOP TRYING TO BE GOD", mbid: "mb-wrong", conf: 0.78,
                recommend: EnrichmentStatus.NeedsReview, warnings: ["title_mismatch"]));
        Add(song, EnrichmentProvider.MusicBrainzWeb, ProviderAttemptStatus.Matched,
            Result("Travis Scott", "STOP TRYING TO BE GOD", mbid: "mb-wrong", conf: 0.9,
                recommend: EnrichmentStatus.NeedsReview, warnings: ["title_mismatch", "album_mismatch"]));

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.AcoustID, EnrichmentProvider.MusicBrainzWeb), Opts);

        Assert.Equal(EnrichmentStatus.NeedsReview, r.Status);
    }

    [Fact]
    public void Cluster_ContradictedByCleanIndependentNameBased_NeedsReview()
    {
        // The cluster (AcoustID + MusicBrainzWeb on a shared MBID) is itself clean, but Apple Music
        // independently matched a DIFFERENT identity at full confidence with no warnings. That's a
        // genuine conflict — defer to review rather than auto-matching the cluster over a clean
        // dissenter that may well be the correct song.
        var song = Song();
        Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
            Result("Travis Scott", "STOP TRYING TO BE GOD", mbid: "mb-x", conf: 0.8,
                recommend: EnrichmentStatus.Matched));
        Add(song, EnrichmentProvider.MusicBrainzWeb, ProviderAttemptStatus.Matched,
            Result("Travis Scott", "STOP TRYING TO BE GOD", mbid: "mb-x", conf: 0.9,
                recommend: EnrichmentStatus.Matched));
        Add(song, EnrichmentProvider.AppleMusic, ProviderAttemptStatus.Matched,
            FullResult("AppleMusic", "Travis Scott", "90210", album: "Rodeo", year: 2015, conf: 1.0));

        var r = ConsensusEvaluator.Evaluate(
            song,
            Enabled(EnrichmentProvider.AcoustID, EnrichmentProvider.MusicBrainzWeb, EnrichmentProvider.AppleMusic),
            Opts);

        Assert.Equal(EnrichmentStatus.NeedsReview, r.Status);
    }

    [Fact]
    public void Cluster_CleanAgreement_NoContradiction_StillMatched()
    {
        // No-regression: three providers independently agree on one clean identity with no blocking
        // warnings and no dissenter → still Matched. The new guards must not break honest consensus.
        var song = Song();
        Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
            Result("Daft Punk", "One More Time", mbid: "mb-1", conf: 0.85, recommend: EnrichmentStatus.Matched));
        Add(song, EnrichmentProvider.MusicBrainzWeb, ProviderAttemptStatus.Matched,
            Result("Daft Punk", "One More Time", mbid: "mb-1", conf: 0.9, recommend: EnrichmentStatus.Matched));
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched,
            Result("Daft Punk", "One More Time", spotifyId: "s", conf: 0.88, recommend: EnrichmentStatus.Matched));

        var r = ConsensusEvaluator.Evaluate(
            song,
            Enabled(EnrichmentProvider.AcoustID, EnrichmentProvider.MusicBrainzWeb, EnrichmentProvider.SpotifyAPI),
            Opts);

        Assert.Equal(EnrichmentStatus.Matched, r.Status);
        Assert.Equal(3, r.AgreeingProviders.Count);
    }

    [Fact]
    public void NinetyTwoTen_WrongFingerprintEchoed_DoesNotAutoMatch_NeedsReview()
    {
        // Regression for the Travis Scott "90210" dossier (AI grader: "Wrong"): AcoustID's
        // fingerprint resolved to the wrong recording ("STOP TRYING TO BE GOD") and MusicBrainzWeb
        // echoed the same wrong MBID. Apple Music independently matched the CORRECT song ("90210")
        // at confidence 1.0 with no warnings. The pipeline used to auto-match the wrong cluster;
        // it must now route to review (the blocking-warning gate fires first; the clean-dissenter
        // guard would also catch it).
        var song = Song();
        song.Title = "90210";
        Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
            Result("Travis Scott", "STOP TRYING TO BE GOD", mbid: "mb-stg", conf: 0.78,
                recommend: EnrichmentStatus.NeedsReview, warnings: ["title_mismatch"]));
        Add(song, EnrichmentProvider.MusicBrainzWeb, ProviderAttemptStatus.Matched,
            Result("Travis Scott", "STOP TRYING TO BE GOD", mbid: "mb-stg", isrc: "USSM11806662", conf: 0.9,
                recommend: EnrichmentStatus.NeedsReview, warnings: ["title_mismatch", "album_mismatch"]));
        Add(song, EnrichmentProvider.AppleMusic, ProviderAttemptStatus.Matched,
            FullResult("AppleMusic", "Travis Scott", "90210", album: "Rodeo", year: 2015, conf: 1.0));

        var r = ConsensusEvaluator.Evaluate(
            song,
            Enabled(EnrichmentProvider.AcoustID, EnrichmentProvider.MusicBrainzWeb, EnrichmentProvider.AppleMusic),
            Opts);

        Assert.Equal(EnrichmentStatus.NeedsReview, r.Status);
    }

    // --- helpers ---

    [Fact]
    public void Cluster_WinnerWithoutDiscreteArtists_BorrowsThemFromAgreeingDonor()
    {
        // The identity winner (here Deezer, highest-confidence name-based) carries only the combined
        // display credit; the agreeing MusicBrainz member has the discrete list + aligned ids. The
        // final winner must carry both — otherwise the tag writer can't emit per-artist ARTISTS.
        var song = Song();
        Add(song, EnrichmentProvider.Deezer, ProviderAttemptStatus.Matched,
            Result("Alice & Bob", "Duet", isrc: "ISRC1", conf: 0.95, recommend: EnrichmentStatus.Matched));
        Add(song, EnrichmentProvider.MusicBrainzWeb, ProviderAttemptStatus.Matched,
            Result("Alice & Bob", "Duet", mbid: "mb-1", isrc: "ISRC1", conf: 0.8, recommend: EnrichmentStatus.Matched)
                with { Artists = "Alice; Bob", ArtistMusicBrainzIds = "mbid-a; mbid-b" });

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.Deezer, EnrichmentProvider.MusicBrainzWeb), Opts);

        Assert.Equal(EnrichmentStatus.Matched, r.Status);
        Assert.Equal("Alice; Bob", r.Winner!.Artists);
        Assert.Equal("mbid-a; mbid-b", r.Winner.ArtistMusicBrainzIds);
    }

    [Fact]
    public void Cluster_DiscreteArtistDonor_PrefersMusicBrainzOverSpotify()
    {
        var song = Song();
        Add(song, EnrichmentProvider.Deezer, ProviderAttemptStatus.Matched,
            Result("Alice & Bob", "Duet", isrc: "ISRC1", conf: 0.95, recommend: EnrichmentStatus.Matched));
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched,
            Result("Alice & Bob", "Duet", spotifyId: "spot-1", isrc: "ISRC1", conf: 0.9, recommend: EnrichmentStatus.Matched)
                with { Artists = "Alice; Bob (spotify spelling)" });
        Add(song, EnrichmentProvider.MusicBrainzWeb, ProviderAttemptStatus.Matched,
            Result("Alice & Bob", "Duet", mbid: "mb-1", isrc: "ISRC1", conf: 0.7, recommend: EnrichmentStatus.Matched)
                with { Artists = "Alice; Bob", ArtistMusicBrainzIds = "mbid-a; mbid-b" });

        var r = ConsensusEvaluator.Evaluate(
            song,
            Enabled(EnrichmentProvider.Deezer, EnrichmentProvider.SpotifyAPI, EnrichmentProvider.MusicBrainzWeb),
            Opts);

        Assert.Equal(EnrichmentStatus.Matched, r.Status);
        Assert.Equal("Alice; Bob", r.Winner!.Artists);
        Assert.Equal("mbid-a; mbid-b", r.Winner.ArtistMusicBrainzIds);
    }

    [Fact]
    public void Cluster_WinnerWithOwnArtists_OnlyBorrowsIdsFromMatchingList()
    {
        // The winner already names its artists; a donor whose list differs (count/order) must not
        // donate ids — Artists and ArtistMusicBrainzIds are positionally aligned and never mixed
        // across sources.
        var song = Song();
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched,
            Result("Alice & Bob", "Duet", spotifyId: "spot-1", isrc: "ISRC1", conf: 0.95, recommend: EnrichmentStatus.Matched)
                with { Artists = "Alice; Bob" });
        Add(song, EnrichmentProvider.MusicBrainzWeb, ProviderAttemptStatus.Matched,
            Result("Alice & Bob", "Duet", mbid: "mb-1", isrc: "ISRC1", conf: 0.8, recommend: EnrichmentStatus.Matched)
                with { Artists = "Alice", ArtistMusicBrainzIds = "mbid-a" });

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.SpotifyAPI, EnrichmentProvider.MusicBrainzWeb), Opts);

        Assert.Equal(EnrichmentStatus.Matched, r.Status);
        Assert.Equal("Alice; Bob", r.Winner!.Artists);
        Assert.Null(r.Winner.ArtistMusicBrainzIds);
    }

    [Fact]
    public void SoloNameBased_PathDerivedIdentityUnverified_NeedsReview()
    {
        // A lone name-based provider that matched against a path guess (identity_unverified) must NOT
        // auto-match — it needs a second provider to corroborate.
        var song = Song();
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched,
            Result("Amy Macdonald", "This Is the Life", spotifyId: "spot-1", conf: 0.95,
                recommend: EnrichmentStatus.Matched, warnings: [MatchWarnings.IdentityUnverified]));

        var r = ConsensusEvaluator.Evaluate(song, Enabled(EnrichmentProvider.SpotifyAPI), Opts);

        Assert.Equal(EnrichmentStatus.NeedsReview, r.Status);
    }

    [Fact]
    public void PathDerivedNameBased_CorroboratedByAcoustId_Matched()
    {
        // The Amy Macdonald case: AcoustID's clean fingerprint corroborates the path-derived Spotify
        // match on the same identity → the cluster (identity_unverified is non-blocking) promotes.
        var song = Song();
        Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
            Result("Amy Macdonald", "This Is the Life", mbid: "mb-1", conf: 0.98, recommend: EnrichmentStatus.Matched));
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched,
            Result("Amy Macdonald", "This Is the Life", spotifyId: "spot-1", conf: 0.95,
                recommend: EnrichmentStatus.Matched, warnings: [MatchWarnings.IdentityUnverified]));

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.AcoustID, EnrichmentProvider.SpotifyAPI), Opts);

        Assert.Equal(EnrichmentStatus.Matched, r.Status);
        Assert.Equal(2, r.AgreeingProviders.Count);
    }

    [Fact]
    public void WrongFingerprint_PlusCorrectPathDerivedNameBased_NoCluster_NeedsReview()
    {
        // The Birdy case: AcoustID matched the WRONG recording (a fingerprint collision), while the
        // path-derived Spotify found the correct one. They don't agree, so there's no corroboration —
        // the lone path-derived match can't stand, and the wrong fingerprint is contradicted → review.
        var song = Song();
        Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
            Result("Lionel Richie", "Penny Lover", mbid: "mb-wrong", conf: 0.98, recommend: EnrichmentStatus.Matched));
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched,
            Result("Birdy", "People Help the People", spotifyId: "spot-1", conf: 0.9,
                recommend: EnrichmentStatus.Matched, warnings: [MatchWarnings.IdentityUnverified]));

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.AcoustID, EnrichmentProvider.SpotifyAPI), Opts);

        Assert.Equal(EnrichmentStatus.NeedsReview, r.Status);
    }

    [Fact]
    public void DownloadOrigin_DurationMismatchOnly_IsrcConsensus_Matched()
    {
        // Wishlist / Spotify-Like download: the YouTube rip is longer than the canonical master, so
        // every provider flags duration_mismatch. Spotify and Deezer independently carry the same ISRC,
        // so the identity is corroborated — with the download relaxation on, the lone duration delta is
        // advisory and the song auto-matches instead of piling up in review.
        var song = Song();
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched,
            Result("Adele", "Easy On Me", isrc: "GBBKS2100270", spotifyId: "sp1", conf: 0.75,
                recommend: EnrichmentStatus.NeedsReview, warnings: ["duration_mismatch"]));
        Add(song, EnrichmentProvider.Deezer, ProviderAttemptStatus.Matched,
            Result("Adele", "Easy On Me", isrc: "GBBKS2100270", conf: 0.75,
                recommend: EnrichmentStatus.NeedsReview, warnings: ["duration_mismatch"]));

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.SpotifyAPI, EnrichmentProvider.Deezer), Opts,
            relaxDownloadDurationMismatch: true);

        Assert.Equal(EnrichmentStatus.Matched, r.Status);
    }

    [Fact]
    public void DownloadOrigin_DurationMismatchOnly_AcoustIdFingerprint_Matched()
    {
        // No shared ISRC, but AcoustID fingerprinted the file's own audio to the same recording the
        // name-based provider matched (they cluster via the shared MBID) — so the duration delta is
        // padding on the YouTube rip, not a wrong match.
        var song = Song();
        Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
            Result("Akon", "Lonely", mbid: "mb-lonely", conf: 0.68, recommend: EnrichmentStatus.NeedsReview));
        Add(song, EnrichmentProvider.Deezer, ProviderAttemptStatus.Matched,
            Result("Akon", "Lonely", mbid: "mb-lonely", isrc: "USUR10400234", conf: 0.75,
                recommend: EnrichmentStatus.NeedsReview, warnings: ["duration_mismatch"]));

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.AcoustID, EnrichmentProvider.Deezer), Opts,
            relaxDownloadDurationMismatch: true);

        Assert.Equal(EnrichmentStatus.Matched, r.Status);
    }

    [Fact]
    public void DownloadOrigin_DurationMismatch_RelaxationOff_NeedsReview()
    {
        // Same corroborated download, but the relaxation flag is off (the source-library default):
        // duration_mismatch stays blocking, so the song goes to review.
        var song = Song();
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched,
            Result("Adele", "Easy On Me", isrc: "GBBKS2100270", spotifyId: "sp1", conf: 0.75,
                recommend: EnrichmentStatus.NeedsReview, warnings: ["duration_mismatch"]));
        Add(song, EnrichmentProvider.Deezer, ProviderAttemptStatus.Matched,
            Result("Adele", "Easy On Me", isrc: "GBBKS2100270", conf: 0.75,
                recommend: EnrichmentStatus.NeedsReview, warnings: ["duration_mismatch"]));

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.SpotifyAPI, EnrichmentProvider.Deezer), Opts,
            relaxDownloadDurationMismatch: false);

        Assert.Equal(EnrichmentStatus.NeedsReview, r.Status);
    }

    [Fact]
    public void DownloadOrigin_DurationMismatch_NoStrongCorroboration_NeedsReview()
    {
        // Two name-based providers agree only by name+duration (no shared ISRC, no AcoustID). Name
        // agreement isn't strong enough to dismiss the duration gap — the relaxation requires an ISRC
        // consensus or a fingerprint, so this still goes to review even with the flag on.
        var song = Song();
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched,
            Result("Adele", "Easy On Me", spotifyId: "sp1", conf: 0.75, durationSeconds: 224,
                recommend: EnrichmentStatus.NeedsReview, warnings: ["duration_mismatch"]));
        Add(song, EnrichmentProvider.Deezer, ProviderAttemptStatus.Matched,
            Result("Adele", "Easy On Me", conf: 0.75, durationSeconds: 224,
                recommend: EnrichmentStatus.NeedsReview, warnings: ["duration_mismatch"]));

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.SpotifyAPI, EnrichmentProvider.Deezer), Opts,
            relaxDownloadDurationMismatch: true);

        Assert.Equal(EnrichmentStatus.NeedsReview, r.Status);
    }

    [Fact]
    public void DownloadOrigin_DurationPlusOtherBlockingWarning_NeedsReview()
    {
        // The relaxation only forgives a *lone* duration_mismatch. A co-occurring artist_mismatch is a
        // real identity contradiction, so the song still goes to review despite ISRC corroboration.
        var song = Song();
        Add(song, EnrichmentProvider.SpotifyAPI, ProviderAttemptStatus.Matched,
            Result("Adele", "Easy On Me", isrc: "GBBKS2100270", spotifyId: "sp1", conf: 0.75,
                recommend: EnrichmentStatus.NeedsReview, warnings: ["duration_mismatch", "artist_mismatch"]));
        Add(song, EnrichmentProvider.Deezer, ProviderAttemptStatus.Matched,
            Result("Adele", "Easy On Me", isrc: "GBBKS2100270", conf: 0.75,
                recommend: EnrichmentStatus.NeedsReview, warnings: ["duration_mismatch", "artist_mismatch"]));

        var r = ConsensusEvaluator.Evaluate(
            song, Enabled(EnrichmentProvider.SpotifyAPI, EnrichmentProvider.Deezer), Opts,
            relaxDownloadDurationMismatch: true);

        Assert.Equal(EnrichmentStatus.NeedsReview, r.Status);
    }

    private static SongMetadata Song() => new()
    {
        OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = "/x.mp3",
        FileName = "x.mp3",
        Extension = ".mp3",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        EnrichmentStatus = EnrichmentStatus.Pending,
    };

    private static IReadOnlySet<EnrichmentProvider> Enabled(params EnrichmentProvider[] p) => new HashSet<EnrichmentProvider>(p);

    private static EnrichmentProviderResult Result(
        string artist, string title,
        string? mbid = null, string? spotifyId = null, string? isrc = null,
        double conf = 0.8, EnrichmentStatus recommend = EnrichmentStatus.NeedsReview,
        List<string>? warnings = null, int? durationSeconds = null)
        => new(artist, artist, title, null, null, mbid, null, spotifyId, null, isrc,
            spotifyId is not null ? "SpotifyAPI" : "AcoustID", conf, warnings ?? [], recommend,
            DurationSeconds: durationSeconds);

    private static EnrichmentProviderResult FullResult(
        string matchedBy, string artist, string title,
        string? isrc = null, string? spotifyId = null,
        string? album = null, int? year = null, int? track = null, double conf = 0.99)
        => new(artist, artist, title, year, track, null, null, spotifyId, null, isrc,
            matchedBy, conf, [], EnrichmentStatus.Matched, Album: album);

    private static void Add(
        SongMetadata song, EnrichmentProvider provider, ProviderAttemptStatus status, EnrichmentProviderResult? candidate)
    {
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = provider,
            Status = status,
            AttemptedAtUtc = DateTime.UtcNow,
            MatchedDataJson = candidate is null ? null : JsonSerializer.Serialize(candidate),
        });
    }

    private static void AddRateLimited(SongMetadata song, EnrichmentProvider provider, DateTime? since)
    {
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = provider,
            Status = ProviderAttemptStatus.RateLimited,
            AttemptedAtUtc = DateTime.UtcNow,
            RateLimitedSinceUtc = since,
        });
    }
}
