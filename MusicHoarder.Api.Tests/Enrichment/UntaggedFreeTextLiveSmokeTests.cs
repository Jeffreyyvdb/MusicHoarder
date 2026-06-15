using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Enrichment.Providers;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

/// <summary>
/// Opt-in smoke tests that hit the real MusicBrainz + Deezer search APIs (both keyless) to verify the
/// provenance-aware, free-text untagged-file matching end to end — the one thing the stubbed unit tests
/// can't prove: that the cleaned filename free-text query actually resolves the right track on a real
/// search engine. Inert unless <c>MH_ENRICH_LIVE</c> is set, so CI never depends on a third-party API.
/// Run locally with: <c>MH_ENRICH_LIVE=1 dotnet test --filter "FullyQualifiedName~UntaggedFreeTextLive"</c>
/// </summary>
public class UntaggedFreeTextLiveSmokeTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("MH_ENRICH_LIVE") is not null;

    private static readonly MusicEnricherOptions Opts = new() { SourceDirectory = "/music", DestinationDirectory = "/dest" };

    [Fact]
    public async Task MusicBrainz_UntaggedBucketLayout_FreeTextResolvesRealTrack_NonBlocking()
    {
        if (!Enabled)
            return;

        var mb = CreateMusicBrainz();

        // The exact failing shape from production: empty tags, an "slskd/<letter>" bucket layout, the
        // real artist+title only present in the filename.
        var song = Untagged("/music/slskd/A/Amy Macdonald - This Is the Life.flac", durationSec: 185);

        var outcome = await mb.TryEnrichAsync(song);

        var candidate = CandidateOf(outcome);
        Assert.NotNull(candidate);
        Assert.Contains("Amy Macdonald", candidate!.Artist, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("This Is the Life", candidate.Title, StringComparison.OrdinalIgnoreCase);
        // The junk "slskd"/"A" path guess must NOT manufacture a blocking warning; the candidate is
        // flagged identity_unverified (non-blocking) instead.
        Assert.Contains(MatchWarnings.IdentityUnverified, candidate.MatchWarnings);
        Assert.DoesNotContain("artist_mismatch", candidate.MatchWarnings);
        Assert.False(MatchWarnings.AnyBlocking(candidate.MatchWarnings));
    }

    [Fact]
    public async Task TwoRealProviders_IndependentlyResolveUntaggedFile_NonBlocking()
    {
        if (!Enabled)
            return;

        var mb = CreateMusicBrainz();
        var deezer = CreateDeezer();

        var song = Untagged("/music/slskd/A/Amy Macdonald - This Is the Life.flac", durationSec: 185);

        await RunAndPersistAsync(song, mb, EnrichmentProvider.MusicBrainzWeb);
        await RunAndPersistAsync(song, deezer, EnrichmentProvider.Deezer);

        // The core guarantee of this change: two independent real providers, given only the junk-folder
        // bucket path, each resolve the CORRECT identity, and the path guess produces a non-blocking
        // identity_unverified (never a false artist_mismatch). Final auto-Matched then requires the
        // existing cross-provider corroboration — in production AcoustID supplies a shared MBID that
        // clusters regardless of catalog duration variance; here MB (196s) and Deezer (185s) disagree on
        // duration, which the identity matcher's ±8s tolerance treats as non-corroborating.
        foreach (var attempt in song.ProviderAttempts)
        {
            Assert.Equal(ProviderAttemptStatus.Matched, attempt.Status);
            var c = JsonSerializer.Deserialize<EnrichmentProviderResult>(attempt.MatchedDataJson!)!;
            Assert.Contains("Amy Macdonald", c.Artist, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("This Is the Life", c.Title, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(MatchWarnings.IdentityUnverified, c.MatchWarnings);
            Assert.False(MatchWarnings.AnyBlocking(c.MatchWarnings));
        }

        // And the consensus winner surfaced for review is the correct track (not a junk-folder artifact).
        var consensus = ConsensusEvaluator.Evaluate(
            song,
            new HashSet<EnrichmentProvider> { EnrichmentProvider.MusicBrainzWeb, EnrichmentProvider.Deezer },
            IdentityMatchOptions.Default);
        Assert.Contains("Amy Macdonald", consensus.Winner!.Artist, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SingleProvider_UntaggedFile_StaysNeedsReview_NoCorroboration()
    {
        if (!Enabled)
            return;

        var deezer = CreateDeezer();
        var song = Untagged("/music/slskd/B/Birdy - People Help the People.flac", durationSec: 255);

        await RunAndPersistAsync(song, deezer, EnrichmentProvider.Deezer);

        var consensus = ConsensusEvaluator.Evaluate(
            song,
            new HashSet<EnrichmentProvider> { EnrichmentProvider.Deezer },
            IdentityMatchOptions.Default);

        // A lone path-derived match needs corroboration; on its own it must surface for review.
        Assert.Equal(EnrichmentStatus.NeedsReview, consensus.Status);
        Assert.Contains("Birdy", consensus.Winner!.Artist, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task RunAndPersistAsync(SongMetadata song, IEnrichmentProvider provider, EnrichmentProvider providerEnum)
    {
        var outcome = await provider.TryEnrichAsync(song);
        var (status, candidate) = outcome switch
        {
            ProviderMatched m => (ProviderAttemptStatus.Matched, m.Result),
            ProviderNoMatch nm => (ProviderAttemptStatus.NoMatch, nm.BestCandidate),
            _ => (ProviderAttemptStatus.Failed, (EnrichmentProviderResult?)null),
        };

        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = providerEnum,
            Status = status,
            AttemptedAtUtc = DateTime.UtcNow,
            MatchedDataJson = candidate is null ? null : JsonSerializer.Serialize(candidate),
        });
    }

    private static EnrichmentProviderResult? CandidateOf(ProviderOutcome outcome) => outcome switch
    {
        ProviderMatched m => m.Result,
        ProviderNoMatch nm => nm.BestCandidate,
        _ => null,
    };

    private static MusicBrainzWebEnrichmentProvider CreateMusicBrainz()
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri("https://musicbrainz.org/ws/2/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.Add("User-Agent", Opts.MusicBrainzUserAgent);
        var svc = new MusicBrainzWebService(http, Microsoft.Extensions.Options.Options.Create(Opts), NullLogger<MusicBrainzWebService>.Instance);
        return new MusicBrainzWebEnrichmentProvider(svc, Microsoft.Extensions.Options.Options.Create(Opts), NullLogger<MusicBrainzWebEnrichmentProvider>.Instance);
    }

    private static DeezerEnrichmentProvider CreateDeezer()
    {
        var catalog = new DeezerCatalogService(
            new HttpClient { Timeout = TimeSpan.FromSeconds(60) },
            new MemoryCache(new MemoryCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(Opts),
            NullLogger<DeezerCatalogService>.Instance);
        return new DeezerEnrichmentProvider(catalog, Microsoft.Extensions.Options.Options.Create(Opts), NullLogger<DeezerEnrichmentProvider>.Instance);
    }

    private static SongMetadata Untagged(string sourcePath, int durationSec) => new()
    {
        OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = sourcePath,
        FileName = sourcePath.Split('/')[^1],
        Extension = ".flac",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = null,
        Title = null,
        Album = null,
        DurationSeconds = durationSec,
        EnrichmentStatus = EnrichmentStatus.Pending,
    };
}
