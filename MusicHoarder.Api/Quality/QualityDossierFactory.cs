using System.Text.Json;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
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
/// <para>
/// The change log is the one unbounded input here — a song that is re-enriched and healed
/// repeatedly accumulates thousands of <see cref="SongMetadataChange"/> rows — so it is capped
/// (newest first) and the whole dossier is re-checked against
/// <see cref="QualityGradingOptions.MaxDossierChars"/>. Without that cap the prompt can exceed the
/// model's context window and every grading call fails with HTTP 400.
/// </para>
/// </summary>
public class QualityDossierFactory(
    IDestinationPathResolver pathResolver,
    IOptionsMonitor<QualityGradingOptions> options) : IQualityDossierFactory
{
    private static readonly JsonSerializerOptions CandidateJson = new(JsonSerializerDefaults.Web);

    public SongGradingDossier Build(SongMetadata song, IReadOnlyList<SongMetadataChange> changes)
    {
        var opts = options.CurrentValue;

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
                c.FieldName, Elide(c.OldValue, opts.MaxChangeValueChars), Elide(c.NewValue, opts.MaxChangeValueChars),
                c.Source, c.Confidence,
                Applied: c.AppliedAtUtc != null && c.RevertedAtUtc == null,
                Proposed: c.AppliedAtUtc == null && c.RevertedAtUtc == null,
                c.CreatedAtUtc))
            .ToList();

        var full = new SongGradingDossier(
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

        return Fit(full, opts);
    }

    /// <summary>
    /// Sheds change-log entries (oldest first) until the serialized dossier fits
    /// <see cref="QualityGradingOptions.MaxDossierChars"/>: first down to
    /// <see cref="QualityGradingOptions.MaxChangeLogEntries"/>, then halving, then empty. Everything
    /// else in the dossier is fixed-size, so an empty change log always fits.
    /// </summary>
    private static SongGradingDossier Fit(SongGradingDossier full, QualityGradingOptions opts)
    {
        var total = full.ChangeLog.Count;
        var maxChars = Math.Max(1, opts.MaxDossierChars);

        var candidate = full;
        foreach (var keep in KeepSteps(total, Math.Max(0, opts.MaxChangeLogEntries)))
        {
            candidate = WithChangeLog(full, keep, total);
            if (QualityGradingPrompt.SerializeDossier(candidate).Length <= maxChars)
                return candidate;
        }

        return candidate; // change log is already empty — nothing left to shed
    }

    /// <summary>Successively smaller change-log sizes to try: the configured cap, then halves, then 0.</summary>
    private static IEnumerable<int> KeepSteps(int total, int cap)
    {
        var keep = Math.Min(total, cap);
        while (keep > 0)
        {
            yield return keep;
            keep /= 2;
        }
        yield return 0;
    }

    private static SongGradingDossier WithChangeLog(SongGradingDossier full, int keep, int total)
    {
        if (keep >= total)
            return full;

        // Keep the newest entries: they describe the decision that produced the current metadata.
        var kept = full.ChangeLog.Skip(total - keep).ToList();
        return full with
        {
            ChangeLog = kept,
            Truncation = new DossierTruncation(
                total, keep,
                $"changeLog holds the {keep} most recent of {total} entries; older entries were omitted to fit the model's context window"),
        };
    }

    private static string? Elide(string? value, int maxChars) =>
        value is not null && value.Length > maxChars ? value[..maxChars] + "…" : value;

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
