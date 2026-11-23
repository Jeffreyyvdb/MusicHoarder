using MusicHoarder.Api.Library;
using MusicHoarder.Api.Listening;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Metadata;

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
/// A play counts for its lead artist only, the name the Artists grid lists a song under: the album
/// artist, else the track's credit without its featured guests. Never a split of "&amp;" or ",", which
/// would turn "Tyler, The Creator" into two artists nobody can open or follow. A play weighs half as
/// much every <see cref="HalfLife"/>, so last week's discovery outranks last spring's rotation. One song on repeat is not a reason to watch an artist's releases: a suggestion needs
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
            .Select(p => (Name: LeadArtistOf(p), Play: p))
            .Where(x => x.Name is not null && !ArtistCreditEvidence.IsPlaceholderName(x.Name))
            .Select(x => (Key: KeyOf(x.Name), Name: x.Name!, x.Play))
            .Where(x => x.Key.Length > 0 && !followedKeys.Contains(x.Key))
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .Select(g => new Ranked(
                g.Key,
                g.GroupBy(x => x.Name, StringComparer.Ordinal)
                    .OrderByDescending(n => n.Count()).ThenBy(n => n.Key, StringComparer.Ordinal).First().Key,
                g.Count(),
                g.Select(x => x.Play.SongId).Distinct().Count(),
                g.Max(x => x.Play.PlayedAtUtc),
                g.Sum(x => Math.Pow(0.5, Math.Max(0, (nowUtc - x.Play.PlayedAtUtc) / HalfLife)))))
            .Where(r => r.Plays >= MinPlays && r.Songs >= MinSongs)
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Plays)
            .ThenBy(r => r.Artist, StringComparer.OrdinalIgnoreCase)
            .Take(Limit)
            .ToList();
    }

    /// <summary>
    /// The album artist (unless it is "Various Artists" or another placeholder, when the track's own
    /// credit says more), else the track credit up to its first guest: split only on ";" or a
    /// featuring clause, the two joins that are never part of one name.
    /// </summary>
    internal static string? LeadArtistOf(ListeningEventRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.AlbumArtist) && !ArtistCreditEvidence.IsPlaceholderName(row.AlbumArtist))
            return row.AlbumArtist.Trim();
        if (string.IsNullOrWhiteSpace(row.Artist))
            return null;
        var credit = row.Artist.Trim();
        return credit.Contains(';') || ArtistCreditNormalizer.HasFeaturingDelimiter(credit)
            ? ArtistCreditNormalizer.SplitArtists(credit).FirstOrDefault()
            : credit;
    }

    public sealed record Ranked(string Key, string Artist, int Plays, int Songs, DateTime LastPlayedAtUtc, double Score);
}
