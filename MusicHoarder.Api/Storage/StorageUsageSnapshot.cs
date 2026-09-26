namespace MusicHoarder.Api.Storage;

/// <summary>
/// What MusicHoarder's files occupy on disk, measured by walking every managed root. Served from
/// <see cref="StorageUsageSnapshotStore"/>; produced by <see cref="StorageUsageCalculator"/>.
/// Category and origin keys are strings on purpose: enums serialize as ints here, and the frontend
/// maps labels and swatches by key with a fallback, so a new key never breaks an old client.
/// </summary>
public sealed record StorageUsageSnapshot(
    DateTime ComputedAtUtc,
    long DurationMs,
    /// <summary>Every byte under a managed root — the number the sidebar shows.</summary>
    long ManagedBytes,
    /// <summary>Summed size of the distinct volumes the roots live on; 0 when none could be probed.</summary>
    long CapacityBytes,
    long FreeBytes,
    IReadOnlyList<StorageVolume> Volumes,
    IReadOnlyList<StorageRoot> Roots,
    /// <summary>One entry per <see cref="CategoryKeys"/> key, zeros included, in display order.</summary>
    IReadOnlyList<StorageBucket> Categories,
    /// <summary>Provenance of the <see cref="CategoryKeys.Library"/> bytes; sums to that bucket exactly.</summary>
    IReadOnlyList<StorageBucket> Origins,
    StorageDuplicates Duplicates,
    StorageLyrics Lyrics,
    StorageReclaimable Reclaimable);

public sealed record StorageBucket(string Key, long Bytes, int Files);

/// <summary>A distinct filesystem behind one or more roots. <paramref name="SamplePath"/> is a root on it.</summary>
public sealed record StorageVolume(string SamplePath, long TotalBytes, long FreeBytes);

/// <summary>
/// One configured directory. <paramref name="Walked"/> is true when its files were counted, either by
/// its own walk or by the walk of a root it sits inside. <paramref name="Skipped"/> says why not.
/// </summary>
public sealed record StorageRoot(
    string Key,
    string Path,
    bool Exists,
    bool Walked,
    string? Skipped,
    long Bytes,
    int Files,
    long UntrackedAudioBytes,
    int UntrackedAudioFiles,
    long DurationMs);

/// <summary>On-disk copies of rows flagged <c>IsDuplicate</c>; tracks counts rows, bytes counts every copy.</summary>
public sealed record StorageDuplicates(long Bytes, int Tracks);

/// <summary>Lyrics live in database columns and are embedded in tags at build — there are no files. Text length, approximately.</summary>
public sealed record StorageLyrics(long ApproxTextBytes, int TracksWithLyrics);

/// <summary>Staged download sources whose library copy is verified and could be released.</summary>
public sealed record StorageReclaimable(long StagedSourceBytes, int StagedSourceTracks, string? UnavailableReason);

/// <summary>What <c>GET /storage</c> returns. <paramref name="Computing"/> covers "queued" too.</summary>
public sealed record StorageUsageResponse(bool Computing, StorageUsageSnapshot? Snapshot, string? LastError);

public static class CategoryKeys
{
    /// <summary>Built audio in the destination library — the copy everything reads.</summary>
    public const string Library = "library";
    /// <summary>Audio in the source library root: the user's own files.</summary>
    public const string Source = "source";
    /// <summary>Audio still sitting in the download staging root (not yet released).</summary>
    public const string Staging = "staging";
    /// <summary>Audio received from another instance's sync.</summary>
    public const string Synced = "synced";
    /// <summary>slskd's completed-downloads directory: transient, but failed transfers accumulate.</summary>
    public const string SoulseekStaging = "soulseekStaging";
    /// <summary>Everything under the videos root: the mp4 clips and their thumbnails.</summary>
    public const string Videos = "videos";
    /// <summary>Image files in the source and destination roots — album covers.</summary>
    public const string Covers = "covers";
    /// <summary>The resized WebP cover cache — derived, regenerable, purgeable.</summary>
    public const string ThumbnailCache = "thumbnailCache";
    /// <summary>Exported m3u8 playlists.</summary>
    public const string Playlists = "playlists";
    /// <summary>Audio under a managed root that no live song row points at.</summary>
    public const string Untracked = "untracked";
    /// <summary>Anything under a dot-directory: in-flight sync uploads, .Trash, .stfolder.</summary>
    public const string Temp = "temp";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All =
    [
        Library, Source, Staging, Synced, SoulseekStaging, Videos, Covers, ThumbnailCache, Playlists,
        Untracked, Temp, Other,
    ];
}

public static class OriginKeys
{
    public const string SpotifyLiked = "spotifyLiked";
    public const string SpotifyPlaylist = "spotifyPlaylist";
    public const string DeezerPlaylist = "deezerPlaylist";
    public const string YouTubePlaylist = "youtubePlaylist";
    public const string DirectUrl = "directUrl";
    public const string AlbumCompletion = "albumCompletion";
    /// <summary>Downloaded, but no wishlist link says why — quality upgrades, for one.</summary>
    public const string OtherDownload = "otherDownload";
    public const string Synced = "synced";
    /// <summary>Found in the source library by a scan.</summary>
    public const string Local = "local";

    public static readonly IReadOnlyList<string> All =
    [
        SpotifyLiked, SpotifyPlaylist, DeezerPlaylist, YouTubePlaylist, DirectUrl, AlbumCompletion, OtherDownload, Synced, Local,
    ];
}

public static class RootKeys
{
    public const string Source = "source";
    public const string Destination = "destination";
    public const string Download = "download";
    public const string Videos = "videos";
    public const string Synced = "synced";
    public const string CoverCache = "coverCache";
    public const string Slskd = "slskd";
}
