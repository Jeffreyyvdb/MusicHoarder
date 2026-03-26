using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Spotify;

namespace MusicHoarder.Api.Tests.Spotify;

public class SpotifyLibraryComparisonServiceTests
{
    #region Normalize tests

    [Theory]
    [InlineData("Hello World", "hello world")]
    [InlineData("UPPERCASE", "uppercase")]
    [InlineData("Song (feat. Artist)", "song")]
    [InlineData("Song (Remix)", "song")]
    [InlineData("Song [Official Video]", "song")]
    [InlineData("Song ft. Someone", "song")]
    [InlineData("Song feat. Someone", "song")]
    [InlineData("Hello, World!", "hello world")]
    [InlineData("  Extra   Spaces  ", "extra spaces")]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData("Song (feat. X) [Remix]", "song")]
    public void Normalize_ProducesExpectedOutput(string? input, string expected)
    {
        Assert.Equal(expected, SpotifyLibraryComparisonService.Normalize(input));
    }

    #endregion

    #region FindBestMatch tests — exact Spotify ID

    [Fact]
    public void FindBestMatch_ExactSpotifyIdMatch_ReturnsInLibraryWith100Percent()
    {
        var likedSong = MakeLikedSong("spotify:123", "Some Artist", "Some Title");
        var index = BuildIndex(new TrackIndexEntry(1, "spotify:123", "Different Artist", "Different Title", EnrichmentStatus.Matched));

        var (status, matched, confidence) = SpotifyLibraryComparisonService.FindBestMatch(likedSong, index);

        Assert.Equal(ComparisonMatchStatus.InLibrary, status);
        Assert.NotNull(matched);
        Assert.Equal(1, matched.Id);
        Assert.Equal(1.0, confidence);
    }

    [Fact]
    public void FindBestMatch_ExactSpotifyIdMatch_CaseInsensitive()
    {
        var likedSong = MakeLikedSong("ABC123", "Artist", "Title");
        var index = BuildIndex(new TrackIndexEntry(1, "abc123", "Artist", "Title", EnrichmentStatus.Pending));

        var (status, _, confidence) = SpotifyLibraryComparisonService.FindBestMatch(likedSong, index);

        Assert.Equal(ComparisonMatchStatus.InLibrary, status);
        Assert.Equal(1.0, confidence);
    }

    #endregion

    #region FindBestMatch tests — normalized match

    [Fact]
    public void FindBestMatch_NormalizedArtistAndTitleMatch_ReturnsInLibrary()
    {
        var likedSong = MakeLikedSong("no-match-id", "Artist (feat. Someone)", "Song Title (Remix)");
        var index = BuildIndex(new TrackIndexEntry(42, null, "Artist", "Song Title", EnrichmentStatus.Matched));

        var (status, matched, confidence) = SpotifyLibraryComparisonService.FindBestMatch(likedSong, index);

        Assert.Equal(ComparisonMatchStatus.InLibrary, status);
        Assert.NotNull(matched);
        Assert.Equal(42, matched.Id);
        Assert.Equal(0.95, confidence);
    }

    [Fact]
    public void FindBestMatch_NormalizedMatch_StripsFeaturingAndParentheses()
    {
        var likedSong = MakeLikedSong("no-id", "Drake feat. Lil Wayne", "God's Plan (Official Video)");
        var index = BuildIndex(new TrackIndexEntry(10, null, "Drake", "God's Plan", EnrichmentStatus.Matched));

        var (status, _, _) = SpotifyLibraryComparisonService.FindBestMatch(likedSong, index);

        Assert.Equal(ComparisonMatchStatus.InLibrary, status);
    }

    #endregion

    #region FindBestMatch tests — fuzzy match

    [Fact]
    public void FindBestMatch_FuzzyMatch_ReturnsPossibleMatchWithScore()
    {
        var likedSong = MakeLikedSong("no-id", "Kendrick Lamar", "HUMBLE");
        var index = BuildIndex(new TrackIndexEntry(99, null, "Kendrik Lamar", "HUMBLE", EnrichmentStatus.Matched));

        var (status, matched, confidence) = SpotifyLibraryComparisonService.FindBestMatch(likedSong, index);

        Assert.Equal(ComparisonMatchStatus.PossibleMatch, status);
        Assert.NotNull(matched);
        Assert.Equal(99, matched.Id);
        Assert.NotNull(confidence);
        Assert.True(confidence >= 0.85);
    }

    [Fact]
    public void FindBestMatch_FuzzyMatch_PicksBestCandidate()
    {
        var likedSong = MakeLikedSong("no-id", "The Weeknd", "Blinding Lights");
        var index = BuildIndex(
            new TrackIndexEntry(1, null, "The Weekend", "Blinding Lights", EnrichmentStatus.Matched),
            new TrackIndexEntry(2, null, "The Weeknd", "Blinding Light", EnrichmentStatus.Matched));

        var (status, matched, _) = SpotifyLibraryComparisonService.FindBestMatch(likedSong, index);

        Assert.Equal(ComparisonMatchStatus.PossibleMatch, status);
        Assert.NotNull(matched);
    }

    #endregion

    #region FindBestMatch tests — no match

    [Fact]
    public void FindBestMatch_NoMatch_ReturnsNotInLibrary()
    {
        var likedSong = MakeLikedSong("no-id", "Completely Unknown Artist", "Totally Different Song");
        var index = BuildIndex(
            new TrackIndexEntry(1, null, "Artist A", "Song A", EnrichmentStatus.Matched),
            new TrackIndexEntry(2, null, "Artist B", "Song B", EnrichmentStatus.Pending));

        var (status, matched, confidence) = SpotifyLibraryComparisonService.FindBestMatch(likedSong, index);

        Assert.Equal(ComparisonMatchStatus.NotInLibrary, status);
        Assert.Null(matched);
        Assert.Null(confidence);
    }

    [Fact]
    public void FindBestMatch_EmptyIndex_ReturnsNotInLibrary()
    {
        var likedSong = MakeLikedSong("some-id", "Artist", "Title");
        var index = BuildIndex();

        var (status, matched, confidence) = SpotifyLibraryComparisonService.FindBestMatch(likedSong, index);

        Assert.Equal(ComparisonMatchStatus.NotInLibrary, status);
        Assert.Null(matched);
        Assert.Null(confidence);
    }

    #endregion

    #region FindBestMatch tests — priority ordering

    [Fact]
    public void FindBestMatch_PrefersExactIdOverNormalizedMatch()
    {
        var likedSong = MakeLikedSong("spotify:exact", "Artist", "Title");
        var index = BuildIndex(
            new TrackIndexEntry(1, "spotify:exact", "Different", "Different", EnrichmentStatus.Pending),
            new TrackIndexEntry(2, null, "Artist", "Title", EnrichmentStatus.Matched));

        var (status, matched, confidence) = SpotifyLibraryComparisonService.FindBestMatch(likedSong, index);

        Assert.Equal(ComparisonMatchStatus.InLibrary, status);
        Assert.Equal(1, matched!.Id);
        Assert.Equal(1.0, confidence);
    }

    [Fact]
    public void FindBestMatch_PrefersNormalizedOverFuzzy()
    {
        var likedSong = MakeLikedSong("no-id", "Artist Name", "Song Title");
        var index = BuildIndex(
            new TrackIndexEntry(1, null, "Artist Name", "Song Title", EnrichmentStatus.Matched),
            new TrackIndexEntry(2, null, "Artist Nme", "Song Title", EnrichmentStatus.Matched));

        var (status, matched, confidence) = SpotifyLibraryComparisonService.FindBestMatch(likedSong, index);

        Assert.Equal(ComparisonMatchStatus.InLibrary, status);
        Assert.Equal(1, matched!.Id);
        Assert.Equal(0.95, confidence);
    }

    #endregion

    #region CompareAsync integration tests

    [Fact]
    public async Task CompareAsync_ReturnsAnnotatedLikedSongs()
    {
        await using var db = CreateDb();
        SeedTracks(db,
            ("spotify:1", "Matched Artist", "Matched Title"),
            (null, "Normalized Artist", "Normalized Title"),
            (null, "Some Artist", "Some Song"));

        var likedSongs = new SpotifyLikedSongsResponse(3, 0, 50, new[]
        {
            MakeLikedSong("spotify:1", "Matched Artist", "Matched Title"),
            MakeLikedSong("no-match", "Normalized Artist (feat. X)", "Normalized Title (Remix)"),
            MakeLikedSong("no-match-2", "Unknown", "Unknown Song"),
        });

        var stubApi = new StubSpotifyApiService(likedSongs);
        var service = CreateService(db, stubApi);

        var result = await service.CompareAsync(0, 50);

        Assert.Equal(3, result.Total);
        Assert.Equal(3, result.Items.Count);

        Assert.Equal(ComparisonMatchStatus.InLibrary, result.Items[0].MatchStatus);
        Assert.Equal(1.0, result.Items[0].MatchConfidence);

        Assert.Equal(ComparisonMatchStatus.InLibrary, result.Items[1].MatchStatus);
        Assert.Equal(0.95, result.Items[1].MatchConfidence);

        Assert.Equal(ComparisonMatchStatus.NotInLibrary, result.Items[2].MatchStatus);
        Assert.Null(result.Items[2].MatchedTrack);
    }

    #endregion

    #region GetSummaryAsync integration tests

    [Fact]
    public async Task GetSummaryAsync_ReturnsCounts()
    {
        await using var db = CreateDb();
        SeedTracks(db,
            ("spotify:1", "Artist 1", "Title 1"),
            (null, "Artist 2", "Title 2"));

        var page1 = new SpotifyLikedSongsResponse(3, 0, 50, new[]
        {
            MakeLikedSong("spotify:1", "Artist 1", "Title 1"),
            MakeLikedSong("no-id", "Artist 2 (feat. X)", "Title 2"),
            MakeLikedSong("no-id-2", "Unknown", "Unknown"),
        });

        var stubApi = new StubSpotifyApiService(page1);
        var service = CreateService(db, stubApi);

        var summary = await service.GetSummaryAsync();

        Assert.Equal(3, summary.Total);
        Assert.Equal(2, summary.InLibrary);
        Assert.Equal(0, summary.PossibleMatch);
        Assert.Equal(1, summary.NotInLibrary);
    }

    #endregion

    #region Edge cases

    [Fact]
    public void FindBestMatch_SkipsEntriesWithNullArtistOrTitle_InFuzzyMatching()
    {
        var likedSong = MakeLikedSong("no-id", "Artist", "Title");
        var index = BuildIndex(
            new TrackIndexEntry(1, null, null, "Title", EnrichmentStatus.Pending),
            new TrackIndexEntry(2, null, "Artist", null, EnrichmentStatus.Pending));

        var (status, _, _) = SpotifyLibraryComparisonService.FindBestMatch(likedSong, index);

        Assert.Equal(ComparisonMatchStatus.NotInLibrary, status);
    }

    [Fact]
    public void FindBestMatch_MatchedTrack_IncludesEnrichmentStatus()
    {
        var likedSong = MakeLikedSong("spotify:x", "A", "B");
        var index = BuildIndex(new TrackIndexEntry(5, "spotify:x", "A", "B", EnrichmentStatus.NeedsReview));

        var (_, matched, _) = SpotifyLibraryComparisonService.FindBestMatch(likedSong, index);

        Assert.NotNull(matched);
        Assert.Equal("NeedsReview", matched.EnrichmentStatus);
    }

    [Fact]
    public async Task CompareAsync_ExcludesDeletedTracks()
    {
        await using var db = CreateDb();
        db.Songs.Add(new SongMetadata
        {
            SourcePath = "/test/deleted.mp3",
            FileSizeBytes = 1000,
            FileName = "deleted.mp3",
            Extension = ".mp3",
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            SpotifyId = "spotify:deleted",
            Artist = "Deleted Artist",
            Title = "Deleted Title",
            DeletedAtUtc = DateTime.UtcNow,
        });
        db.SaveChanges();

        var likedSongs = new SpotifyLikedSongsResponse(1, 0, 50, new[]
        {
            MakeLikedSong("spotify:deleted", "Deleted Artist", "Deleted Title"),
        });

        var stubApi = new StubSpotifyApiService(likedSongs);
        var service = CreateService(db, stubApi);

        var result = await service.CompareAsync(0, 50);

        Assert.Equal(ComparisonMatchStatus.NotInLibrary, result.Items[0].MatchStatus);
    }

    #endregion

    #region Helpers

    private static SpotifyTrackItem MakeLikedSong(string spotifyId, string artist, string title) =>
        new(spotifyId, title, artist, "Album", null, 200000, DateTime.UtcNow);

    private static TrackIndex BuildIndex(params TrackIndexEntry[] entries) =>
        new(entries);

    private static MusicHoarderDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private static void SeedTracks(MusicHoarderDbContext db, params (string? spotifyId, string artist, string title)[] tracks)
    {
        for (var i = 0; i < tracks.Length; i++)
        {
            var (spotifyId, artist, title) = tracks[i];
            db.Songs.Add(new SongMetadata
            {
                SourcePath = $"/test/track{i}.mp3",
                FileSizeBytes = 1000 + i,
                FileName = $"track{i}.mp3",
                Extension = ".mp3",
                LastModifiedUtc = DateTime.UtcNow,
                IndexedAtUtc = DateTime.UtcNow,
                SpotifyId = spotifyId,
                Artist = artist,
                Title = title,
            });
        }
        db.SaveChanges();
    }

    private static SpotifyLibraryComparisonService CreateService(MusicHoarderDbContext db, ISpotifyApiService spotifyApi)
    {
        var scopeFactory = new TestScopeFactory(db);
        var logger = NullLogger<SpotifyLibraryComparisonService>.Instance;
        return new SpotifyLibraryComparisonService(spotifyApi, scopeFactory, logger);
    }

    private sealed class StubSpotifyApiService(SpotifyLikedSongsResponse likedSongs) : ISpotifyApiService
    {
        public Task<SpotifyLikedSongsResponse> GetLikedSongsAsync(int offset = 0, int limit = 50, CancellationToken ct = default)
        {
            var items = likedSongs.Items.Skip(offset).Take(limit).ToList();
            return Task.FromResult(new SpotifyLikedSongsResponse(likedSongs.Total, offset, limit, items));
        }

        public Task<SpotifyPlaylistsResponse> GetPlaylistsAsync(CancellationToken ct = default) =>
            Task.FromResult(new SpotifyPlaylistsResponse(Array.Empty<SpotifyPlaylistItem>()));

        public Task<SpotifyPlaylistTracksResponse> GetPlaylistTracksAsync(string playlistId, int offset = 0, int limit = 50, CancellationToken ct = default) =>
            Task.FromResult(new SpotifyPlaylistTracksResponse(0, 0, 50, Array.Empty<SpotifyTrackItem>()));
    }

    private sealed class TestScopeFactory(MusicHoarderDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new TestScope(new TestServiceProvider(db));
    }

    private sealed class TestScope(IServiceProvider provider) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = provider;
        public void Dispose() { }
    }

    private sealed class TestServiceProvider(MusicHoarderDbContext db) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(MusicHoarderDbContext) ? db : null;
    }

    #endregion
}
