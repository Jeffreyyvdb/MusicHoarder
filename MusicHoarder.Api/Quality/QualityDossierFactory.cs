using System.Text.Json;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Quality;

public interface IQualityDossierFactory
{
    SongGradingDossier Build(SongMetadata song, IReadOnlyList<SongMetadataChange> changes);
}

/// <summary>
/// Assembles a <see cref="SongGradingDossier"/> from a song + its provider attempts + change log.
/// Pure projection apart from resolving the destination-path preview, which mirrors what the
/// LibraryBuilder would write so the grader sees the same "WILL WRITE TO" the UI shows.
/// </summary>
public class QualityDossierFactory(IDestinationPathResolver pathResolver) : IQualityDossierFactory
{
    private static readonly JsonSerializerOptions CandidateJson = new(JsonSerializerDefaults.Web);

    public SongGradingDossier Build(SongMetadata song, IReadOnlyList<SongMetadataChange> changes)
    {
        var embedded = song.OriginalMetadataCaptured
            ? new DossierMetadata(
                song.OriginalTitle, song.OriginalArtist, song.OriginalAlbumArtist, song.OriginalAlbum,
                song.OriginalYear, song.OriginalTrackNumber, song.OriginalArtists, song.OriginalIsrc,
                song.OriginalMusicBrainzId, song.OriginalSpotifyId)
            // Before any enrichment ran, the current row still holds the file's own tags.
            : Current(song);

        var attempts = song.ProviderAttempts
            .OrderBy(a => a.AttemptedAtUtc)
            .Select(a => new DossierProviderAttempt(
                a.Provider.ToString(),
                a.Status.ToString(),
                a.AttemptedAtUtc,
                a.Error,
                ParseCandidate(a.MatchedDataJson)))
            .ToList();

        var changeRows = changes
            .OrderBy(c => c.CreatedAtUtc)
            .Select(c => new DossierChange(
                c.FieldName, c.OldValue, c.NewValue, c.Source, c.Confidence,
                Applied: c.AppliedAtUtc != null && c.RevertedAtUtc == null,
                Proposed: c.AppliedAtUtc == null && c.RevertedAtUtc == null,
                c.CreatedAtUtc))
            .ToList();

        return new SongGradingDossier(
            song.Id,
            new DossierFile(
                song.SourcePath, song.FileName, song.Extension, song.FileSizeBytes,
                song.DurationSeconds, song.Bitrate, !string.IsNullOrWhiteSpace(song.Fingerprint),
                song.IndexedAtUtc),
            embedded,
            Current(song),
            new DossierEnrichment(
                song.EnrichmentStatus.ToString(),
                song.MatchedBy,
                song.MatchConfidence,
                ParseWarnings(song.MatchWarnings),
                song.EnrichmentError,
                song.IsManuallyApproved,
                song.IsUnreleased),
            ResolveDestinationPreview(song),
            attempts,
            changeRows,
            song.IsDuplicate || song.DuplicateOfId != null
                ? new DossierDuplicate(song.IsDuplicate, song.DuplicateOfId)
                : null);
    }

    private static DossierMetadata Current(SongMetadata s) => new(
        s.Title, s.Artist, s.AlbumArtist, s.Album, s.Year, s.TrackNumber,
        s.Artists, s.Isrc, s.MusicBrainzId, s.SpotifyId);

    private string? ResolveDestinationPreview(SongMetadata song)
    {
        if (!string.IsNullOrWhiteSpace(song.DestinationPath))
            return song.DestinationPath;
        try
        {
            return pathResolver.ResolvePath(song);
        }
        catch
        {
            // Resolver can throw when required metadata is missing — a legitimate dossier signal,
            // not a failure. The grader treats a null preview as "no committed/derivable path".
            return null;
        }
    }

    private static DossierCandidate? ParseCandidate(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var r = JsonSerializer.Deserialize<EnrichmentProviderResult>(json, CandidateJson);
            if (r is null) return null;
            return new DossierCandidate(
                r.Title, r.Artist, r.AlbumArtist, r.Album, r.Year, r.TrackNumber, r.Isrc,
                r.MusicBrainzId, r.SpotifyId, r.MatchedBy, r.MatchConfidence,
                r.RecommendedStatus.ToString(), r.MatchWarnings ?? []);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ParseWarnings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }
}
