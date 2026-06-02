namespace MusicHoarder.Api.Persistence;

/// <summary>
/// A canonical album reconciled from every enabled metadata provider (MusicBrainz, Spotify, Deezer,
/// Apple), keyed by a provider-agnostic identity (<see cref="ArtistKey"/> + <see cref="AlbumKey"/>,
/// both normalized via <c>TitleNormalizer</c>). Catalog/reference data — <em>not</em> user-scoped —
/// holding the reconciled full tracklist so the album view can show every real track and grey out
/// the ones the user is missing. Populated by <c>CanonicalAlbumFetchService</c> once an album lands.
/// </summary>
public class CanonicalAlbum
{
    public int Id { get; set; }

    /// <summary>Normalized album-artist key (one half of the unique album identity).</summary>
    public string ArtistKey { get; set; } = string.Empty;

    /// <summary>Normalized album-title key (the other half of the unique album identity).</summary>
    public string AlbumKey { get; set; } = string.Empty;

    public string? DisplayTitle { get; set; }
    public string? DisplayArtist { get; set; }
    public int? Year { get; set; }
    public string? CoverArtUrl { get; set; }

    /// <summary>Consensus (most-voted) track count among the agreeing providers.</summary>
    public int ResolvedTrackCount { get; set; }

    /// <summary>True when the agreeing providers disagree on how many tracks the album has.</summary>
    public bool TrackCountContested { get; set; }

    /// <summary>JSON: per-provider album id + track count + whether it won the cluster (for the UI).</summary>
    public string? SourcesJson { get; set; }

    public CanonicalAlbumStatus Status { get; set; } = CanonicalAlbumStatus.Pending;

    public DateTime? FetchedAtUtc { get; set; }
    public DateTime? NextRetryAfterUtc { get; set; }

    public ICollection<CanonicalAlbumTrack> Tracks { get; set; } = new List<CanonicalAlbumTrack>();
}

public enum CanonicalAlbumStatus
{
    Pending = 0,
    Fetched = 1,
    NotFound = 2,
    Failed = 3,
}
