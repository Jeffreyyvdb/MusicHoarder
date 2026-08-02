using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

public class ReleaseClassifierTests
{
    [Theory]
    [InlineData("unreleased")]
    [InlineData("stems")]
    [InlineData("ssc")]
    [InlineData("misc")]
    [InlineData("recent")]
    [InlineData("best of")]
    public void TrackerCategory_OutsideTheReleasedSet_IsUnreleased(string category)
    {
        var result = ReleaseClassifier.Classify(
            isUnreleasedFlag: false, enrichmentStatus: EnrichmentStatus.Matched, matchedBy: "Tracker", matchWarnings: [$"category:{category}"],
            isrc: null, spotifyId: null);

        Assert.Equal(ReleaseClassification.Unreleased, result);
    }

    [Theory]
    [InlineData("released")]
    [InlineData("Released")]
    [InlineData("album copies")]
    public void TrackerCategory_InTheReleasedSet_IsReleased(string category)
    {
        var result = ReleaseClassifier.Classify(
            isUnreleasedFlag: false, enrichmentStatus: EnrichmentStatus.Matched, matchedBy: "YeTracker", matchWarnings: [$"category:{category}"],
            isrc: null, spotifyId: null);

        Assert.Equal(ReleaseClassification.Released, result);
    }

    [Fact]
    public void TrackerMatch_WithoutACategory_IsUnreleased()
    {
        var result = ReleaseClassifier.Classify(
            isUnreleasedFlag: false, enrichmentStatus: EnrichmentStatus.Matched, matchedBy: "YeTracker", matchWarnings: ["duration_mismatch"],
            isrc: null, spotifyId: null);

        Assert.Equal(ReleaseClassification.Unreleased, result);
    }

    [Fact]
    public void SyncedTrackerMatch_StillReadsAsATracker()
    {
        // SyncIngestService rewrites MatchedBy to "<provider>+sync", so equality checks would miss it.
        var result = ReleaseClassifier.Classify(
            isUnreleasedFlag: false, enrichmentStatus: EnrichmentStatus.Matched, matchedBy: "Tracker+sync", matchWarnings: ["category:unreleased"],
            isrc: null, spotifyId: null);

        Assert.Equal(ReleaseClassification.Unreleased, result);
    }

    [Fact]
    public void TrackerCategory_OutranksAnIncidentalIsrcOnTheSourceFile()
    {
        var result = ReleaseClassifier.Classify(
            isUnreleasedFlag: false, enrichmentStatus: EnrichmentStatus.Matched, matchedBy: "Tracker", matchWarnings: ["category:unreleased"],
            isrc: "USUM71703861", spotifyId: null);

        Assert.Equal(ReleaseClassification.Unreleased, result);
    }

    [Fact]
    public void StoredUnreleasedFlag_FromASyncPeer_Wins()
    {
        var result = ReleaseClassifier.Classify(
            isUnreleasedFlag: true, enrichmentStatus: EnrichmentStatus.Matched, matchedBy: "SpotifyAPI", matchWarnings: null,
            isrc: "USUM71703861", spotifyId: "6f2Y5W6t1E");

        Assert.Equal(ReleaseClassification.Unreleased, result);
    }

    [Theory]
    [InlineData("SpotifyAPI")]
    [InlineData("Deezer")]
    [InlineData("AppleMusic")]
    [InlineData("MusicBrainzWeb")]
    public void CommercialCatalogMatch_IsReleased(string provider)
    {
        var result = ReleaseClassifier.Classify(
            isUnreleasedFlag: false, enrichmentStatus: EnrichmentStatus.Matched, matchedBy: provider, matchWarnings: null,
            isrc: null, spotifyId: null);

        Assert.Equal(ReleaseClassification.Released, result);
    }

    [Fact]
    public void CommercialIdentifier_IsReleased_EvenWithoutAMatch()
    {
        var fromIsrc = ReleaseClassifier.Classify(
            isUnreleasedFlag: false, enrichmentStatus: EnrichmentStatus.Matched, matchedBy: null, matchWarnings: null,
            isrc: "USUM71703861", spotifyId: null);
        var fromSpotify = ReleaseClassifier.Classify(
            isUnreleasedFlag: false, enrichmentStatus: EnrichmentStatus.Matched, matchedBy: null, matchWarnings: null,
            isrc: null, spotifyId: "6f2Y5W6t1E");

        Assert.Equal(ReleaseClassification.Released, fromIsrc);
        Assert.Equal(ReleaseClassification.Released, fromSpotify);
    }

    [Fact]
    public void EveryProviderRanAndFoundNothing_IsLikelyUnreleased()
    {
        // ConsensusEvaluator's zero-candidate branch: all enabled providers reached a terminal
        // non-error status and not one produced a candidate, so MatchedBy was never set.
        var result = ReleaseClassifier.Classify(
            isUnreleasedFlag: false, enrichmentStatus: EnrichmentStatus.NeedsReview, matchedBy: null,
            matchWarnings: null, isrc: null, spotifyId: null);

        Assert.Equal(ReleaseClassification.LikelyUnreleased, result);
    }

    [Fact]
    public void ReviewWithABestCandidate_IsNotLikelyUnreleased()
    {
        // The big review bucket — mis-tagged downloads whose candidates carry blocking artist/title
        // mismatches. Providers DID find things; the row just couldn't clear the confidence bar.
        var result = ReleaseClassifier.Classify(
            isUnreleasedFlag: false, enrichmentStatus: EnrichmentStatus.NeedsReview, matchedBy: "AcoustID",
            matchWarnings: ["title_mismatch"], isrc: null, spotifyId: null);

        Assert.Equal(ReleaseClassification.Unknown, result);
    }

    [Theory]
    [InlineData(EnrichmentStatus.Pending)]
    [InlineData(EnrichmentStatus.Failed)]
    public void IncompleteOrErroredEnrichment_StaysUnknown(EnrichmentStatus status)
    {
        // Pending = a provider hasn't had its turn (e.g. no fingerprint, so AcoustID never ran).
        // Failed = a provider errored. Neither is evidence about the recording.
        var result = ReleaseClassifier.Classify(
            isUnreleasedFlag: false, enrichmentStatus: status, matchedBy: null,
            matchWarnings: null, isrc: null, spotifyId: null);

        Assert.Equal(ReleaseClassification.Unknown, result);
    }

    [Fact]
    public void NothingFound_ButTheFileCarriesAnIsrc_IsReleased()
    {
        // The lookup failed, but the file's own tags carry a distribution identifier — that outranks
        // the absence of a match.
        var result = ReleaseClassifier.Classify(
            isUnreleasedFlag: false, enrichmentStatus: EnrichmentStatus.NeedsReview, matchedBy: null,
            matchWarnings: null, isrc: "USUM71703861", spotifyId: null);

        Assert.Equal(ReleaseClassification.Released, result);
    }

    [Fact]
    public void NoEvidence_StaysUnknown()
    {
        // A fingerprint-only match: AcoustID's database carries leaks too, so guessing "released"
        // here would be wrong as often as it's right.
        Assert.Equal(
            ReleaseClassification.Unknown,
            ReleaseClassifier.Classify(false, EnrichmentStatus.Matched, "AcoustID", ["low_confidence"], null, null));
    }
}
