using FuzzySharp;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Spotify;

/// <summary>
/// Pure, in-memory algorithm that ranks a Spotify liked track against the owner's library.
/// Given a pre-built <see cref="TrackIndex"/> it decides, in priority order, whether the track is
/// already <see cref="ComparisonMatchStatus.InLibrary"/> (exact Spotify id, then a normalized
/// artist+title hit), a fuzzy <see cref="ComparisonMatchStatus.PossibleMatch"/>, or
/// <see cref="ComparisonMatchStatus.NotInLibrary"/>.
/// <para>
/// Deliberately free of any dependency on the database, the Spotify API, or the like/sync side
/// effects — those belong to <see cref="SpotifyLibraryComparisonService"/>, which builds the index
/// from persisted rows and acts on the verdict. Keeping the matcher separate lets the ranking rules
/// be exercised in isolation (see <c>SpotifyTrackLibraryMatcherTests</c>) without standing up the
/// full comparison service.
/// </para>
/// </summary>
public static class SpotifyTrackLibraryMatcher
{
    private const double FuzzyThreshold = 85.0;

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

    // Delegates to the shared normalizer so all providers + library comparison agree on
    // case/punctuation/feat./diacritic handling.
    internal static string Normalize(string? s) => TitleNormalizer.NormalizeForSearch(s);
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
        NormalizedArtist = SpotifyTrackLibraryMatcher.Normalize(artist);
        NormalizedTitle = SpotifyTrackLibraryMatcher.Normalize(title);
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
