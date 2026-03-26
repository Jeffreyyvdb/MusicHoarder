using System.Text.RegularExpressions;
using FuzzySharp;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Spotify;

public partial class SpotifyLibraryComparisonService(
    ISpotifyApiService spotifyApi,
    IServiceScopeFactory scopeFactory,
    ILogger<SpotifyLibraryComparisonService> logger) : ISpotifyLibraryComparisonService
{
    private const double FuzzyThreshold = 85.0;

    public async Task<SpotifyComparisonResponse> CompareAsync(
        int offset = 0,
        int limit = 50,
        ComparisonMatchStatus? matchStatus = null,
        CancellationToken ct = default)
    {
        var trackIndex = await LoadTrackIndexAsync(ct);

        if (matchStatus is null)
        {
            var likedSongs = await spotifyApi.GetLikedSongsAsync(offset, limit, ct);
            var items = MatchAll(likedSongs.Items, trackIndex);
            return new SpotifyComparisonResponse(likedSongs.Total, likedSongs.Offset, likedSongs.Limit, items);
        }

        const int batchSize = 50;
        var matched = new List<SpotifyComparisonItem>();
        var spotifyOffset = 0;
        var totalLiked = 0;

        while (true)
        {
            var page = await spotifyApi.GetLikedSongsAsync(spotifyOffset, batchSize, ct);
            if (page.Items.Count == 0) break;

            totalLiked = page.Total;

            foreach (var song in page.Items)
            {
                var (status, matchedTrack, confidence) = FindBestMatch(song, trackIndex);
                if (status != matchStatus) continue;

                matched.Add(new SpotifyComparisonItem(
                    song.SpotifyId,
                    song.Title,
                    song.Artist,
                    song.Album,
                    song.AlbumArt,
                    song.DurationMs,
                    song.AddedAt,
                    status,
                    matchedTrack,
                    confidence));
            }

            spotifyOffset += page.Items.Count;
            if (spotifyOffset >= page.Total) break;
        }

        var totalFiltered = matched.Count;
        var pageItems = matched.Skip(offset).Take(limit).ToList();
        return new SpotifyComparisonResponse(totalFiltered, offset, limit, pageItems);
    }

    public async Task<SpotifyComparisonSummaryResponse> GetSummaryAsync(CancellationToken ct = default)
    {
        var trackIndex = await LoadTrackIndexAsync(ct);

        int total = 0, inLibrary = 0, possibleMatch = 0, notInLibrary = 0;
        int spotifyOffset = 0;
        const int batchSize = 50;

        while (true)
        {
            var page = await spotifyApi.GetLikedSongsAsync(spotifyOffset, batchSize, ct);

            if (page.Items.Count == 0) break;

            total = page.Total;

            foreach (var likedSong in page.Items)
            {
                var (status, _, _) = FindBestMatch(likedSong, trackIndex);
                switch (status)
                {
                    case ComparisonMatchStatus.InLibrary: inLibrary++; break;
                    case ComparisonMatchStatus.PossibleMatch: possibleMatch++; break;
                    case ComparisonMatchStatus.NotInLibrary: notInLibrary++; break;
                }
            }

            spotifyOffset += page.Items.Count;
            if (spotifyOffset >= page.Total) break;
        }

        return new SpotifyComparisonSummaryResponse(total, inLibrary, possibleMatch, notInLibrary);
    }

    private async Task<TrackIndex> LoadTrackIndexAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var tracks = await db.Songs
            .AsNoTracking()
            .Where(s => s.DeletedAtUtc == null)
            .Select(s => new TrackIndexEntry(
                s.Id,
                s.SpotifyId,
                s.Artist,
                s.Title,
                s.EnrichmentStatus))
            .ToListAsync(ct);

        logger.LogDebug("Loaded {Count} library tracks for comparison", tracks.Count);
        return new TrackIndex(tracks);
    }

    private static IReadOnlyList<SpotifyComparisonItem> MatchAll(
        IReadOnlyList<SpotifyTrackItem> likedSongs,
        TrackIndex index)
    {
        var results = new List<SpotifyComparisonItem>(likedSongs.Count);

        foreach (var song in likedSongs)
        {
            var (status, matched, confidence) = FindBestMatch(song, index);
            results.Add(new SpotifyComparisonItem(
                song.SpotifyId,
                song.Title,
                song.Artist,
                song.Album,
                song.AlbumArt,
                song.DurationMs,
                song.AddedAt,
                status,
                matched,
                confidence));
        }

        return results;
    }

    internal static (ComparisonMatchStatus Status, ComparisonMatchedTrack? Track, double? Confidence)
        FindBestMatch(SpotifyTrackItem likedSong, TrackIndex index)
    {
        if (index.BySpotifyId.TryGetValue(likedSong.SpotifyId, out var exactMatch))
        {
            return (ComparisonMatchStatus.InLibrary, ToMatchedTrack(exactMatch), 1.0);
        }

        var normalizedArtist = Normalize(likedSong.Artist);
        var normalizedTitle = Normalize(likedSong.Title);
        var key = $"{normalizedArtist}\0{normalizedTitle}";

        if (index.ByNormalizedArtistTitle.TryGetValue(key, out var normalizedMatch))
        {
            return (ComparisonMatchStatus.InLibrary, ToMatchedTrack(normalizedMatch), 0.95);
        }

        TrackIndexEntry? bestFuzzy = null;
        double bestScore = 0;

        foreach (var entry in index.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.NormalizedArtist) || string.IsNullOrWhiteSpace(entry.NormalizedTitle))
                continue;

            var artistScore = Fuzz.WeightedRatio(normalizedArtist, entry.NormalizedArtist);
            var titleScore = Fuzz.WeightedRatio(normalizedTitle, entry.NormalizedTitle);

            if (artistScore >= FuzzyThreshold && titleScore >= FuzzyThreshold)
            {
                var combinedScore = (artistScore + titleScore) / 200.0;
                if (combinedScore > bestScore)
                {
                    bestScore = combinedScore;
                    bestFuzzy = entry;
                }
            }
        }

        if (bestFuzzy is not null)
        {
            return (ComparisonMatchStatus.PossibleMatch, ToMatchedTrack(bestFuzzy), Math.Round(bestScore, 2));
        }

        return (ComparisonMatchStatus.NotInLibrary, null, null);
    }

    private static ComparisonMatchedTrack ToMatchedTrack(TrackIndexEntry entry) =>
        new(entry.Id, entry.Title, entry.Artist, entry.EnrichmentStatus.ToString());

    internal static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return string.Empty;

        var result = s.ToLowerInvariant();
        result = ParenthesesPattern().Replace(result, "");
        result = BracketsPattern().Replace(result, "");
        result = FeaturingPattern().Replace(result, "");
        result = PunctuationPattern().Replace(result, "");
        result = WhitespacePattern().Replace(result, " ");
        return result.Trim();
    }

    [GeneratedRegex(@"\(.*?\)", RegexOptions.Compiled)]
    private static partial Regex ParenthesesPattern();

    [GeneratedRegex(@"\[.*?\]", RegexOptions.Compiled)]
    private static partial Regex BracketsPattern();

    [GeneratedRegex(@"\b(feat\.?|ft\.?)\s.*", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex FeaturingPattern();

    [GeneratedRegex(@"[^\w\s]", RegexOptions.Compiled)]
    private static partial Regex PunctuationPattern();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespacePattern();
}

internal sealed class TrackIndexEntry
{
    public int Id { get; }
    public string? SpotifyId { get; }
    public string? Artist { get; }
    public string? Title { get; }
    public EnrichmentStatus EnrichmentStatus { get; }
    public string NormalizedArtist { get; }
    public string NormalizedTitle { get; }

    public TrackIndexEntry(int id, string? spotifyId, string? artist, string? title, EnrichmentStatus enrichmentStatus)
    {
        Id = id;
        SpotifyId = spotifyId;
        Artist = artist;
        Title = title;
        EnrichmentStatus = enrichmentStatus;
        NormalizedArtist = SpotifyLibraryComparisonService.Normalize(artist);
        NormalizedTitle = SpotifyLibraryComparisonService.Normalize(title);
    }
}

internal sealed class TrackIndex
{
    public IReadOnlyList<TrackIndexEntry> Entries { get; }
    public Dictionary<string, TrackIndexEntry> BySpotifyId { get; }
    public Dictionary<string, TrackIndexEntry> ByNormalizedArtistTitle { get; }

    public TrackIndex(IReadOnlyList<TrackIndexEntry> entries)
    {
        Entries = entries;

        BySpotifyId = new Dictionary<string, TrackIndexEntry>(StringComparer.OrdinalIgnoreCase);
        ByNormalizedArtistTitle = new Dictionary<string, TrackIndexEntry>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.SpotifyId))
                BySpotifyId.TryAdd(entry.SpotifyId, entry);

            if (!string.IsNullOrWhiteSpace(entry.NormalizedArtist) && !string.IsNullOrWhiteSpace(entry.NormalizedTitle))
            {
                var key = $"{entry.NormalizedArtist}\0{entry.NormalizedTitle}";
                ByNormalizedArtistTitle.TryAdd(key, entry);
            }
        }
    }
}
