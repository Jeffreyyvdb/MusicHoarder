using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.AppleMusic;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Enrichment.Providers;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

public class AppleMusicEnrichmentProviderTests
{
    [Fact]
    public async Task TryEnrichAsync_StrongMatch_ReturnsMatched()
    {
        var track = new AppleMusicCatalogTrack("12345", "Lucid Dreams", "Juice WRLD", "Goodbye & Good Riddance", 2018, 6, 239_000, null);
        var catalog = new StubAppleCatalog { OnSearch = _ => [track] };
        var provider = CreateProvider(catalog);
        var song = Song(artist: "Juice WRLD", title: "Lucid Dreams", durationSec: 239);

        var result = await provider.TryEnrichAsync(song);

        var matched = Assert.IsType<ProviderMatched>(result);
        Assert.Equal(EnrichmentStatus.Matched, matched.Result.RecommendedStatus);
        Assert.Equal("AppleMusic", matched.Result.MatchedBy);
        Assert.Equal("Goodbye & Good Riddance", matched.Result.Album);
        Assert.Equal(2018, matched.Result.Year);
        Assert.Equal(6, matched.Result.TrackNumber);
        Assert.Null(matched.Result.Isrc);
        Assert.Equal(1, catalog.SearchCalls);
    }

    [Fact]
    public async Task TryEnrichAsync_DurationMismatch_BlocksMatched_GoesNeedsReview()
    {
        var track = new AppleMusicCatalogTrack("12345", "Lucid Dreams", "Juice WRLD", "Album", 2018, 1, 60_000, null);
        var catalog = new StubAppleCatalog { OnSearch = _ => [track] };
        var provider = CreateProvider(catalog);
        var song = Song(artist: "Juice WRLD", title: "Lucid Dreams", durationSec: 239);

        var result = await provider.TryEnrichAsync(song);

        var matched = Assert.IsType<ProviderMatched>(result);
        Assert.Equal(EnrichmentStatus.NeedsReview, matched.Result.RecommendedStatus);
        Assert.Contains("duration_mismatch", matched.Result.MatchWarnings);
    }

    [Fact]
    public async Task TryEnrichAsync_SymbolOnlyArtist_WrongCandidate_NotCleanMatch()
    {
        var track = new AppleMusicCatalogTrack("wrong-id", "FIELD TRIP", "RAREKID", "FIELD TRIP", 2024, 1, 318_000, null);
        var catalog = new StubAppleCatalog { OnSearch = _ => [track] };
        var provider = CreateProvider(catalog);
        var song = Song(artist: "¥$", title: "Field Trip", durationSec: 318);

        var result = await provider.TryEnrichAsync(song);

        var candidate = result switch
        {
            ProviderMatched m => m.Result,
            ProviderNoMatch nm => nm.BestCandidate,
            _ => null,
        };
        Assert.NotNull(candidate);
        Assert.NotEqual(EnrichmentStatus.Matched, candidate!.RecommendedStatus);
        Assert.Contains("artist_mismatch", candidate.MatchWarnings);
    }

    [Fact]
    public async Task TryEnrichAsync_UntaggedFile_DerivesQueryFromPath_Matches()
    {
        var track = new AppleMusicCatalogTrack("12345", "Lucid Dreams", "Juice WRLD", "Goodbye & Good Riddance", 2018, 5, 239_000, null);
        string? seenQuery = null;
        var catalog = new StubAppleCatalog { OnSearch = q => { seenQuery = q; return [track]; } };
        var provider = CreateProvider(catalog);
        var song = Song(artist: null, title: null, durationSec: 239,
            sourcePath: "/s/Juice WRLD/Goodbye & Good Riddance/05 Lucid Dreams.mp3",
            fileName: "05 Lucid Dreams.mp3");

        var result = await provider.TryEnrichAsync(song);

        Assert.IsType<ProviderMatched>(result);
        Assert.Equal(1, catalog.SearchCalls);
        Assert.NotNull(seenQuery);
        Assert.Contains("Lucid Dreams", seenQuery);
        Assert.DoesNotContain("05", seenQuery);
    }

    [Fact]
    public async Task TryEnrichAsync_AlbumNarrowedSearchEmpty_FallsBackToArtistTitleQuery()
    {
        // A tagged file with a known album first searches narrowed by that album; when that returns
        // nothing the provider must retry with the un-narrowed artist+title query so recall never
        // drops. Pins the two-query fallback the shared planner has to preserve.
        var track = new AppleMusicCatalogTrack("12345", "Lucid Dreams", "Juice WRLD", "Goodbye & Good Riddance", 2018, 6, 239_000, null);
        var queries = new List<string>();
        var catalog = new StubAppleCatalog
        {
            OnSearch = q =>
            {
                queries.Add(q);
                return q.Contains("Goodbye", StringComparison.OrdinalIgnoreCase) ? [] : [track];
            },
        };
        var provider = CreateProvider(catalog);
        var song = Song(artist: "Juice WRLD", title: "Lucid Dreams", durationSec: 239, album: "Goodbye & Good Riddance");

        var result = await provider.TryEnrichAsync(song);

        Assert.IsType<ProviderMatched>(result);
        Assert.Equal(2, catalog.SearchCalls);
        Assert.Equal(2, queries.Count);
        Assert.Contains("Goodbye", queries[0], StringComparison.OrdinalIgnoreCase);       // album-narrowed first
        Assert.DoesNotContain("Goodbye", queries[1], StringComparison.OrdinalIgnoreCase); // un-narrowed fallback
    }

    [Fact]
    public async Task TryEnrichAsync_RateLimited_ReturnsProviderRateLimited()
    {
        var catalog = new StubAppleCatalog
        {
            OnSearch = _ => throw new ProviderRateLimitedException(TimeSpan.FromSeconds(30)),
        };
        var provider = CreateProvider(catalog);
        var song = Song(artist: "Juice WRLD", title: "Lucid Dreams", durationSec: 239);

        var result = await provider.TryEnrichAsync(song);

        var rateLimited = Assert.IsType<ProviderRateLimited>(result);
        Assert.Equal(TimeSpan.FromSeconds(30), rateLimited.RetryAfter);
    }

    private static AppleMusicEnrichmentProvider CreateProvider(
        IAppleMusicCatalogService catalog, IOptions<MusicEnricherOptions>? opts = null)
    {
        opts ??= Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions { SourceDirectory = "/s", DestinationDirectory = "/d" });
        return new AppleMusicEnrichmentProvider(catalog, opts, NullLogger<AppleMusicEnrichmentProvider>.Instance);
    }

    private static SongMetadata Song(
        string? artist, string? title, int durationSec,
        string sourcePath = "/s/a.mp3", string fileName = "a.mp3", string? album = null) => new()
    {
        OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
        SourcePath = sourcePath,
        FileName = fileName,
        Extension = ".mp3",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = artist,
        Title = title,
        Album = album,
        DurationSeconds = durationSec,
        EnrichmentStatus = EnrichmentStatus.Pending,
    };

    private sealed class StubAppleCatalog : IAppleMusicCatalogService
    {
        public Func<string, IReadOnlyList<AppleMusicCatalogTrack>>? OnSearch { get; init; }
        public int SearchCalls { get; private set; }

        public Task<IReadOnlyList<AppleMusicCatalogTrack>> SearchTracksAsync(string query, CancellationToken ct = default)
        {
            SearchCalls++;
            return Task.FromResult(OnSearch?.Invoke(query) ?? []);
        }

        public Task<string?> SearchAlbumIdAsync(string artist, string album, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<AppleAlbumDetail?> GetAlbumAsync(string collectionId, CancellationToken ct = default)
            => Task.FromResult<AppleAlbumDetail?>(null);
    }
}
