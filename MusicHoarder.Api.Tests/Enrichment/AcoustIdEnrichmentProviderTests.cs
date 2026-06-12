using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Enrichment.Providers;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

public class AcoustIdEnrichmentProviderTests
{
    [Fact]
    public async Task TryEnrich_PassesDiscreteArtistsAndIdsThroughToResult()
    {
        // The AcoustID recording carries the discrete artist list (with MusicBrainz artist ids);
        // it must reach the provider result so consensus/merge can persist it — flattening to the
        // "; "-joined display credit alone loses the per-artist values the tag writer needs.
        var match = new AcoustIdMatch(
            MusicBrainzRecordingId: "rec-1",
            AcoustIdTrackId: "acoust-1",
            Title: "née-nah",
            Artist: "21 Savage; Travis Scott; Metro Boomin",
            AlbumArtist: "21 Savage",
            Score: 0.97f,
            RecordingDurationMs: 220_000,
            Artists: "21 Savage; Travis Scott; Metro Boomin",
            ArtistMusicBrainzIds: "mbid-21; mbid-travis; mbid-metro");
        var provider = new AcoustIdEnrichmentProvider(
            new EnrichmentOrchestratorTests.StubAcoustIdService(_ => Task.FromResult<AcoustIdMatch?>(match)),
            new AcoustIdMatchValidator(),
            NullLogger<AcoustIdEnrichmentProvider>.Instance);

        var outcome = await provider.TryEnrichAsync(Song());

        var matched = Assert.IsType<ProviderMatched>(outcome);
        Assert.Equal("21 Savage; Travis Scott; Metro Boomin", matched.Result.Artists);
        Assert.Equal("mbid-21; mbid-travis; mbid-metro", matched.Result.ArtistMusicBrainzIds);
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
        Title = "née-nah",
        Artist = "21 Savage",
        Fingerprint = "fp",
        DurationSeconds = 220,
    };
}
