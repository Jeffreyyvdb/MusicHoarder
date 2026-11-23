using System.Globalization;
using MusicHoarder.Api.Metadata;

namespace MusicHoarder.Api.Listening;

/// <summary>
/// Every aggregate the listening page shows, as one pure function over a caller's own play rows, so
/// none of it needs a database to be tested. The caps below are the page's: it never gets more than
/// that many of a list, whatever the library holds.
/// </summary>
public static class ListeningStatsCalculator
{
    public const int TopTracksLimit = 10;
    public const int TopArtistsLimit = 10;
    public const int TopAlbumsLimit = 10;
    public const int TopGenresLimit = 12;
    public const int RecentLimit = 50;
    public const int NightEndsBeforeHour = 5;

    public static ListeningStats Compute(
        IReadOnlyList<ListeningEventRow> events,
        IReadOnlyList<ListeningEventRow> recent,
        ListeningRange range,
        DateTime nowUtc,
        TimeSpan utcOffset,
        DateTime? firstPlayAtUtc)
    {
        var plays = events
            .Select(e => new Play(e, e.PlayedAtUtc + utcOffset))
            .ToList();

        var clock = new int[24];
        var weekdays = new int[7];
        foreach (var p in plays)
        {
            clock[p.Local.Hour]++;
            weekdays[((int)p.Local.DayOfWeek + 6) % 7]++;
        }

        var days = plays
            .GroupBy(p => p.Local.Date)
            .OrderBy(g => g.Key)
            .Select(g => new ListeningDay(DayKey(g.Key), g.Count(), Minutes(g.Sum(p => Seconds(p)))))
            .ToList();

        return new ListeningStats(
            range.Key(),
            range.FromUtc(nowUtc),
            firstPlayAtUtc,
            Totals(plays),
            TopTracks(plays),
            TopArtists(plays),
            TopAlbums(plays),
            TopGenres(plays),
            clock,
            weekdays,
            days,
            Facts(plays, clock, nowUtc + utcOffset),
            recent
                .OrderByDescending(e => e.PlayedAtUtc)
                .Take(RecentLimit)
                .Select(e => new RecentStream(e.SongId, e.Title, e.Artist, e.Album, e.PlayedAtUtc))
                .ToList());
    }

    private sealed record Play(ListeningEventRow Row, DateTime Local);

    private sealed record RepeatedDay(DateTime Date, int Streams, ListeningEventRow Row);

    private static long Seconds(Play p) => p.Row.DurationSeconds ?? 0;

    private static int Minutes(long seconds) => (int)Math.Round(seconds / 60.0, MidpointRounding.AwayFromZero);

    private static string DayKey(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The artists a play is credited to: every artist on the track, once each. Suggestions share it.</summary>
    internal static IEnumerable<string> ArtistsOf(ListeningEventRow row) =>
        string.IsNullOrWhiteSpace(row.Artist)
            ? []
            : ArtistCreditNormalizer.SplitArtists(row.Artist)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .DistinctBy(a => a, StringComparer.OrdinalIgnoreCase);

    private static (string Artist, string Album)? AlbumKeyOf(ListeningEventRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Album))
            return null;
        var artist = string.IsNullOrWhiteSpace(row.AlbumArtist) ? row.Artist : row.AlbumArtist;
        return (artist?.Trim() ?? string.Empty, row.Album.Trim());
    }

    private static ListeningTotals Totals(List<Play> plays) => new(
        plays.Count,
        Minutes(plays.Sum(Seconds)),
        plays.Select(p => p.Row.SongId).Distinct().Count(),
        plays.SelectMany(p => ArtistsOf(p.Row)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
        plays.Select(p => AlbumKeyOf(p.Row)).Where(k => k is not null).Select(k => k!.Value)
            .Distinct(PairComparer.Instance).Count());

    private static List<TopTrack> TopTracks(List<Play> plays) => plays
        .GroupBy(p => p.Row.SongId)
        .Select(g => new Ranked<IGrouping<int, Play>>(g, g.Count(), g.Sum(Seconds), g.Max(p => p.Row.PlayedAtUtc)))
        .OrderByRank()
        .Take(TopTracksLimit)
        .Select(r =>
        {
            var latest = r.Item.MaxBy(p => p.Row.PlayedAtUtc)!.Row;
            return new TopTrack(r.Item.Key, latest.Title, latest.Artist, latest.Album, r.Streams, Minutes(r.Seconds));
        })
        .ToList();

    private static List<TopArtist> TopArtists(List<Play> plays) => plays
        .SelectMany(p => ArtistsOf(p.Row).Select(a => (Artist: a, Play: p)))
        .GroupBy(x => x.Artist, StringComparer.OrdinalIgnoreCase)
        .Select(g => new Ranked<IGrouping<string, (string Artist, Play Play)>>(
            g, g.Count(), g.Sum(x => Seconds(x.Play)), g.Max(x => x.Play.Row.PlayedAtUtc)))
        .OrderByRank()
        .Take(TopArtistsLimit)
        .Select(r => new TopArtist(
            MostCommon(r.Item.Select(x => x.Artist)),
            r.Streams,
            Minutes(r.Seconds),
            RepresentativeSong(r.Item.Select(x => x.Play))))
        .ToList();

    private sealed record AlbumPlay(string Artist, string Album, Play Play);

    private static List<TopAlbum> TopAlbums(List<Play> plays) => plays
        .Select(p => AlbumKeyOf(p.Row) is { } k ? new AlbumPlay(k.Artist, k.Album, p) : null)
        .OfType<AlbumPlay>()
        .GroupBy(x => (x.Artist, x.Album), PairComparer.Instance)
        .Select(g => new Ranked<IGrouping<(string Artist, string Album), AlbumPlay>>(
            g, g.Count(), g.Sum(x => Seconds(x.Play)), g.Max(x => x.Play.Row.PlayedAtUtc)))
        .OrderByRank()
        .Take(TopAlbumsLimit)
        .Select(r => new TopAlbum(
            MostCommon(r.Item.Select(x => x.Album)),
            NullIfEmpty(MostCommon(r.Item.Select(x => x.Artist))),
            r.Streams,
            Minutes(r.Seconds),
            RepresentativeSong(r.Item.Select(x => x.Play))))
        .ToList();

    private static List<TopGenre> TopGenres(List<Play> plays) => plays
        .SelectMany(p => MultiValue.Split(p.Row.Genre).Distinct(StringComparer.OrdinalIgnoreCase))
        .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
        .Select(g => new TopGenre(MostCommon(g), g.Count()))
        .OrderByDescending(g => g.Streams)
        .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
        .Take(TopGenresLimit)
        .ToList();

    private static ListeningFacts Facts(List<Play> plays, int[] clock, DateTime localNow)
    {
        if (plays.Count == 0)
            return new ListeningFacts(null, 0, null, null, null, null);

        var dayCounts = plays.GroupBy(p => p.Local.Date).ToDictionary(g => g.Key, g => g.Count());
        var busiest = dayCounts.OrderByDescending(d => d.Value).ThenByDescending(d => d.Key).First();

        var repeated = plays
            .GroupBy(p => (p.Row.SongId, p.Local.Date))
            .Select(g => new RepeatedDay(g.Key.Date, g.Count(), g.MaxBy(p => p.Row.PlayedAtUtc)!.Row))
            .Where(g => g.Streams >= 2)
            .OrderByDescending(g => g.Streams)
            .ThenByDescending(g => g.Date)
            .FirstOrDefault();

        var peak = 0;
        for (var h = 1; h < clock.Length; h++)
            if (clock[h] > clock[peak]) peak = h;

        var night = plays.Count(p => p.Local.Hour < NightEndsBeforeHour);

        return new ListeningFacts(
            LongestRun(dayCounts.Keys),
            CurrentStreak(dayCounts.Keys, localNow.Date),
            new BusiestDay(DayKey(busiest.Key), busiest.Value),
            repeated is null
                ? null
                : new MostRepeated(
                    repeated.Row.SongId, repeated.Row.Title, repeated.Row.Artist,
                    DayKey(repeated.Date), repeated.Streams),
            (double)night / plays.Count,
            peak);
    }

    private static LongestStreak? LongestRun(IEnumerable<DateTime> days)
    {
        LongestStreak? best = null;
        DateTime? start = null;
        DateTime? previous = null;
        var length = 0;
        foreach (var day in days.Order())
        {
            if (previous is { } p && day == p.AddDays(1))
            {
                length++;
            }
            else
            {
                start = day;
                length = 1;
            }

            previous = day;
            if (best is null || length >= best.Days)
                best = new LongestStreak(length, DayKey(start!.Value), DayKey(day));
        }

        return best;
    }

    /// <summary>
    /// The run of consecutive days that ends today or yesterday: someone who has not yet played
    /// today has not lost their streak.
    /// </summary>
    private static int CurrentStreak(IEnumerable<DateTime> days, DateTime today)
    {
        var set = days.ToHashSet();
        var day = set.Contains(today) ? today : today.AddDays(-1);
        var length = 0;
        while (set.Contains(day))
        {
            length++;
            day = day.AddDays(-1);
        }

        return length;
    }

    private static int RepresentativeSong(IEnumerable<Play> plays) => plays
        .GroupBy(p => p.Row.SongId)
        .OrderByDescending(g => g.Count())
        .ThenByDescending(g => g.Max(p => p.Row.PlayedAtUtc))
        .First().Key;

    private static string MostCommon(IEnumerable<string> spellings) => spellings
        .GroupBy(s => s, StringComparer.Ordinal)
        .OrderByDescending(g => g.Count())
        .ThenBy(g => g.Key, StringComparer.Ordinal)
        .First().Key;

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private sealed record Ranked<T>(T Item, int Streams, long Seconds, DateTime Latest);

    private static IEnumerable<Ranked<T>> OrderByRank<T>(this IEnumerable<Ranked<T>> source) => source
        .OrderByDescending(r => r.Streams)
        .ThenByDescending(r => r.Seconds)
        .ThenByDescending(r => r.Latest);

    private sealed class PairComparer : IEqualityComparer<(string Artist, string Album)>
    {
        public static readonly PairComparer Instance = new();

        public bool Equals((string Artist, string Album) x, (string Artist, string Album) y) =>
            string.Equals(x.Artist, y.Artist, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Album, y.Album, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Artist, string Album) value) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Artist),
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Album));
    }
}
