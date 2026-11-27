namespace MusicHoarder.Api.Persistence;

/// <summary>
/// One stored audio file, named by the SHA-256 of its bytes. Instance-wide: it has no owner and no
/// query filter, because any number of libraries' <see cref="SongMetadata"/> rows may point at the
/// same file (<see cref="SongMetadata.AudioAssetId"/>). MusicHoarder never writes tags into an asset's
/// file, so its hash stays true for as long as nothing outside the app changes it.
/// <para>
/// The file is either in the content store (<c>&lt;ContentStoreDirectory&gt;/ab/cd/&lt;sha256&gt;.&lt;ext&gt;</c>,
/// where hosted downloads land) or registered where it already was: an untouched original on the
/// source share or in the download folder. A library's tagged destination copy is never an asset.
/// </para>
/// <para>
/// Rows are never deleted. <see cref="PurgedAtUtc"/> marks one whose file was removed on purpose.
/// </para>
/// </summary>
public class AudioAsset
{
    public int Id { get; set; }

    /// <summary>Lowercase hex SHA-256 of the file's bytes. Unique.</summary>
    public required string Sha256 { get; set; }

    /// <summary>Absolute path of the file, with forward slashes, as the API process sees it.</summary>
    public required string StoragePath { get; set; }

    public required long SizeBytes { get; set; }

    /// <summary>The file extension without its dot, lowercase (<c>flac</c>, <c>opus</c>).</summary>
    public required string Container { get; set; }

    /// <summary>The codec as the tag reader describes it, when it could read one.</summary>
    public string? Codec { get; set; }

    /// <summary>Kilobits per second.</summary>
    public int? Bitrate { get; set; }

    public int? SampleRate { get; set; }

    public int? DurationMs { get; set; }

    /// <summary>The Chromaprint fingerprint, copied from a song whose file this is, once one is known.</summary>
    public string? Fingerprint { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>The last time a song was linked to this asset.</summary>
    public DateTime LastReferencedAtUtc { get; set; }

    /// <summary>When the file was deleted on purpose. The row stays.</summary>
    public DateTime? PurgedAtUtc { get; set; }
}
