using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// Singleton row holding the subset of <see cref="Options.MusicEnricherOptions"/> (and
/// <see cref="Options.QualityGradingOptions"/>) values that can be tweaked at runtime from the
/// Settings UI. Any field left null falls back to the bound configuration value, so user-secrets /
/// appsettings.json continue to act as defaults.
/// </summary>
public class RuntimeSettings
{
    [Key]
    public int Id { get; set; }

    public bool? EnableAcoustIdProvider { get; set; }
    public bool? EnableMusicBrainzWebProvider { get; set; }
    public bool? EnableSpotifyApiProvider { get; set; }
    public bool? EnableTrackerProvider { get; set; }
    public bool? EnableDeezerProvider { get; set; }
    public bool? EnableAppleMusicProvider { get; set; }

    /// <summary>Overlays <see cref="Options.QualityGradingOptions.Enabled"/> — the AI quality grader master switch.</summary>
    public bool? QualityGradingEnabled { get; set; }

    /// <summary>
    /// Set once by the one-time cover-art backfill (see <c>CoverArtBackfillBackgroundService</c>) after it
    /// has populated <c>HasCoverArt</c> and written destination album covers for the pre-existing library.
    /// Null until then; its presence is the marker that stops the backfill from re-running.
    /// </summary>
    public DateTime? CoverArtBackfillCompletedAtUtc { get; set; }

    /// <summary>
    /// Set once by the one-time library-write baseline (see <c>LibraryWriteBaselineBackgroundService</c>)
    /// after it has seeded <see cref="SongMetadata.LastWrittenTagsJson"/> for the pre-existing built
    /// library, so the first re-tag after the History feature shipped diffs against the tracks' actual
    /// current tags rather than the source-original baseline. Null until then; its presence stops the
    /// seed from re-running.
    /// </summary>
    public DateTime? LibraryWriteBaselineCompletedAtUtc { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }
}
