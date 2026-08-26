using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Contracts;

/// <summary>
/// The ONLY shape a caller receives for a song it does not own. Every field here is a deliberate
/// decision to publish something to someone else's account.
///
/// <para>
/// This is a real record rather than an anonymous type on purpose, and it is a separate type from
/// the owner's row rather than one type with a <c>Redact()</c> pass. Redaction by blanklist fails
/// open: a column added to <see cref="SongMetadata"/> next year would be published to every
/// grantee because nobody remembered to blank it. Here the default is exclusion — a new column
/// reaches a grantee only if somebody types its name into this file.
/// </para>
///
/// <para>
/// Withheld on purpose, and why: filesystem paths (the owner's disk layout and library naming),
/// pipeline internals (<c>EnrichmentError</c>, <c>LibraryBuildError</c>, <c>MatchWarnings</c>,
/// <c>Fingerprint</c>, the <c>Original*</c> set), and — most sensitive — <c>SpotifyAddedAtUtc</c>
/// and <c>SpotifyLikedAtUtc</c>, which are the owner's personal Spotify save history and have
/// nothing to do with what was shared.
/// </para>
///
/// <para>
/// The field list is pinned by <c>SharedProjectionSurfaceTests</c>. Widening it fails CI unless the
/// test is updated in the same change, which is the point.
/// </para>
/// </summary>
public sealed record SharedSongRowDto
{
    public required int Id { get; init; }

    /// <summary>
    /// Always empty. The frontend's <c>ApiSong</c> still requires the key, and the owner's real
    /// path is none of a grantee's business. Drop this once the client makes the field optional.
    /// </summary>
    public string SourcePath { get; init; } = "";

    public required string FileName { get; init; }
    public required string Extension { get; init; }
    public required long FileSizeBytes { get; init; }

    public string? Artist { get; init; }
    public string? Artists { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }
    public string? Title { get; init; }
    public int? Year { get; init; }
    public int? TrackNumber { get; init; }
    public int? DiscNumber { get; init; }
    public int? DurationSeconds { get; init; }
    public int? DurationMs { get; init; }
    public int? Bitrate { get; init; }

    public string? Genre { get; init; }
    public string? ReleaseDate { get; init; }
    public string? OriginalReleaseDate { get; init; }
    public string? Label { get; init; }

    // Public catalog identifiers, so the metadata panel is not a wall of dashes. These name a
    // recording in a public database; they are not owner or pipeline state.
    public string? MusicBrainzId { get; init; }
    public string? MusicBrainzReleaseId { get; init; }
    public string? Isrc { get; init; }
    public string? SpotifyId { get; init; }

    public bool HasCoverArt { get; init; }
    public bool HasSyncedLyrics { get; init; }
    public bool HasPlainLyrics { get; init; }
    public bool IsInstrumental { get; init; }
    public bool HasMusicVideo { get; init; }

    public DateTime IndexedAtUtc { get; init; }
    public DateTime? AcquiredAtUtc { get; init; }

    /// <summary>
    /// Server-authoritative "is this playable from the built library". Granted rows carry no
    /// <c>DestinationPath</c>, so a client cannot derive it — and both clients previously guessed,
    /// which is the whole reason the web app needed a global shared-library mode flag.
    /// </summary>
    public bool IsBuilt { get; init; }

    /// <summary>
    /// Who shared this. Null is impossible on this type (it only exists for granted rows) but the
    /// wire contract is "absent means the caller owns the row", so owner rows simply never carry
    /// the field. Pairs with the response's <c>grantors</c> lookup for the display name.
    /// </summary>
    public required Guid SharedByUserId { get; init; }

    // The CALLER's own listening state, read from their UserSongState row — never the owner's
    // like/play columns. Projected under the same key names the owner rows use so liked and
    // recently-played work identically on both.
    public DateTime? LikedAtUtc { get; init; }
    public int PlayCount { get; init; }
    public DateTime? LastPlayedAtUtc { get; init; }

    public static SharedSongRowDto From(
        SongMetadata song,
        Guid sharedByUserId,
        bool hasMusicVideo,
        UserSongState? state) => new()
        {
            Id = song.Id,
            FileName = song.FileName,
            Extension = song.Extension,
            FileSizeBytes = song.FileSizeBytes,
            Artist = song.Artist,
            Artists = song.Artists,
            AlbumArtist = song.AlbumArtist,
            Album = song.Album,
            Title = song.Title,
            Year = song.Year,
            TrackNumber = song.TrackNumber,
            DiscNumber = song.DiscNumber,
            DurationSeconds = song.DurationSeconds,
            DurationMs = song.DurationMs,
            Bitrate = song.Bitrate,
            Genre = song.Genre,
            ReleaseDate = song.ReleaseDate,
            OriginalReleaseDate = song.OriginalReleaseDate,
            Label = song.Label,
            MusicBrainzId = song.MusicBrainzId,
            MusicBrainzReleaseId = song.MusicBrainzReleaseId,
            Isrc = song.Isrc,
            SpotifyId = song.SpotifyId,
            HasCoverArt = song.HasCoverArt,
            HasSyncedLyrics = !string.IsNullOrWhiteSpace(song.DisplaySyncedLyrics),
            HasPlainLyrics = !string.IsNullOrWhiteSpace(song.DisplayPlainLyrics),
            IsInstrumental = song.IsInstrumental == true,
            HasMusicVideo = hasMusicVideo,
            IndexedAtUtc = song.IndexedAtUtc,
            AcquiredAtUtc = song.AcquiredAtUtc,
            IsBuilt = song.LibraryBuildStatus == LibraryBuildStatus.Done
                && !string.IsNullOrWhiteSpace(song.DestinationPath),
            SharedByUserId = sharedByUserId,
            LikedAtUtc = state?.LikedAtUtc,
            PlayCount = state?.PlayCount ?? 0,
            LastPlayedAtUtc = state?.LastPlayedAtUtc,
        };
}

/// <summary>An account whose music appears in the caller's library, for "Shared by …".</summary>
/// <param name="DisplayName">
/// Null when the grantor never set one. Never their email — the UI picks neutral wording instead.
/// </param>
public sealed record GrantorDto(Guid UserId, string? DisplayName, int SongCount);
