namespace MusicHoarder.Api.Listening;

/// <summary>The columns of a <c>ListeningEvent</c> the stats read, so a lifetime query loads no more.</summary>
public sealed record ListeningEventRow(
    int SongId,
    DateTime PlayedAtUtc,
    string Title,
    string? Artist,
    string? AlbumArtist,
    string? Album,
    string? Genre,
    int? DurationSeconds);

public sealed record ListeningStats(
    string Range,
    DateTime? FromUtc,
    DateTime? TrackingSinceUtc,
    ListeningTotals Totals,
    IReadOnlyList<TopTrack> TopTracks,
    IReadOnlyList<TopArtist> TopArtists,
    IReadOnlyList<TopAlbum> TopAlbums,
    IReadOnlyList<TopGenre> TopGenres,
    int[] Clock,
    int[] Weekdays,
    IReadOnlyList<ListeningDay> Days,
    ListeningFacts Facts,
    IReadOnlyList<RecentStream> Recent);

public sealed record ListeningTotals(
    int Streams, int Minutes, int DistinctTracks, int DistinctArtists, int DistinctAlbums);

public sealed record TopTrack(int SongId, string Title, string? Artist, string? Album, int Streams, int Minutes);

public sealed record TopArtist(string Name, int Streams, int Minutes, int SongId);

public sealed record TopAlbum(string Album, string? AlbumArtist, int Streams, int Minutes, int SongId);

public sealed record TopGenre(string Name, int Streams);

public sealed record ListeningDay(string Date, int Streams, int Minutes);

public sealed record ListeningFacts(
    LongestStreak? LongestStreak,
    int CurrentStreakDays,
    BusiestDay? BusiestDay,
    MostRepeated? MostRepeated,
    double? NightOwlShare,
    int? PeakHour);

public sealed record LongestStreak(int Days, string Start, string End);

public sealed record BusiestDay(string Date, int Streams);

public sealed record MostRepeated(int SongId, string Title, string? Artist, string Date, int Streams);

public sealed record RecentStream(
    int SongId, string Title, string? Artist, string? Album, DateTime PlayedAtUtc);
