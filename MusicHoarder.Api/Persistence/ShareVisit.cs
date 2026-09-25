using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

public enum ShareVisitKind
{
    /// <summary>Someone opened the share page (or the Android share viewer).</summary>
    View = 0,

    /// <summary>Someone started playing a track from the share.</summary>
    Play = 1,
}

/// <summary>
/// One open of — or play from — a <see cref="SongShare"/> link, reported by the recipient's
/// browser (a beacon from the share page) or the Android share viewer. What the owner's Share links
/// page counts.
///
/// <para>
/// Deliberately thin, and deliberately free of anything that identifies a person: no IP address, no
/// user agent, no account. <see cref="VisitorKey"/> is a keyed hash of (share, IP, user agent) under
/// a salt that rotates every UTC day and is never persisted (see
/// <see cref="Sharing.ShareVisitTracker"/>), so the same visitor is recognisable within a day — for
/// de-duplicating reloads and counting visitors — and unlinkable across days.
/// </para>
/// </summary>
public class ShareVisit
{
    public long Id { get; set; }

    public int ShareId { get; set; }

    public SongShare? Share { get; set; }

    /// <summary>The share's owner, copied at write time so the tenancy query filter needs no join.</summary>
    public Guid OwnerUserId { get; set; }

    public ShareVisitKind Kind { get; set; }

    /// <summary>The track that was played (<see cref="ShareVisitKind.Play"/> only).</summary>
    public int? SongId { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    /// <summary>Daily-salted hash identifying a visitor within one UTC day; see the class remarks.</summary>
    [MaxLength(32)]
    public required string VisitorKey { get; set; }

    /// <summary>
    /// Where the visitor came from: a known app or site ("TikTok", "Instagram") or the referring
    /// host, derived from the referrer and in-app-browser markers. Null when nothing said.
    /// </summary>
    [MaxLength(64)]
    public string? Source { get; set; }
}
