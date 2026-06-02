using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Quality;

/// <summary>Minimal owned-song projection the album dossier is built from.</summary>
public sealed record OwnedSongForGrading(
    int Id, string? Title, string? Artist, int? DiscNumber, int? TrackNumber, int? DurationSeconds, string? MusicBrainzId);

public interface IAlbumGradingDossierFactory
{
    AlbumGradingDossier Build(
        CanonicalAlbum album,
        IReadOnlyList<OwnedSongForGrading> ownedSongs,
        IReadOnlyDictionary<int, SongQualityVerdict> latestSongVerdicts,
        double titleThreshold);
}

/// <summary>
/// Pure projection (no EF/IO) of a reconciled album + the owner's matching songs into the grading
/// dossier. Reuses <see cref="AlbumOwnedTrackMatcher"/> so the owned↔canonical mapping is identical to
/// the tracklist endpoint's.
/// </summary>
public sealed class AlbumGradingDossierFactory : IAlbumGradingDossierFactory
{
    public AlbumGradingDossier Build(
        CanonicalAlbum album,
        IReadOnlyList<OwnedSongForGrading> ownedSongs,
        IReadOnlyDictionary<int, SongQualityVerdict> latestSongVerdicts,
        double titleThreshold)
    {
        var tracks = album.Tracks
            .OrderBy(t => t.DiscNumber)
            .ThenBy(t => t.TrackNumber)
            .ToList();

        var ownedInfo = ownedSongs
            .Select(s => new OwnedTrackInfo(s.Id, s.MusicBrainzId, s.DiscNumber, s.TrackNumber, s.Title))
            .ToList();
        var matched = AlbumOwnedTrackMatcher.Match(tracks, ownedInfo, titleThreshold);
        var matchedSongIds = matched.Values.ToHashSet();
        var matchedTrackIds = matched.Keys.ToHashSet();

        var canonicalTracks = tracks
            .Select(t => new AlbumDossierCanonicalTrack(
                t.DiscNumber, t.TrackNumber, t.Title,
                t.DurationMs is int ms ? ms / 1000 : null,
                t.CorroborationCount, t.IsContested,
                Owned: matchedTrackIds.Contains(t.Id)))
            .ToList();

        var ownedDossier = ownedSongs
            .Select(s => new AlbumDossierOwnedSong(
                s.Title, s.Artist, s.DiscNumber, s.TrackNumber, s.DurationSeconds,
                MatchedToCanonical: matchedSongIds.Contains(s.Id)))
            .ToList();

        var sources = ParseSources(album.SourcesJson);

        var summary = new AlbumDossierMatchSummary(
            OwnedCount: ownedSongs.Count,
            CanonicalCount: tracks.Count,
            OwnedMatchedCount: matchedSongIds.Count,
            OwnedUnmatchedCount: ownedSongs.Count - matchedSongIds.Count,
            CanonicalMatchedCount: matched.Count,
            TitleMatchRate: ownedSongs.Count > 0 ? Math.Round((double)matchedSongIds.Count / ownedSongs.Count, 3) : 0);

        var verdicts = latestSongVerdicts.Values.ToList();
        var rollup = new AlbumDossierSongRollup(
            Graded: verdicts.Count,
            Excellent: verdicts.Count(v => v == SongQualityVerdict.Excellent),
            Good: verdicts.Count(v => v == SongQualityVerdict.Good),
            Questionable: verdicts.Count(v => v == SongQualityVerdict.Questionable),
            Wrong: verdicts.Count(v => v == SongQualityVerdict.Wrong),
            Ungradeable: verdicts.Count(v => v == SongQualityVerdict.Ungradeable));

        return new AlbumGradingDossier(
            CanonicalAlbumId: album.Id,
            LocalAlbum: new AlbumDossierLocal(album.DisplayArtist ?? "", album.DisplayTitle ?? "", ownedDossier),
            Canonical: new AlbumDossierCanonical(
                album.DisplayTitle, album.DisplayArtist, album.Year,
                album.ResolvedTrackCount, album.TrackCountContested, canonicalTracks),
            Sources: sources,
            MatchSummary: summary,
            SongGradeRollup: rollup);
    }

    private static IReadOnlyList<AlbumDossierSource> ParseSources(string? sourcesJson)
    {
        if (string.IsNullOrWhiteSpace(sourcesJson))
            return [];
        try
        {
            var stored = System.Text.Json.JsonSerializer.Deserialize<List<StoredSource>>(sourcesJson);
            return stored is null
                ? []
                : stored.Select(s => new AlbumDossierSource(s.Provider.ToString(), s.AlbumId, s.TrackCount, s.InWinningCluster)).ToList();
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private sealed record StoredSource(EnrichmentProvider Provider, string? AlbumId, int TrackCount, bool InWinningCluster);
}
