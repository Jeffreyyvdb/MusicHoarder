using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// The music-video candidates last found for one song — the list "Choose video…" shows — kept so the
/// picker opens on an answer instead of a YouTube search. Written by the background prefetch for
/// every song without a video (<c>MusicVideoCandidatePrefetchService</c>) and by every live search the picker
/// makes; each probe the picker runs writes its verdict back into the row, so a candidate is measured
/// once. Nothing here is downloaded: it is a cache of search results and their probes.
///
/// <para>
/// <see cref="QueryKey"/> is the search the list answers (artist, title, length). A row whose key no
/// longer matches the song — re-enrichment renamed it — is ignored and replaced, never served.
/// </para>
/// </summary>
public class SongVideoCandidateSet
{
    [Key]
    public int Id { get; set; }

    /// <summary>One set per song (unique index).</summary>
    public int SongId { get; set; }
    public SongMetadata Song { get; set; } = null!;

    /// <summary>See <c>MusicVideoCandidateCache.QueryKeyFor</c>.</summary>
    [MaxLength(64)]
    public string QueryKey { get; set; } = string.Empty;

    public DateTime SearchedAtUtc { get; set; }

    /// <summary>The ranked candidates with whatever was measured about them, as JSON.</summary>
    public string CandidatesJson { get; set; } = "[]";

    /// <summary>
    /// Why the last search produced nothing usable (yt-dlp missing, a YouTube error). A set with an
    /// error is never served; it only stops the prefetch from retrying the same song every pass.
    /// </summary>
    [MaxLength(1024)]
    public string? Error { get; set; }

    /// <summary>
    /// When the prefetch searches this song again (<c>MusicVideoSearchSchedule</c>); null means now,
    /// which is what a set stored before the schedule existed holds. Set by every write the cache makes.
    /// </summary>
    public DateTime? NextSearchAtUtc { get; set; }

    /// <summary>Consecutive searches that failed or found nothing; a search that found candidates resets it to 0.</summary>
    public int EmptySearches { get; set; }
}
