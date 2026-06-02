namespace MusicHoarder.Api.Persistence;

/// <summary>
/// One reconciled canonical track on a <see cref="CanonicalAlbum"/>. Owned songs are matched against
/// these rows by recording MBID, then (disc, track) position, then fuzzy title.
/// </summary>
public class CanonicalAlbumTrack
{
    public int Id { get; set; }

    public int CanonicalAlbumId { get; set; }
    public CanonicalAlbum CanonicalAlbum { get; set; } = null!;

    public int DiscNumber { get; set; } = 1;
    public int TrackNumber { get; set; }

    public string? Title { get; set; }
    public int? DurationMs { get; set; }

    /// <summary>The track's MusicBrainz recording MBID (strongest key for matching an owned song).</summary>
    public string? MusicBrainzRecordingId { get; set; }

    /// <summary>Comma-separated provider names that corroborate this track (e.g. "MusicBrainzWeb,Deezer").</summary>
    public string? CorroboratingProviders { get; set; }

    /// <summary>Number of distinct providers that corroborate this track.</summary>
    public int CorroborationCount { get; set; }

    /// <summary>True when not every provider in the winning cluster backs this track (bonus / disputed).</summary>
    public bool IsContested { get; set; }
}
