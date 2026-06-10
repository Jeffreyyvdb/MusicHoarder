using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Matching;

/// <summary>Minimal owned-song shape needed to match it to a canonical album track.</summary>
public sealed record OwnedTrackInfo(int Id, string? MusicBrainzId, int? DiscNumber, int? TrackNumber, string? Title);

/// <summary>
/// Maps each canonical album track to the owned song that represents it, in strength order across all
/// tracks: every recording-MBID match resolves before any (disc, track) position match, and all
/// position matches before any fuzzy-title match. Each owned song is consumed at most once. Shared by
/// the album tracklist endpoint and the album quality dossier so both compute ownership identically.
/// </summary>
public static class AlbumOwnedTrackMatcher
{
    /// <param name="usePositionPhase">
    /// When true (the default, used by the tracklist endpoint and quality dossier) the middle phase
    /// matches by (disc, track) position. Callers that intend to <em>rewrite</em> track numbers from
    /// the canonical tracklist must pass false: the owned positions may be exactly what's corrupt
    /// (each track enriched against a different release), so position-matching would link the wrong
    /// files. With it off, only recording-MBID then fuzzy-title are used.
    /// </param>
    public static Dictionary<int, int> Match(
        IReadOnlyList<CanonicalAlbumTrack> tracks, IReadOnlyList<OwnedTrackInfo> ownedSongs, double titleThreshold,
        bool usePositionPhase = true)
    {
        var matched = new Dictionary<int, int>();
        var consumed = new HashSet<int>();

        foreach (var t in tracks)
        {
            if (string.IsNullOrEmpty(t.MusicBrainzRecordingId)) continue;
            var song = ownedSongs.FirstOrDefault(s => !consumed.Contains(s.Id) && s.MusicBrainzId == t.MusicBrainzRecordingId);
            if (song is not null) { matched[t.Id] = song.Id; consumed.Add(song.Id); }
        }

        if (usePositionPhase)
        {
            foreach (var t in tracks)
            {
                if (matched.ContainsKey(t.Id)) continue;
                var song = ownedSongs.FirstOrDefault(s =>
                    !consumed.Contains(s.Id) && (s.DiscNumber ?? 1) == t.DiscNumber && s.TrackNumber == t.TrackNumber);
                if (song is not null) { matched[t.Id] = song.Id; consumed.Add(song.Id); }
            }
        }

        foreach (var t in tracks)
        {
            if (matched.ContainsKey(t.Id) || string.IsNullOrWhiteSpace(t.Title)) continue;
            OwnedTrackInfo? best = null;
            double bestScore = 0;
            foreach (var s in ownedSongs)
            {
                if (consumed.Contains(s.Id)) continue;
                var score = FuzzyTextMatch.Ratio(t.Title, s.Title) ?? 0;
                if (score > bestScore) { bestScore = score; best = s; }
            }

            if (best is not null && bestScore >= titleThreshold) { matched[t.Id] = best.Id; consumed.Add(best.Id); }
        }

        return matched;
    }
}
