using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Metadata;

namespace MusicHoarder.Api.Library;

/// <summary>
/// One track the radio may pick, reduced to the fields the scoring reads.
///
/// <para>
/// A row the caller does not own is built from what <see cref="Contracts.SharedSongRowDto"/>
/// publishes and nothing else, exactly as <c>GET /api/albums</c> does: the fields that type
/// withholds arrive here null, so a grantor's private state cannot steer a grantee's station.
/// <see cref="ArtistMusicBrainzIds"/> is the one signal that is always absent for a granted row.
/// </para>
///
/// <para>
/// <see cref="PlayCount"/>, <see cref="LikedAtUtc"/> and <see cref="LastPlayedAtUtc"/> are always
/// the <i>caller's</i> listening state — the song's own columns when they own it, their
/// <see cref="Persistence.UserSongState"/> row when they do not. Reading the owner's columns for a
/// granted row would tune the station to the wrong person's taste.
/// </para>
/// </summary>
public sealed record RadioTrackRow(
    int Id,
    string? Artist,
    string? AlbumArtist,
    /// <summary>Discrete credited artists, ';'-joined, as <c>SongMetadata.Artists</c> stores them.</summary>
    string? Artists,
    /// <summary>';'-joined artist MBIDs. Null for a granted row — the shared surface withholds it.</summary>
    string? ArtistMusicBrainzIds,
    string? Album,
    string? Genre,
    string? Label,
    int? Year,
    int? DurationSeconds,
    int PlayCount,
    DateTime? LikedAtUtc,
    DateTime? LastPlayedAtUtc,
    bool IsBuilt);

/// <summary>
/// Picks what plays next when a queue runs dry — the "keep the music going" continuation every
/// streaming service has, and the reason a one-track album no longer ends in silence.
///
/// <para>
/// This is the single definition of what "similar" means in this app, deliberately server-side.
/// Album grouping taught the lesson: a rule ported into <c>frontend/</c> and <c>android/</c>
/// becomes two rules that drift. Both clients call <c>GET /api/radio</c> and append what comes
/// back, so a station is identical on the phone and in the browser.
/// </para>
///
/// <para>
/// The signals are the ones the library actually owns. Spotify's audio-features and
/// <c>/recommendations</c> endpoints answer 404 for a dev-mode app — the same wall the Discover
/// feature hit — so there is no acoustic similarity to lean on, and none is faked here.
/// </para>
/// </summary>
public static class RadioRanker
{
    // Artist affinity. The three are alternatives scored by MAX rather than summed: a track by the
    // seed's artist normally satisfies all three at once, and adding them up would let one
    // relationship out-score every other signal combined.
    private const double SameLeadArtist = 50;
    private const double SharedArtistMbid = 45;
    private const double SharedCreditedArtist = 34;

    private const double SameAlbum = 20;
    private const double GenreOverlapMax = 26;
    private const double EraProximityMax = 15;
    /// <summary>Years apart at which the era bonus reaches zero.</summary>
    private const double EraSpanYears = 15;
    private const double SameLabel = 5;

    // Taste. Small next to artist affinity on purpose: this steers between neighbours, it does not
    // turn the station into a replay of the liked list.
    private const double LikedBonus = 12;
    private const double PlayCountBonusMax = 8;

    // Penalties.
    private const double JustPlayedPenalty = 30;
    private static readonly TimeSpan JustPlayedWindow = TimeSpan.FromHours(6);
    private const double RecentlyPlayedPenalty = 10;
    private static readonly TimeSpan RecentlyPlayedWindow = TimeSpan.FromDays(2);
    /// <summary>Skits and interludes are rarely what anyone wants next, but they are not banned.</summary>
    private const double ShortTrackPenalty = 22;
    private const int ShortTrackSeconds = 60;
    /// <summary>An unbuilt row streams from the source copy, so it plays — it just tags worse.</summary>
    private const double UnbuiltPenalty = 6;

    /// <summary>
    /// Tie-break spread. Scores collide constantly (same artist, same genre, same era all land on
    /// the same number), and without this a station would be the same handful of tracks in id
    /// order forever. It is a stable hash of (seed, candidate) rather than a random number, so one
    /// seed always yields the same station — across restarts, across the two clients, and in tests.
    /// </summary>
    private const double JitterMax = 6;

    /// <summary>Tracks by one artist per batch, so a station widens instead of becoming an album.</summary>
    private const int MaxPerArtist = 3;

    /// <summary>Back-to-back tracks by one artist, so the widening is audible.</summary>
    private const int MaxConsecutivePerArtist = 2;

    /// <summary>
    /// Orders <paramref name="candidates"/> by how well they follow <paramref name="seed"/> and
    /// returns at most <paramref name="limit"/> song ids.
    /// </summary>
    /// <param name="exclude">
    /// Ids already in the caller's queue. The seed is always excluded whether or not it is listed.
    /// </param>
    public static IReadOnlyList<int> Rank(
        RadioTrackRow seed,
        IEnumerable<RadioTrackRow> candidates,
        IReadOnlySet<int> exclude,
        int limit,
        DateTime nowUtc)
    {
        if (limit <= 0) return [];

        var seedLead = LeadArtistKey(seed);
        var seedCredits = CreditKeys(seed);
        var seedMbids = MbidKeys(seed);
        var seedGenres = GenreKeys(seed);
        var seedAlbum = TitleNormalizer.NormalizeForSearch(seed.Album);
        var seedLabel = TitleNormalizer.NormalizeForSearch(seed.Label);

        var scored = new List<(RadioTrackRow Row, double Score, string LeadKey)>();
        foreach (var row in candidates)
        {
            if (row.Id == seed.Id || exclude.Contains(row.Id)) continue;

            var lead = LeadArtistKey(row);
            var score = Score(
                row, lead, seedLead, seedCredits, seedMbids, seedGenres, seedAlbum, seedLabel,
                seed.Year, nowUtc)
                + (Jitter(seed.Id, row.Id) * JitterMax);
            scored.Add((row, score, lead));
        }

        scored.Sort((a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : a.Row.Id.CompareTo(b.Row.Id);
        });

        return SpreadByArtist(scored, limit);
    }

    /// <summary>
    /// Walks the scored list picking the best candidate that does not over-represent one artist.
    /// When the caps leave nothing pickable — a library of one artist, or a batch larger than the
    /// neighbourhood — they are relaxed rather than returning a short list: a queue that stops is
    /// the bug being fixed, so a repetitive continuation still beats silence.
    /// </summary>
    private static List<int> SpreadByArtist(
        List<(RadioTrackRow Row, double Score, string LeadKey)> scored, int limit)
    {
        var picked = new List<int>(Math.Min(limit, scored.Count));
        var perArtist = new Dictionary<string, int>(StringComparer.Ordinal);
        var consecutive = 0;
        var lastLead = string.Empty;

        while (picked.Count < limit && scored.Count > 0)
        {
            var index = scored.FindIndex(c => WithinCaps(c.LeadKey, perArtist, lastLead, consecutive));
            if (index < 0)
                index = scored.FindIndex(c => !SameArtistRun(c.LeadKey, lastLead, consecutive));
            if (index < 0)
                index = 0;

            var chosen = scored[index];
            scored.RemoveAt(index);
            picked.Add(chosen.Row.Id);

            perArtist[chosen.LeadKey] = perArtist.GetValueOrDefault(chosen.LeadKey) + 1;
            consecutive = chosen.LeadKey == lastLead ? consecutive + 1 : 1;
            lastLead = chosen.LeadKey;
        }

        return picked;
    }

    private static bool WithinCaps(
        string lead, Dictionary<string, int> perArtist, string lastLead, int consecutive) =>
        perArtist.GetValueOrDefault(lead) < MaxPerArtist && !SameArtistRun(lead, lastLead, consecutive);

    private static bool SameArtistRun(string lead, string lastLead, int consecutive) =>
        lead == lastLead && consecutive >= MaxConsecutivePerArtist;

    private static double Score(
        RadioTrackRow row,
        string lead,
        string seedLead,
        HashSet<string> seedCredits,
        HashSet<string> seedMbids,
        HashSet<string> seedGenres,
        string seedAlbum,
        string seedLabel,
        int? seedYear,
        DateTime nowUtc)
    {
        var score = ArtistAffinity(row, lead, seedLead, seedCredits, seedMbids);

        if (seedAlbum.Length > 0 && TitleNormalizer.NormalizeForSearch(row.Album) == seedAlbum)
            score += SameAlbum;

        score += GenreOverlap(seedGenres, GenreKeys(row)) * GenreOverlapMax;

        if (seedYear is { } sy && row.Year is { } ry)
        {
            var apart = Math.Abs(sy - ry);
            score += EraProximityMax * Math.Max(0, 1 - (apart / EraSpanYears));
        }

        if (seedLabel.Length > 0 && TitleNormalizer.NormalizeForSearch(row.Label) == seedLabel)
            score += SameLabel;

        if (row.LikedAtUtc is not null) score += LikedBonus;
        if (row.PlayCount > 0)
            score += Math.Min(PlayCountBonusMax, Math.Log2(1 + row.PlayCount) * 3);

        if (row.LastPlayedAtUtc is { } played)
        {
            var since = nowUtc - played;
            if (since < JustPlayedWindow) score -= JustPlayedPenalty;
            else if (since < RecentlyPlayedWindow) score -= RecentlyPlayedPenalty;
        }

        if (row.DurationSeconds is { } seconds && seconds > 0 && seconds < ShortTrackSeconds)
            score -= ShortTrackPenalty;

        if (!row.IsBuilt) score -= UnbuiltPenalty;

        return score;
    }

    private static double ArtistAffinity(
        RadioTrackRow row, string lead, string seedLead,
        HashSet<string> seedCredits, HashSet<string> seedMbids)
    {
        if (seedMbids.Count > 0 && MbidKeys(row).Overlaps(seedMbids)) return SharedArtistMbid;
        if (seedLead.Length > 0 && lead == seedLead) return SameLeadArtist;
        if (seedCredits.Count > 0 && CreditKeys(row).Overlaps(seedCredits)) return SharedCreditedArtist;
        return 0;
    }

    /// <summary>
    /// Jaccard overlap of the two genre sets, 0..1. Genre strings are free text written by whichever
    /// provider matched, so they are split on every delimiter seen in the wild before comparing.
    /// </summary>
    private static double GenreOverlap(HashSet<string> seed, HashSet<string> candidate)
    {
        if (seed.Count == 0 || candidate.Count == 0) return 0;
        var shared = seed.Count(candidate.Contains);
        if (shared == 0) return 0;
        return (double)shared / (seed.Count + candidate.Count - shared);
    }

    /// <summary>
    /// The lead artist — <c>AlbumArtist ?? Artist</c>, folded to its first credit. That is the web's
    /// <c>artistOf</c>, so the station groups by the same name the library displays.
    /// </summary>
    private static string LeadArtistKey(RadioTrackRow row)
    {
        var credit = string.IsNullOrWhiteSpace(row.AlbumArtist) ? row.Artist : row.AlbumArtist;
        return TitleNormalizer.NormalizeForSearch(ArtistCreditNormalizer.GetPrimaryArtist(credit));
    }

    /// <summary>Every credited artist, so a feature links two tracks that share no lead.</summary>
    private static HashSet<string> CreditKeys(RadioTrackRow row)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in new[] { row.Artists, row.Artist, row.AlbumArtist })
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            foreach (var name in ArtistCreditNormalizer.SplitArtists(source))
            {
                var key = TitleNormalizer.NormalizeForSearch(name);
                if (key.Length > 0) keys.Add(key);
            }
        }
        return keys;
    }

    private static HashSet<string> MbidKeys(RadioTrackRow row)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(row.ArtistMusicBrainzIds)) return keys;
        foreach (var id in row.ArtistMusicBrainzIds.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            keys.Add(id);
        return keys;
    }

    private static HashSet<string> GenreKeys(RadioTrackRow row)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(row.Genre)) return keys;
        foreach (var part in row.Genre.Split([';', '/', ',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var key = TitleNormalizer.NormalizeForSearch(part);
            if (key.Length > 0) keys.Add(key);
        }
        return keys;
    }

    /// <summary>
    /// A stable 0..1 spread for one (seed, candidate) pair. Hand-rolled rather than
    /// <c>HashCode.Combine</c>, which is seeded per process and would hand the same listener a
    /// different station after every restart.
    /// </summary>
    private static double Jitter(int seedId, int candidateId)
    {
        unchecked
        {
            var h = 2166136261u;
            h = (h ^ (uint)seedId) * 16777619u;
            h = (h ^ (uint)candidateId) * 16777619u;
            h ^= h >> 13;
            h *= 0x5bd1e995u;
            h ^= h >> 15;
            return h / (double)uint.MaxValue;
        }
    }
}
