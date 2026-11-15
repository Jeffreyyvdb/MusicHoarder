using Microsoft.Extensions.Caching.Memory;
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

    // ── Album-first: one album lookup serves every track of the album ─────────────────

    private static readonly AppleAlbumDetail Goodbye = new(
        "1440", "Goodbye & Good Riddance", "Juice WRLD", 2018, null,
        [
            new AppleAlbumTrackItem(1, 5, "All Girls Are the Same", 165_000, "101"),
            new AppleAlbumTrackItem(1, 6, "Lucid Dreams", 239_000, "102"),
            new AppleAlbumTrackItem(1, 11, "Wasted (feat. Lil Uzi Vert)", 220_000, "103", "Juice WRLD"),
        ]);

    private static StubAppleCatalog GoodbyeCatalog(Func<string, IReadOnlyList<AppleMusicCatalogTrack>>? onSearch = null) => new()
    {
        OnAlbumSearch = (_, _) => [new AppleAlbumCandidate("1440", "Goodbye & Good Riddance", "Juice WRLD")],
        OnGetAlbum = id => id == "1440" ? Goodbye : null,
        OnSearch = onSearch,
    };

    [Fact]
    public async Task TryEnrichAsync_TrackOnItsAlbumsTracklist_MatchesWithoutATrackSearch()
    {
        var catalog = GoodbyeCatalog();
        var provider = CreateProvider(catalog);
        var song = Song(artist: "Juice WRLD", title: "Lucid Dreams", durationSec: 239, album: "Goodbye & Good Riddance");

        var result = await provider.TryEnrichAsync(song);

        var matched = Assert.IsType<ProviderMatched>(result);
        Assert.Equal(EnrichmentStatus.Matched, matched.Result.RecommendedStatus);
        Assert.Equal("Goodbye & Good Riddance", matched.Result.Album);
        Assert.Equal(2018, matched.Result.Year);
        Assert.Equal(6, matched.Result.TrackNumber);
        Assert.Equal(0, catalog.SearchCalls);
        Assert.Equal(1, catalog.AlbumSearchCalls);
        Assert.Equal(1, catalog.GetAlbumCalls);
    }

    [Fact]
    public async Task TryEnrichAsync_WholeAlbum_CostsTwoRequestsInAll()
    {
        // The point of the album lookup: an album's tracks used to cost a search (or two) each, at
        // iTunes' 4-second spacing. Now the album search and its tracklist serve them all.
        var catalog = GoodbyeCatalog();
        var provider = CreateProvider(catalog);
        var songs = new[]
        {
            Song("Juice WRLD", "All Girls Are the Same", 165, album: "Goodbye & Good Riddance"),
            Song("Juice WRLD", "Lucid Dreams", 239, album: "Goodbye & Good Riddance"),
            // A featured credit on the track still finds the album through the album artist.
            Song("Juice WRLD feat. Lil Uzi Vert", "Wasted", 220, album: "Goodbye & Good Riddance", albumArtist: "Juice WRLD"),
        };

        foreach (var song in songs)
            Assert.IsType<ProviderMatched>(await provider.TryEnrichAsync(song));

        Assert.Equal(0, catalog.SearchCalls);
        Assert.Equal(1, catalog.AlbumSearchCalls);
        Assert.Equal(1, catalog.GetAlbumCalls);
    }

    [Fact]
    public async Task TryEnrichAsync_TracksOfOneAlbumArrivingTogether_ShareOneAlbumLookup()
    {
        var gate = new TaskCompletionSource();
        var catalog = new StubAppleCatalog
        {
            AlbumSearchGate = gate.Task,
            OnAlbumSearch = (_, _) => [new AppleAlbumCandidate("1440", "Goodbye & Good Riddance", "Juice WRLD")],
            OnGetAlbum = _ => Goodbye,
        };
        var provider = CreateProvider(catalog);

        var calls = Enumerable.Range(0, 5)
            .Select(_ => provider.TryEnrichAsync(Song("Juice WRLD", "Lucid Dreams", 239, album: "Goodbye & Good Riddance")))
            .ToList();
        gate.SetResult();
        var results = await Task.WhenAll(calls);

        Assert.All(results, r => Assert.IsType<ProviderMatched>(r));
        Assert.Equal(1, catalog.AlbumSearchCalls);
        Assert.Equal(1, catalog.GetAlbumCalls);
    }

    [Fact]
    public async Task TryEnrichAsync_AlbumSearchAnswersWithAnotherAlbum_SearchesTheTrackAndRemembersTheMiss()
    {
        // An unreleased album: iTunes answers the album search with something else entirely. That
        // must not be scored as the album, and the miss is remembered so the album's other tracks
        // don't search for it again.
        var track = new AppleMusicCatalogTrack("9", "Father Stretch My Hands, Pt. 1", "Kanye West", "The Life of Pablo", 2016, 2, 135_000, null);
        var catalog = new StubAppleCatalog
        {
            OnAlbumSearch = (_, _) => [new AppleAlbumCandidate("1065946325", "Baby Lullaby: Relaxing Piano Lullabies", "Baby Lullaby Academy")],
            OnGetAlbum = _ => throw new InvalidOperationException("an unverified album must not be fetched"),
            OnSearch = _ => [track],
        };
        var provider = CreateProvider(catalog);

        await provider.TryEnrichAsync(Song("Kanye West", "Father Stretch My Hands, Pt. 1", 135, album: "SWISH"));
        await provider.TryEnrichAsync(Song("Kanye West", "Real Friends", 250, album: "SWISH"));

        Assert.Equal(1, catalog.AlbumSearchCalls);
        Assert.Equal(0, catalog.GetAlbumCalls);
        Assert.True(catalog.SearchCalls >= 2);
    }

    [Fact]
    public async Task TryEnrichAsync_AlbumBySomeoneElse_IsNotTheAlbum()
    {
        // Same title, different artist: Jaymes Young's "What Is Love?" is not Juice WRLD's.
        var catalog = new StubAppleCatalog
        {
            OnAlbumSearch = (_, _) => [new AppleAlbumCandidate("7", "What Is Love?", "Jaymes Young")],
            OnGetAlbum = _ => throw new InvalidOperationException("an unverified album must not be fetched"),
        };
        var provider = CreateProvider(catalog);

        var result = await provider.TryEnrichAsync(Song("Juice WRLD", "What Is Love?", 200, album: "What Is Love?"));

        Assert.IsType<ProviderNoMatch>(result);
        Assert.Equal(0, catalog.GetAlbumCalls);
    }

    [Fact]
    public async Task TryEnrichAsync_TrackTheAlbumDoesNotHold_FallsBackToTheTrackSearch()
    {
        var single = new AppleMusicCatalogTrack("55", "Righteous", "Juice WRLD", "Righteous - Single", 2020, 1, 241_000, null);
        var catalog = GoodbyeCatalog(onSearch: _ => [single]);
        var provider = CreateProvider(catalog);

        var result = await provider.TryEnrichAsync(Song("Juice WRLD", "Righteous", 241, album: "Goodbye & Good Riddance"));

        var matched = Assert.IsType<ProviderMatched>(result);
        Assert.Equal("Righteous - Single", matched.Result.Album);
        Assert.True(catalog.SearchCalls >= 1);
    }

    [Fact]
    public async Task TryEnrichAsync_AlbumTrackNotClean_SearchCanStillWin()
    {
        // The album's cut is far shorter than the file (a radio edit on this edition, say): not a
        // clean match, so the track is searched too, and the better candidate wins.
        var shortCut = new AppleAlbumDetail("1440", "Goodbye & Good Riddance", "Juice WRLD", 2018, null,
            [new AppleAlbumTrackItem(1, 6, "Lucid Dreams", 120_000, "102")]);
        var full = new AppleMusicCatalogTrack("12345", "Lucid Dreams", "Juice WRLD", "Goodbye & Good Riddance", 2018, 6, 239_000, null);
        var catalog = new StubAppleCatalog
        {
            OnAlbumSearch = (_, _) => [new AppleAlbumCandidate("1440", "Goodbye & Good Riddance", "Juice WRLD")],
            OnGetAlbum = _ => shortCut,
            OnSearch = _ => [full],
        };
        var provider = CreateProvider(catalog);

        var result = await provider.TryEnrichAsync(Song("Juice WRLD", "Lucid Dreams", 239, album: "Goodbye & Good Riddance"));

        var matched = Assert.IsType<ProviderMatched>(result);
        Assert.Equal(EnrichmentStatus.Matched, matched.Result.RecommendedStatus);
        Assert.True(catalog.SearchCalls >= 1);
    }

    [Fact]
    public async Task TryEnrichAsync_AlbumLookupRateLimited_ReturnsRateLimitedAndRemembersNothing()
    {
        var limited = true;
        var catalog = new StubAppleCatalog
        {
            OnGetAlbum = _ => Goodbye,
        };
        catalog.OnAlbumSearch = (_, _) => limited
            ? throw new ProviderRateLimitedException(TimeSpan.FromSeconds(30))
            : [new AppleAlbumCandidate("1440", "Goodbye & Good Riddance", "Juice WRLD")];
        var provider = CreateProvider(catalog);
        var song = Song("Juice WRLD", "Lucid Dreams", 239, album: "Goodbye & Good Riddance");

        var first = await provider.TryEnrichAsync(song);
        limited = false;
        var second = await provider.TryEnrichAsync(song);

        Assert.Equal(TimeSpan.FromSeconds(30), Assert.IsType<ProviderRateLimited>(first).RetryAfter);
        Assert.IsType<ProviderMatched>(second);
        Assert.Equal(2, catalog.AlbumSearchCalls);
        Assert.Equal(0, catalog.SearchCalls);
    }

    private static AppleMusicEnrichmentProvider CreateProvider(
        IAppleMusicCatalogService catalog, IOptions<MusicEnricherOptions>? opts = null)
    {
        opts ??= Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions { SourceDirectory = "/s", DestinationDirectory = "/d" });
        var albumIndex = new AppleMusicAlbumIndex(
            catalog, new MemoryCache(new MemoryCacheOptions()), opts, NullLogger<AppleMusicAlbumIndex>.Instance);
        return new AppleMusicEnrichmentProvider(catalog, albumIndex, opts, NullLogger<AppleMusicEnrichmentProvider>.Instance);
    }

    private static SongMetadata Song(
        string? artist, string? title, int durationSec,
        string sourcePath = "/s/a.mp3", string fileName = "a.mp3", string? album = null,
        string? albumArtist = null) => new()
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
        AlbumArtist = albumArtist,
        DurationSeconds = durationSec,
        EnrichmentStatus = EnrichmentStatus.Pending,
    };

    private sealed class StubAppleCatalog : IAppleMusicCatalogService
    {
        public Func<string, IReadOnlyList<AppleMusicCatalogTrack>>? OnSearch { get; init; }
        public Func<string, string, IReadOnlyList<AppleAlbumCandidate>>? OnAlbumSearch { get; set; }
        public Func<string, AppleAlbumDetail?>? OnGetAlbum { get; init; }
        /// <summary>When set, album searches wait for it, so a test can pile callers up behind one.</summary>
        public Task? AlbumSearchGate { get; init; }
        public int SearchCalls { get; private set; }
        private int _albumSearchCalls;
        public int AlbumSearchCalls => _albumSearchCalls;
        public int GetAlbumCalls { get; private set; }

        public Task<IReadOnlyList<AppleMusicCatalogTrack>> SearchTracksAsync(string query, CancellationToken ct = default)
        {
            SearchCalls++;
            return Task.FromResult(OnSearch?.Invoke(query) ?? []);
        }

        public Task<string?> SearchAlbumIdAsync(string artist, string album, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public async Task<IReadOnlyList<AppleAlbumCandidate>> SearchAlbumCandidatesAsync(string artist, string album, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _albumSearchCalls);
            if (AlbumSearchGate is not null)
                await AlbumSearchGate;
            return OnAlbumSearch?.Invoke(artist, album) ?? [];
        }

        public Task<AppleAlbumDetail?> GetAlbumAsync(string collectionId, CancellationToken ct = default)
        {
            GetAlbumCalls++;
            return Task.FromResult(OnGetAlbum?.Invoke(collectionId));
        }
    }
}
