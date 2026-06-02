using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Enrichment.Providers;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

public class DeezerEnrichmentProviderTests
{
    [Fact]
    public async Task TryEnrichAsync_StrongMatch_HydratesAndReturnsMatched()
    {
        // Search returns a lightweight track (no ISRC/year/track#); the provider hydrates the
        // chosen candidate via LookupByIdAsync to fill those in (and seed the gossip ISRC).
        var lightweight = new DeezerCatalogTrack("deezer-1", "Lucid Dreams", "Juice WRLD", "Goodbye & Good Riddance", null, null, 239_000, null);
        var hydrated = new DeezerCatalogTrack("deezer-1", "Lucid Dreams", "Juice WRLD", "Goodbye & Good Riddance", 2018, 3, 239_000, "USUM71807840");
        var catalog = new StubDeezerCatalog
        {
            OnSearch = _ => [lightweight],
            OnLookupId = _ => hydrated,
        };
        var provider = CreateProvider(catalog);
        var song = Song(artist: "Juice WRLD", title: "Lucid Dreams", durationSec: 239);

        var result = await provider.TryEnrichAsync(song);

        var matched = Assert.IsType<ProviderMatched>(result);
        Assert.Equal(EnrichmentStatus.Matched, matched.Result.RecommendedStatus);
        Assert.Equal("Deezer", matched.Result.MatchedBy);
        Assert.Equal("Goodbye & Good Riddance", matched.Result.Album);
        Assert.Equal(2018, matched.Result.Year);
        Assert.Equal(3, matched.Result.TrackNumber);
        Assert.Equal("USUM71807840", matched.Result.Isrc);
        Assert.Equal(1, catalog.SearchCalls);
        Assert.Equal(1, catalog.LookupIdCalls);
    }

    [Fact]
    public async Task TryEnrichAsync_DurationMismatch_BlocksMatched_GoesNeedsReview()
    {
        var track = new DeezerCatalogTrack("deezer-1", "Lucid Dreams", "Juice WRLD", "Album", 2018, 1, 60_000, "USUM71807840");
        var catalog = new StubDeezerCatalog
        {
            OnSearch = _ => [track],
            OnLookupId = _ => track,
        };
        var provider = CreateProvider(catalog);
        var song = Song(artist: "Juice WRLD", title: "Lucid Dreams", durationSec: 239);

        var result = await provider.TryEnrichAsync(song);

        var matched = Assert.IsType<ProviderMatched>(result);
        Assert.Equal(EnrichmentStatus.NeedsReview, matched.Result.RecommendedStatus);
        Assert.Contains("duration_mismatch", matched.Result.MatchWarnings);
    }

    [Fact]
    public async Task TryEnrichAsync_IsrcFirst_ShortCircuitsSearch()
    {
        var isrcTrack = new DeezerCatalogTrack("deezer-1", "Lucid Dreams", "Juice WRLD", "Goodbye & Good Riddance", 2018, 3, 239_000, "USUM71807840");
        var catalog = new StubDeezerCatalog
        {
            OnIsrc = _ => isrcTrack,
            OnSearch = _ => throw new InvalidOperationException("search should not be called when ISRC lookup hits"),
        };
        var provider = CreateProvider(catalog);
        var song = Song(artist: "Juice WRLD", title: "Lucid Dreams", durationSec: 239, isrc: "USUM71807840");

        var result = await provider.TryEnrichAsync(song);

        var matched = Assert.IsType<ProviderMatched>(result);
        Assert.Equal(EnrichmentStatus.Matched, matched.Result.RecommendedStatus);
        Assert.Equal("USUM71807840", matched.Result.Isrc);
        Assert.Equal(1, catalog.IsrcCalls);
        Assert.Equal(0, catalog.SearchCalls);
        Assert.Equal(0, catalog.LookupIdCalls);
    }

    [Fact]
    public async Task TryEnrichAsync_SymbolOnlyArtist_WrongCandidate_NotCleanMatch()
    {
        // "¥$" normalizes to empty; the raw fallback must keep an unrelated artist from scoring 100%.
        var track = new DeezerCatalogTrack("wrong-id", "FIELD TRIP", "RAREKID", "FIELD TRIP", 2024, 1, 318_000, "GXBDS2665817");
        var catalog = new StubDeezerCatalog
        {
            OnSearch = _ => [track],
            OnLookupId = _ => track,
        };
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
        var hydrated = new DeezerCatalogTrack("deezer-1", "Lucid Dreams", "Juice WRLD", "Goodbye & Good Riddance", 2018, 5, 239_000, "USUM71807840");
        string? seenQuery = null;
        var catalog = new StubDeezerCatalog
        {
            OnSearch = q => { seenQuery = q; return [hydrated]; },
            OnLookupId = _ => hydrated,
        };
        var provider = CreateProvider(catalog);
        var song = Song(artist: null, title: null, durationSec: 239,
            sourcePath: "/s/Juice WRLD/Goodbye & Good Riddance/05 Lucid Dreams.mp3",
            fileName: "05 Lucid Dreams.mp3");

        var result = await provider.TryEnrichAsync(song);

        var matched = Assert.IsType<ProviderMatched>(result);
        Assert.Equal(1, catalog.SearchCalls);
        Assert.NotNull(seenQuery);
        Assert.Contains("Lucid Dreams", seenQuery);
        Assert.DoesNotContain("05", seenQuery);
    }

    [Fact]
    public async Task TryEnrichAsync_AlbumBoost_PrefersOriginalAlbumOverCompilation()
    {
        // The same recording appears on the original album and a "Greatest Hits" compilation with
        // identical artist/title/duration. The album-agreement boost must select the candidate on the
        // file's own album rather than whichever the catalog ranked first.
        var comp = new DeezerCatalogTrack("d-comp", "Ready or Not", "Fugees", "Greatest Hits", 2000, 2, 200_000, "USSM19600051");
        var original = new DeezerCatalogTrack("d-orig", "Ready or Not", "Fugees", "The Score", 1996, 3, 200_000, "USSM19600051");
        var catalog = new StubDeezerCatalog
        {
            OnSearch = _ => [comp, original], // compilation first; the boost must flip the choice
        };
        var provider = CreateProvider(catalog);
        var song = Song(artist: "Fugees", title: "Ready or Not", durationSec: 200, album: "The Score (Expanded Edition)");

        var result = await provider.TryEnrichAsync(song);

        var matched = Assert.IsType<ProviderMatched>(result);
        Assert.Equal("The Score", matched.Result.Album);
        Assert.Equal(1996, matched.Result.Year);
        Assert.Equal(3, matched.Result.TrackNumber);
    }

    [Fact]
    public async Task TryEnrichAsync_RateLimited_ReturnsProviderRateLimited()
    {
        var catalog = new StubDeezerCatalog
        {
            OnSearch = _ => throw new ProviderRateLimitedException(TimeSpan.FromSeconds(30)),
        };
        var provider = CreateProvider(catalog);
        var song = Song(artist: "Juice WRLD", title: "Lucid Dreams", durationSec: 239);

        var result = await provider.TryEnrichAsync(song);

        var rateLimited = Assert.IsType<ProviderRateLimited>(result);
        Assert.Equal(TimeSpan.FromSeconds(30), rateLimited.RetryAfter);
    }

    private static DeezerEnrichmentProvider CreateProvider(
        IDeezerCatalogService catalog, IOptions<MusicEnricherOptions>? opts = null)
    {
        opts ??= Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions { SourceDirectory = "/s", DestinationDirectory = "/d" });
        return new DeezerEnrichmentProvider(catalog, opts, NullLogger<DeezerEnrichmentProvider>.Instance);
    }

    private static SongMetadata Song(
        string? artist, string? title, int durationSec, string? isrc = null,
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
        Isrc = isrc,
        EnrichmentStatus = EnrichmentStatus.Pending,
    };

    private sealed class StubDeezerCatalog : IDeezerCatalogService
    {
        public Func<string, DeezerCatalogTrack?>? OnIsrc { get; init; }
        public Func<string, IReadOnlyList<DeezerCatalogTrack>>? OnSearch { get; init; }
        public Func<string, DeezerCatalogTrack?>? OnLookupId { get; init; }

        public int IsrcCalls { get; private set; }
        public int SearchCalls { get; private set; }
        public int LookupIdCalls { get; private set; }

        public Task<DeezerCatalogTrack?> LookupByIsrcAsync(string isrc, CancellationToken ct = default)
        {
            IsrcCalls++;
            return Task.FromResult(OnIsrc?.Invoke(isrc));
        }

        public Task<IReadOnlyList<DeezerCatalogTrack>> SearchTracksAsync(string query, CancellationToken ct = default)
        {
            SearchCalls++;
            return Task.FromResult(OnSearch?.Invoke(query) ?? []);
        }

        public Task<DeezerCatalogTrack?> LookupByIdAsync(string id, CancellationToken ct = default)
        {
            LookupIdCalls++;
            return Task.FromResult(OnLookupId?.Invoke(id));
        }

        public Task<string?> SearchAlbumIdAsync(string artist, string album, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<DeezerAlbumDetail?> GetAlbumAsync(string albumId, CancellationToken ct = default)
            => Task.FromResult<DeezerAlbumDetail?>(null);
    }
}
