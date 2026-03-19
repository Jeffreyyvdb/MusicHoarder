using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

public record EnrichmentMatchData(
    string? Artist,
    string? AlbumArtist,
    string? Title,
    string? MusicBrainzId,
    string MatchedBy,
    double AdjustedScore,
    string? WarningsJson,
    EnrichmentStatus RecommendedStatus);

public class SongMetadata
{
    private const int MaxErrorLength = 1024;

    [Key]
    public int Id { get; set; }
    public required string SourcePath { get; set; }
    public required long FileSizeBytes { get; set; }
    public required string FileName { get; set; }
    public required string Extension { get; set; }
    public required DateTime LastModifiedUtc { get; set; }
    public string? Artist { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Album { get; set; }
    public string? Title { get; set; }
    public int? Year { get; set; }
    public int? TrackNumber { get; set; }
    public int? DurationSeconds { get; set; }
    public int? DurationMs { get; set; }
    public required DateTime IndexedAtUtc { get; set; }
    public string? Fingerprint { get; set; }
    public int? Bitrate { get; set; }

    // --- Duplicate detection ---

    public bool IsDuplicate { get; set; }
    public int? DuplicateOfId { get; set; }
    public SongMetadata? DuplicateOf { get; set; }

    public string? Isrc { get; set; }
    public string? MusicBrainzId { get; set; }
    public string? SpotifyId { get; set; }
    public EnrichmentStatus EnrichmentStatus { get; set; } = EnrichmentStatus.Pending;
    public string? MatchedBy { get; set; }
    public double? MatchConfidence { get; set; }
    public string? MatchWarnings { get; set; }
    public DateTime? EnrichedAtUtc { get; set; }
    public DateTime? EnrichmentLastAttemptedAtUtc { get; set; }
    public string? EnrichmentError { get; set; }

    public bool OriginalMetadataCaptured { get; set; }
    public string? OriginalArtist { get; set; }
    public string? OriginalAlbumArtist { get; set; }
    public string? OriginalAlbum { get; set; }
    public string? OriginalTitle { get; set; }
    public int? OriginalYear { get; set; }
    public int? OriginalTrackNumber { get; set; }
    public string? OriginalIsrc { get; set; }
    public string? OriginalMusicBrainzId { get; set; }
    public string? OriginalSpotifyId { get; set; }
    public DateTime? OriginalMetadataCapturedAtUtc { get; set; }
    public bool IsUnreleased { get; set; }
    public LibraryBuildStatus LibraryBuildStatus { get; set; } = LibraryBuildStatus.Pending;
    public DateTime? LibraryBuiltAtUtc { get; set; }
    public DateTime? LibraryBuildLastAttemptedAtUtc { get; set; }
    public string? LibraryBuildError { get; set; }
    public string? DestinationPath { get; set; }
    public string? PreviousDestinationPath { get; set; }

    public DateTime? DeletedAtUtc { get; set; }

    // --- Lyrics ---

    public string? PlainLyrics { get; set; }
    public string? SyncedLyrics { get; set; }
    public bool? IsInstrumental { get; set; }
    public LyricsStatus LyricsStatus { get; set; } = LyricsStatus.NotFetched;

    // --- Guard properties ---

    public bool IsDeleted => DeletedAtUtc.HasValue;

    public bool IsReadyForEnrichment =>
        !IsDeleted
        && !string.IsNullOrWhiteSpace(Fingerprint)
        && DurationSeconds is not null
        && EnrichmentStatus == EnrichmentStatus.Pending;

    public bool IsReadyForBuild =>
        !IsDeleted
        && !IsDuplicate
        && EnrichmentStatus == EnrichmentStatus.Matched
        && LibraryBuildStatus != LibraryBuildStatus.Done;

    public string TrackLabel
    {
        get
        {
            var artist = string.IsNullOrWhiteSpace(Artist) ? "<unknown-artist>" : Artist;
            var title = string.IsNullOrWhiteSpace(Title) ? "<unknown-title>" : Title;
            return $"{artist} - {title} [{FileName}]";
        }
    }

    // --- Enrichment lifecycle ---

    public void RecordEnrichmentAttempt()
    {
        EnrichmentLastAttemptedAtUtc = DateTime.UtcNow;
    }

    public void CaptureOriginalMetadata()
    {
        if (OriginalMetadataCaptured) return;

        OriginalMetadataCaptured = true;
        OriginalArtist = Artist;
        OriginalAlbumArtist = AlbumArtist;
        OriginalAlbum = Album;
        OriginalTitle = Title;
        OriginalYear = Year;
        OriginalTrackNumber = TrackNumber;
        OriginalIsrc = Isrc;
        OriginalMusicBrainzId = MusicBrainzId;
        OriginalSpotifyId = SpotifyId;
        OriginalMetadataCapturedAtUtc = DateTime.UtcNow;
    }

    public void RestoreOriginalMetadata()
    {
        if (!OriginalMetadataCaptured) return;

        Artist = OriginalArtist;
        AlbumArtist = OriginalAlbumArtist;
        Album = OriginalAlbum;
        Title = OriginalTitle;
        Year = OriginalYear;
        TrackNumber = OriginalTrackNumber;
        Isrc = OriginalIsrc;
        MusicBrainzId = OriginalMusicBrainzId;
        SpotifyId = OriginalSpotifyId;
    }

    public void ApplyEnrichmentMatch(EnrichmentMatchData match)
    {
        CaptureOriginalMetadata();

        Artist = string.IsNullOrWhiteSpace(match.Artist) ? Artist : match.Artist;
        AlbumArtist = string.IsNullOrWhiteSpace(match.AlbumArtist) ? AlbumArtist : match.AlbumArtist;
        Title = string.IsNullOrWhiteSpace(match.Title) ? Title : match.Title;
        MusicBrainzId = match.MusicBrainzId;
        MatchedBy = match.MatchedBy;
        MatchConfidence = match.AdjustedScore;
        MatchWarnings = match.WarningsJson;
        EnrichmentStatus = match.RecommendedStatus;
        EnrichedAtUtc = DateTime.UtcNow;
        EnrichmentError = null;
    }

    public void MarkEnrichmentNeedsReview(string reason)
    {
        var now = DateTime.UtcNow;
        EnrichmentStatus = EnrichmentStatus.NeedsReview;
        EnrichmentLastAttemptedAtUtc = now;
        EnrichedAtUtc = now;
        EnrichmentError = reason;
        MatchedBy = null;
        MatchConfidence = null;
        MatchWarnings = null;
    }

    public void MarkEnrichmentFailed(string error)
    {
        var now = DateTime.UtcNow;
        EnrichmentStatus = EnrichmentStatus.Failed;
        EnrichmentError = Truncate(error, MaxErrorLength);
        EnrichmentLastAttemptedAtUtc = now;
        EnrichedAtUtc = now;
    }

    public void ResetEnrichment(bool restoreOriginal = true)
    {
        if (restoreOriginal)
            RestoreOriginalMetadata();

        EnrichmentStatus = EnrichmentStatus.Pending;
        MatchedBy = null;
        MatchConfidence = null;
        MatchWarnings = null;
        EnrichedAtUtc = null;
        EnrichmentLastAttemptedAtUtc = null;
        EnrichmentError = null;

        // Reset lyrics so the next enrichment cycle re-fetches them.
        // This allows re-enriching to pick up lyrics that previously failed or weren't found.
        ResetLyrics();
    }

    // --- Duplicate detection lifecycle ---

    public void MarkAsDuplicate(int duplicateOfId)
    {
        IsDuplicate = true;
        DuplicateOfId = duplicateOfId;
    }

    public void ClearDuplicate()
    {
        IsDuplicate = false;
        DuplicateOfId = null;
    }

    // --- Library build lifecycle ---

    public void MarkCopied()
    {
        LibraryBuildStatus = LibraryBuildStatus.Copied;
        LibraryBuildError = null;
    }

    public void MarkTagged()
    {
        LibraryBuildStatus = LibraryBuildStatus.Tagged;
    }

    public void MarkBuildDone(string destinationPath)
    {
        LibraryBuildStatus = LibraryBuildStatus.Done;
        LibraryBuiltAtUtc = DateTime.UtcNow;
        LibraryBuildError = null;
        DestinationPath = destinationPath;
        PreviousDestinationPath = null;
    }

    public void MarkBuildFailed(string error)
    {
        LibraryBuildStatus = LibraryBuildStatus.Failed;
        LibraryBuildError = Truncate(error, MaxErrorLength);
        LibraryBuiltAtUtc = null;
        LibraryBuildLastAttemptedAtUtc = DateTime.UtcNow;
    }

    public void ResetLibraryBuild()
    {
        LibraryBuildStatus = LibraryBuildStatus.Pending;
        LibraryBuiltAtUtc = null;
        LibraryBuildLastAttemptedAtUtc = null;
        LibraryBuildError = null;
        PreviousDestinationPath = DestinationPath;
        DestinationPath = null;
    }

    // --- Lyrics lifecycle ---

    public bool IsReadyForLyricsFetch =>
        !IsDeleted
        && (EnrichmentStatus == EnrichmentStatus.Matched || EnrichmentStatus == EnrichmentStatus.NeedsReview)
        && LyricsStatus == LyricsStatus.NotFetched
        && !string.IsNullOrWhiteSpace(Title)
        && !string.IsNullOrWhiteSpace(Artist);

    public void ApplyLyricsResult(string? syncedLyrics, string? plainLyrics, bool instrumental)
    {
        IsInstrumental = instrumental;
        if (instrumental)
        {
            LyricsStatus = LyricsStatus.Instrumental;
            SyncedLyrics = null;
            PlainLyrics = null;
            return;
        }

        SyncedLyrics = string.IsNullOrWhiteSpace(syncedLyrics) ? null : syncedLyrics;
        PlainLyrics = string.IsNullOrWhiteSpace(plainLyrics) ? null : plainLyrics;

        if (SyncedLyrics is null && PlainLyrics is null)
        {
            LyricsStatus = LyricsStatus.NotFound;
        }
        else
        {
            LyricsStatus = LyricsStatus.Fetched;
        }
    }

    public void MarkLyricsNotFound()
    {
        LyricsStatus = LyricsStatus.NotFound;
        SyncedLyrics = null;
        PlainLyrics = null;
    }

    public void MarkLyricsFailed()
    {
        LyricsStatus = LyricsStatus.Failed;
    }

    public void ResetLyrics()
    {
        LyricsStatus = LyricsStatus.NotFetched;
        SyncedLyrics = null;
        PlainLyrics = null;
        IsInstrumental = null;
    }

    // --- Soft delete ---

    public void SoftDelete()
    {
        DeletedAtUtc = DateTime.UtcNow;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}

public enum EnrichmentStatus
{
    Pending = 0,
    Matched = 1,
    NeedsReview = 2,
    Failed = 3,
}

public enum LibraryBuildStatus
{
    Pending = 0,
    Copied = 1,
    Tagged = 2,
    Done = 3,
    Failed = 4,
}

public enum LyricsStatus
{
    NotFetched = 0,
    Fetched = 1,
    Instrumental = 2,
    NotFound = 3,
    Failed = 4,
}
