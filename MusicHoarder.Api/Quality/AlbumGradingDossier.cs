namespace MusicHoarder.Api.Quality;

/// <summary>
/// Everything the grader needs to judge whether a reconciled album correctly corresponds to the
/// user's local album: the local album (its owned songs as ground truth), the reconciled canonical
/// album + tracks, the contributing providers, an owned↔canonical match summary, and a rollup of the
/// owned songs' existing per-song AI grades. Fed to the LLM and emitted by the export endpoint.
/// </summary>
public record AlbumGradingDossier(
    int CanonicalAlbumId,
    AlbumDossierLocal LocalAlbum,
    AlbumDossierCanonical Canonical,
    IReadOnlyList<AlbumDossierSource> Sources,
    AlbumDossierMatchSummary MatchSummary,
    AlbumDossierSongRollup SongGradeRollup);

public record AlbumDossierLocal(
    string Artist,
    string Album,
    IReadOnlyList<AlbumDossierOwnedSong> OwnedSongs);

public record AlbumDossierOwnedSong(
    string? Title,
    string? Artist,
    int? DiscNumber,
    int? TrackNumber,
    int? DurationSeconds,
    /// <summary>Whether this owned song mapped to a canonical track (false = present locally but absent from the reconciled album — a wrong-album signal).</summary>
    bool MatchedToCanonical);

public record AlbumDossierCanonical(
    string? Title,
    string? Artist,
    int? Year,
    int ResolvedTrackCount,
    bool TrackCountContested,
    IReadOnlyList<AlbumDossierCanonicalTrack> Tracks);

public record AlbumDossierCanonicalTrack(
    int DiscNumber,
    int TrackNumber,
    string? Title,
    int? DurationSeconds,
    int CorroborationCount,
    bool IsContested,
    /// <summary>Whether the user owns this canonical track.</summary>
    bool Owned);

public record AlbumDossierSource(
    string Provider,
    string? AlbumId,
    int TrackCount,
    bool InWinningCluster);

public record AlbumDossierMatchSummary(
    int OwnedCount,
    int CanonicalCount,
    int OwnedMatchedCount,
    /// <summary>Owned songs that map to no canonical track — the strongest "wrong album" signal.</summary>
    int OwnedUnmatchedCount,
    int CanonicalMatchedCount,
    /// <summary>Fraction of owned songs that matched a canonical track (0–1).</summary>
    double TitleMatchRate);

/// <summary>Counts of the owned songs' latest per-song AI verdicts (the song-grade rollup signal).</summary>
public record AlbumDossierSongRollup(
    int Graded,
    int Excellent,
    int Good,
    int Questionable,
    int Wrong,
    int Ungradeable);
