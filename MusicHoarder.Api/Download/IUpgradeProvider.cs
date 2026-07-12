using MusicHoarder.Api.Audio;

namespace MusicHoarder.Api.Download;

/// <summary>
/// The quality a candidate must beat to count as an upgrade: the target's current codec
/// <see cref="Tier"/> and <see cref="AudioQuality.Score"/>, plus its <see cref="DurationMs"/> (0/null
/// = unknown) so a provider can sanity-check that a candidate is the same-length recording.
/// </summary>
public readonly record struct UpgradeFloor(AudioCodecTier Tier, int Score, int? DurationMs);

/// <summary>
/// A download backend that can acquire a strictly-better copy of a song already in the library. This
/// is the upgrade-specialized sibling of <see cref="IDownloadProvider"/>: a provider that can never
/// improve on an existing file (e.g. yt-dlp, which only produces lossy audio) simply doesn't
/// implement it and is thereby excluded from the upgrade chain with no special-casing. The
/// verification of "is this download actually better and the same recording" still happens
/// centrally in the merge stage — providers only need to make a best effort.
/// </summary>
public interface IUpgradeProvider
{
    /// <summary>Stable identifier, matched against <c>MusicEnricher:DownloadProviders</c> for chain ordering.
    /// Mirrors <see cref="IDownloadProvider.Name"/> so a class implementing both shares one name.</summary>
    string Name { get; }

    /// <summary>
    /// Could this provider plausibly beat <paramref name="floor"/>? A cheap pre-filter that skips
    /// pointless searches/downloads (e.g. the lossless-FLAC sidecar returns false once the target is
    /// already lossless). The merge stage remains the authoritative check on the real file.
    /// </summary>
    bool CanUpgrade(UpgradeFloor floor);

    /// <summary>
    /// Acquire a copy that beats <paramref name="floor"/> into <paramref name="req"/>'s destination.
    /// Same <see cref="DownloadResult"/> semantics as <see cref="IDownloadProvider.DownloadAsync"/>:
    /// <see cref="DownloadResult.Ok"/> → take it; <see cref="DownloadResult.Missing"/> → nothing
    /// better here, try the next provider; <see cref="DownloadResult.Failed"/> → transient, stop the
    /// chain (don't burn the next provider's quota on a flaky backend).
    /// </summary>
    Task<DownloadResult> DownloadBetterAsync(DownloadRequest req, UpgradeFloor floor, CancellationToken ct);
}
