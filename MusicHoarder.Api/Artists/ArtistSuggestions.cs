using MusicHoarder.Api.Library;
using MusicHoarder.Api.Listening;
using MusicHoarder.Api.Matching;

namespace MusicHoarder.Api.Artists;

/// <summary>An artist worth following, and the listening that says so.</summary>
public sealed record ArtistSuggestion(
    string Artist,
    int Plays,
    int Songs,
    DateTime LastPlayedAtUtc,
    string? DeezerArtistId);

public sealed record ArtistSuggestionsResponse(IReadOnlyList<ArtistSuggestion> Suggestions);

/// <summary>
/// Which artists the Artists page suggests following: the ones the caller has been listening to
/// lately and does not follow yet. A pure function over the caller's own play rows, so it needs no
/// database to be tested.
/// <para>
/// A play counts for every artist credited on the track (the listening stats' rule), and weighs
/// half as much every <see cref="HalfLife"/>, so last week's discovery outranks last spring's
/// rotation. One song on repeat is not a reason to watch an artist's releases: a suggestion needs
/// <see cref="MinPlays"/> plays across <see cref="MinSongs"/> different songs.
/// </para>
/// </summary>
public static class ArtistSuggestions
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(90);
    public static readonly TimeSpan HalfLife = TimeSpan.FromDays(30);
    public const int MinPlays = 3;
    public const int MinSongs = 2;
    public const int Limit = 12;

    /// <summary>The key a follow is found under (<c>FollowedArtist.ArtistKey</c>).</summary>
    public static string KeyOf(string? artist) => TitleNormalizer.NormalizeForSearch(artist);

    /// <param name="plays">The caller's plays; rows before <see cref="Window"/> are ignored.</param>
    /// <param name="followedKeys">Keys (<see cref="KeyOf"/>) of the artists already followed.</param>
    public static IReadOnlyList<Ranked> Rank(
        IEnumerable<ListeningEventRow> plays, IReadOnlySet<string> followedKeys, DateTime nowUtc)
    {
        var from = nowUtc - Window;
        return plays
            .Where(p => p.PlayedAtUtc >= from)
            .SelectMany(p => ListeningStatsCalculator.ArtistsOf(p).Select(a => (Name: a.Trim(), Play: p)))
            .Select(x => (Key: KeyOf(x.Name), x.Name, x.Play))
            .Where(x => x.Key.Length > 0
                && !followedKeys.Contains(x.Key)
                && !DestinationPathResolver.IsVariousArtistsSentinel(x.Name))
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .Select(g => new Ranked(
                g.Key,
                g.GroupBy(x => x.Name, StringComparer.Ordinal)
                    .OrderByDescending(n => n.Count()).ThenBy(n => n.Key, StringComparer.Ordinal).First().Key,
                g.Count(),
                g.Select(x => x.Play.SongId).Distinct().Count(),
                g.Max(x => x.Play.PlayedAtUtc),
                g.Sum(x => Math.Pow(0.5, (nowUtc - x.Play.PlayedAtUtc) / HalfLife))))
            .Where(r => r.Plays >= MinPlays && r.Songs >= MinSongs)
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Plays)
            .ThenBy(r => r.Artist, StringComparer.OrdinalIgnoreCase)
            .Take(Limit)
            .ToList();
    }

    public sealed record Ranked(string Key, string Artist, int Plays, int Songs, DateTime LastPlayedAtUtc, double Score);
}
