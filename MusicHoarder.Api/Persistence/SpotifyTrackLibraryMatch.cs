using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// Cached Spotify track id → local library match result (from background sync or on-demand page enrichment).
/// </summary>
public class SpotifyTrackLibraryMatch
{
    [Key]
    [MaxLength(64)]
    public string SpotifyTrackId { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="Spotify.ComparisonMatchStatus"/> as int for EF storage.
    /// </summary>
    public int MatchStatus { get; set; }

    public int? MatchedSongId { get; set; }

    public double? MatchConfidence { get; set; }

    [MaxLength(512)]
    public string? MatchedTitle { get; set; }

    [MaxLength(512)]
    public string? MatchedArtist { get; set; }

    [MaxLength(64)]
    public string? MatchedEnrichmentStatus { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>
    /// "liked_sync" when written by periodic liked-songs sync; "api_page" when written on-demand for playlist / first page views.
    /// </summary>
    [MaxLength(32)]
    public string Source { get; set; } = "api_page";

    [MaxLength(512)]
    public string? SpotifyTitle { get; set; }

    [MaxLength(512)]
    public string? SpotifyArtist { get; set; }

    [MaxLength(512)]
    public string? SpotifyAlbum { get; set; }

    public int? SpotifyDurationMs { get; set; }

    public DateTime? SpotifyAddedAtUtc { get; set; }
}
