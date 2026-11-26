namespace MusicHoarder.Api.Deezer;

/// <summary>
/// Parsed Deezer track from catalog search / track lookup (no auth).
/// Shaped like <c>SpotifyCatalogTrack</c> so provider scoring is identical; Deezer reports
/// duration in seconds, which the service converts to <see cref="DurationMs"/>.
/// </summary>
public record DeezerCatalogTrack(
    string Id,
    string Title,
    string Artist,
    string AlbumName,
    int? ReleaseYear,
    int? TrackNumber,
    int DurationMs,
    string? Isrc,
    /// <summary>Discrete contributor names (';'-joined), from the full track detail's
    /// <c>contributors</c> array; null on search results and single-artist payloads.</summary>
    string? Artists = null);

/// <summary>A Deezer album with its full tracklist (from <c>GET /album/{id}</c>).</summary>
public record DeezerAlbumDetail(
    string Id,
    string? Title,
    string? Artist,
    int? Year,
    string? CoverUrl,
    IReadOnlyList<DeezerAlbumTrackItem> Tracks);

public record DeezerAlbumTrackItem(
    int DiscNumber,
    int TrackNumber,
    string? Title,
    int DurationMs,
    string? Id,
    /// <summary>The track's own artist — differs from the album's on a compilation or a feature.</summary>
    string? Artist = null);

/// <summary>An album search hit (<c>GET /search/album</c>) carrying enough identity to verify the match.</summary>
public record DeezerAlbumCandidate(string Id, string? Title, string? ArtistName);

/// <summary>
/// An artist search hit (<c>GET /search/artist</c>) with its portraits: <see cref="PictureUrl"/>
/// is sized for grid/list tiles, <see cref="PictureUrlLarge"/> is the largest available (hero
/// views).
/// </summary>
public record DeezerArtistCandidate(
    string? Name,
    string? PictureUrl,
    string? Id = null,
    int Fans = 0,
    int AlbumCount = 0,
    string? PictureUrlLarge = null);

/// <summary>
/// One release on an artist's catalog page (<c>GET /artist/{id}/albums</c>). <see cref="RecordType"/>
/// is Deezer's <c>record_type</c>: <c>album</c>, <c>ep</c>, <c>single</c> or <c>compile</c>.
/// </summary>
public record DeezerArtistRelease(
    string Id,
    string Title,
    string? RecordType,
    DateOnly? ReleaseDate,
    string? CoverUrl,
    bool Explicit);

/// <summary>
/// A playlist as returned by <c>GET /playlist/{id}</c>. <see cref="Checksum"/> is
/// Deezer's tracklist fingerprint (present on the full <c>GET /playlist/{id}</c> only) — used to skip
/// re-syncing an unchanged subscribed playlist.
/// </summary>
public record DeezerPlaylistSummary(
    string Id,
    string Title,
    string? Description,
    string? CoverUrl,
    int TrackCount,
    string? CreatorName,
    string? Checksum);

/// <summary>
/// A lightweight track from a playlist tracklist (<c>GET /playlist/{id}/tracks</c>). Carries no ISRC /
/// release year / track position — those come from <see cref="IDeezerCatalogService.LookupByIdAsync"/>.
/// </summary>
public record DeezerPlaylistTrack(
    string Id,
    string Title,
    string Artist,
    string? Album,
    int DurationMs,
    string? CoverUrl);

/// <summary>
/// A playlist's fetched tracklist plus whether it was paged to completion. <see cref="IsComplete"/> is
/// false when a page fetch failed mid-run (a transient error) or the fetch was capped by a caller-supplied
/// max. Callers that persist a skip-if-unchanged checksum must only advance it on a complete fetch, else
/// the never-fetched tail stays hidden until the upstream playlist changes.
/// </summary>
public record DeezerPlaylistTracksResult(IReadOnlyList<DeezerPlaylistTrack> Tracks, bool IsComplete);
