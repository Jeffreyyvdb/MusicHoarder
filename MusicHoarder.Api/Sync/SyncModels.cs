using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Sync;

/// <summary>
/// Identity + quality probe: "do you have this track, and at what quality?" Identity travels as the
/// portable ladder inputs (never a database id). Quality travels as extension+bitrate — each side
/// scores with its own <see cref="MusicHoarder.Api.Audio.AudioQuality"/>, so a scoring-formula
/// change never needs a lockstep deploy.
/// </summary>
public sealed record SyncCheckRequest(
    string? Fingerprint,
    string? AcoustIdTrackId,
    string? MusicBrainzId,
    string? Artist,
    string? Title,
    int? DurationMs,
    string Extension,
    int? Bitrate);

public enum SyncVerdict
{
    NotPresent = 0,
    PresentLowerQuality = 1,
    PresentSameOrBetter = 2,
}

public sealed record SyncCheckResponse(
    SyncVerdict Verdict,
    int? SongId,
    int? RemoteQualityScore,
    string? MatchedBy);

public enum SyncUploadOutcome
{
    Created = 0,
    Replaced = 1,
    SkippedSameOrBetter = 2,
    SkippedIdentical = 3,
}

public sealed record SyncUploadResponse(
    SyncUploadOutcome Outcome,
    int SongId,
    int QualityScore);

/// <summary>
/// Everything the receiving instance needs to create/refresh a track row WITHOUT re-running its own
/// enrichment: the pushing instance's consensus is authoritative. Field names mirror
/// <see cref="SongMetadata"/> so the mapping stays greppable.
/// </summary>
public sealed record SyncTrackPayload(
    // File facts (of the uploaded file — the pusher's built destination artifact)
    string FileName,
    string Extension,
    long FileSizeBytes,
    int? Bitrate,
    int? DurationSeconds,
    int? DurationMs,
    string? Fingerprint,
    // Portable identity
    string? Isrc,
    string? MusicBrainzId,
    string? MusicBrainzReleaseId,
    string? MusicBrainzReleaseGroupId,
    string? SpotifyId,
    string? AcoustIdTrackId,
    string? LrclibId,
    // Tags
    string? Artist,
    string? AlbumArtist,
    string? Album,
    string? Title,
    int? Year,
    int? TrackNumber,
    int? DiscNumber,
    int? TotalDiscs,
    int? TotalTracks,
    string? Artists,
    string? ArtistMusicBrainzIds,
    string? AlbumArtistMusicBrainzId,
    bool IsCompilation,
    string? ReleaseTypePrimary,
    string? ReleaseTypes,
    bool IsUnreleased,
    // Enrichment facts
    string? MatchedBy,
    double? MatchConfidence,
    // Lyrics
    string? PlainLyrics,
    string? SyncedLyrics,
    bool? IsInstrumental,
    LyricsStatus LyricsStatus);
