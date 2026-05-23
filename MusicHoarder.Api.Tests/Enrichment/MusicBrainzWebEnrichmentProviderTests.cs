using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Enrichment.Providers;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

public class MusicBrainzWebEnrichmentProviderTests
{
    [Fact]
    public async Task LooksUpByMbidTag_WhenPresent_ReturnsMatched()
    {
        var svc = new StubMb
        {
            ByMbid = _ => new MusicBrainzRecording(
                "mb-1", "One More Time", "Daft Punk", "Daft Punk", "rel-1", "Discovery", 2001, "USXXX", 222_000),
        };
        var provider = Create(svc);
        var song = Song(artist: "Daft Punk", title: "One More Time", mbid: "mb-1", durationSec: 222);

        var outcome = await provider.TryEnrichAsync(song);

        var matched = Assert.IsType<ProviderMatched>(outcome);
        Assert.Equal("mb-1", matched.Result.MusicBrainzId);
        Assert.Equal("rel-1", matched.Result.MusicBrainzReleaseId);
        Assert.Equal(EnrichmentStatus.Matched, matched.Result.RecommendedStatus);
        Assert.Equal(1, svc.MbidCalls);
        Assert.Equal(0, svc.SearchCalls);
    }

    [Fact]
    public async Task LooksUpByIsrcTag_WhenNoMbid_ReturnsMatched()
    {
        var svc = new StubMb
        {
            ByIsrc = _ => new MusicBrainzRecording(
                "mb-2", "Get Lucky", "Daft Punk", "Daft Punk", "rel-2", "RAM", 2013, "USQX91300108", 248_000),
        };
        var provider = Create(svc);
        var song = Song(artist: "Daft Punk", title: "Get Lucky", isrc: "USQX91300108", durationSec: 248);

        var outcome = await provider.TryEnrichAsync(song);

        var matched = Assert.IsType<ProviderMatched>(outcome);
        Assert.Equal("mb-2", matched.Result.MusicBrainzId);
        Assert.Equal(1, svc.IsrcCalls);
        Assert.Equal(0, svc.SearchCalls);
    }

    [Fact]
    public async Task NameSearch_WhenNoIdentifiers_ScoresAndMatches()
    {
        var svc = new StubMb
        {
            Search = (a, t) =>
            [
                new MusicBrainzRecording("mb-3", "Lucid Dreams", "Juice WRLD", "Juice WRLD", "rel-3", "Goodbye & Good Riddance", 2018, null, 239_000, Score: 100),
            ],
        };
        var provider = Create(svc);
        var song = Song(artist: "Juice WRLD", title: "Lucid Dreams", durationSec: 239);

        var outcome = await provider.TryEnrichAsync(song);

        var matched = Assert.IsType<ProviderMatched>(outcome);
        Assert.Equal("mb-3", matched.Result.MusicBrainzId);
        Assert.Equal(1, svc.SearchCalls);
    }

    [Fact]
    public async Task NameSearch_VersionMismatch_DoesNotMatchStudioToLive()
    {
        var svc = new StubMb
        {
            Search = (a, t) =>
            [
                new MusicBrainzRecording("mb-live", "Anthem (Live)", "Band", "Band", "rel-live", "Live at X", 2010, null, 200_000, Score: 100),
            ],
        };
        var provider = Create(svc);
        var song = Song(artist: "Band", title: "Anthem", durationSec: 200); // studio request

        var outcome = await provider.TryEnrichAsync(song);

        // The version penalty pushes the live-only result below MinConfidence, so it is not
        // matched — but the candidate is preserved (with the version_mismatch reason) for review.
        var noMatch = Assert.IsType<ProviderNoMatch>(outcome);
        Assert.NotNull(noMatch.BestCandidate);
        Assert.Contains("version_mismatch", noMatch.BestCandidate!.MatchWarnings);
    }

    [Fact]
    public async Task NameSearch_PassesPathDerivedAlbumHintToSearch()
    {
        var svc = new StubMb
        {
            Search = (a, t) =>
            [
                new MusicBrainzRecording("mb-3", "Lucid Dreams", "Juice WRLD", "Juice WRLD", "rel-3", "Goodbye & Good Riddance", 2018, null, 239_000, Score: 100),
            ],
        };
        var provider = Create(svc);
        var song = Song(durationSec: 239); // untagged: artist/album/title come from the path
        song.SourcePath = "/s/Juice WRLD/Goodbye & Good Riddance/05 Lucid Dreams.mp3";

        var outcome = await provider.TryEnrichAsync(song);

        Assert.IsType<ProviderMatched>(outcome);
        Assert.Equal("Goodbye & Good Riddance", svc.LastSearchAlbum);
    }

    [Fact]
    public async Task NameSearch_NoResults_ReturnsNoMatch()
    {
        var svc = new StubMb { Search = (_, _) => [] };
        var provider = Create(svc);
        var song = Song(artist: "Nobody", title: "Nothing");

        var outcome = await provider.TryEnrichAsync(song);

        Assert.IsType<ProviderNoMatch>(outcome);
    }

    [Fact]
    public async Task ExactLookup_PropagatesReleaseGroupCompilationAndDiscData()
    {
        var svc = new StubMb
        {
            ByMbid = _ => new MusicBrainzRecording(
                "mb-1", "A Hit", "Various Performers", "Various Performers", "rel-1", "Greatest Hits", 2001, "USXXX", 200_000,
                Artists: "Alice; Bob",
                ArtistMusicBrainzIds: "id-a; id-b",
                AlbumArtistMusicBrainzId: "aa-1",
                ReleaseGroupId: "rg-1",
                ReleaseTypePrimary: "album",
                ReleaseTypes: "album; compilation",
                IsCompilation: true,
                TotalDiscs: 2,
                TotalTracks: 30),
        };
        var provider = Create(svc);
        var song = Song(artist: "Various Performers", title: "A Hit", mbid: "mb-1", durationSec: 200);

        var outcome = await provider.TryEnrichAsync(song);

        var result = Assert.IsType<ProviderMatched>(outcome).Result;
        Assert.Equal("Alice; Bob", result.Artists);
        Assert.Equal("id-a; id-b", result.ArtistMusicBrainzIds);
        Assert.Equal("aa-1", result.AlbumArtistMusicBrainzId);
        Assert.Equal("rg-1", result.MusicBrainzReleaseGroupId);
        Assert.Equal("album; compilation", result.ReleaseTypes);
        Assert.True(result.IsCompilation);
        Assert.Equal(2, result.TotalDiscs);
        Assert.Equal(30, result.TotalTracks);
    }

    // --- helpers ---

    private static MusicBrainzWebEnrichmentProvider Create(IMusicBrainzWebService svc) =>
        new(svc,
            Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions { SourceDirectory = "/s", DestinationDirectory = "/d" }),
            NullLogger<MusicBrainzWebEnrichmentProvider>.Instance);

    private static SongMetadata Song(
        string? artist = null, string? title = null, string? mbid = null, string? isrc = null, int? durationSec = null)
        => new()
        {
            OwnerUserId = MusicHoarder.Api.Auth.WellKnownUsers.OwnerId,
            SourcePath = "/x.mp3",
            FileName = "x.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            Artist = artist,
            Title = title,
            MusicBrainzId = mbid,
            Isrc = isrc,
            DurationSeconds = durationSec,
        };

    private sealed class StubMb : IMusicBrainzWebService
    {
        public Func<string, MusicBrainzRecording?>? ByMbid { get; set; }
        public Func<string, MusicBrainzRecording?>? ByIsrc { get; set; }
        public Func<string, string, IReadOnlyList<MusicBrainzRecording>>? Search { get; set; }

        public int MbidCalls { get; private set; }
        public int IsrcCalls { get; private set; }
        public int SearchCalls { get; private set; }

        public Task<MusicBrainzRecording?> LookupByRecordingIdAsync(string mbid, CancellationToken ct = default)
        {
            MbidCalls++;
            return Task.FromResult(ByMbid?.Invoke(mbid));
        }

        public Task<MusicBrainzRecording?> LookupByIsrcAsync(string isrc, CancellationToken ct = default)
        {
            IsrcCalls++;
            return Task.FromResult(ByIsrc?.Invoke(isrc));
        }

        public string? LastSearchAlbum { get; private set; }

        public Task<IReadOnlyList<MusicBrainzRecording>> SearchAsync(string artist, string title, int limit, string? album = null, CancellationToken ct = default)
        {
            SearchCalls++;
            LastSearchAlbum = album;
            return Task.FromResult(Search?.Invoke(artist, title) ?? []);
        }
    }
}
