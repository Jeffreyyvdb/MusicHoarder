using System.Text.Json;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

public class ConsensusEvaluatorTests
{
    private static readonly IdentityMatchOptions Opts = IdentityMatchOptions.Default;

    [Fact]
    public void AcoustIdAlone_NeedsReview()
    {
        var song = Song();
        Add(song, EnrichmentProvider.AcoustID, ProviderAttemptStatus.Matched,
            Result("Artist", "Title", mbid: "mb-1", conf: 0.95, recommend: EnrichmentStatus.Matched));

        var r = ConsensusEvaluator.Evaluate(song, Enabled(EnrichmentProvider.AcoustID), Opts);

        Assert.Equal(EnrichmentStatus.NeedsReview, r.Status);
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

    // --- helpers ---

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
        double conf = 0.8, EnrichmentStatus recommend = EnrichmentStatus.NeedsReview)
        => new(artist, artist, title, null, null, mbid, null, spotifyId, null, isrc,
            spotifyId is not null ? "SpotifyAPI" : "AcoustID", conf, [], recommend);

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
}
