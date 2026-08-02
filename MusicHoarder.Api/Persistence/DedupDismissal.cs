namespace MusicHoarder.Api.Persistence;

public enum DedupDismissalKind
{
    /// <summary>Two artist-name spellings the user declared NOT the same artist.</summary>
    ArtistPair = 0,

    /// <summary>Two album titles (under one artist) the user declared NOT the same album.</summary>
    AlbumPair = 1,
}

/// <summary>
/// A user's "these are not duplicates" decision for artist/album pairs. Key-addressed (normalized
/// keys, ordinal-ordered <see cref="KeyLow"/> &lt; <see cref="KeyHigh"/>) because no artist/album
/// entity exists — song-pair dismissals live on <see cref="SongDuplicateLink.Status"/> instead.
/// Detection re-runs exclude dismissed pairs forever.
/// </summary>
public class DedupDismissal
{
    public int Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public DedupDismissalKind Kind { get; set; }

    /// <summary>For <see cref="DedupDismissalKind.AlbumPair"/>: the shared normalized artist key the
    /// two albums live under; empty for artist pairs.</summary>
    public string ScopeKey { get; set; } = string.Empty;

    public required string KeyLow { get; set; }
    public required string KeyHigh { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
