using MusicHoarder.Api.Download;
using MusicHoarder.Api.Settings;

namespace MusicHoarder.Api.Tests.Download;

/// <summary>Free space as the test says it is; null means "cannot tell".</summary>
public sealed class FakeFreeDiskSpace(long? availableBytes = 500L * 1024 * 1024 * 1024) : IFreeDiskSpace
{
    public long? Available { get; set; } = availableBytes;
    public List<string> Asked { get; } = [];

    public long? AvailableBytes(string directory)
    {
        Asked.Add(directory);
        return Available;
    }
}

/// <summary>Runtime settings with only the automatic-video toggle in play.</summary>
public sealed class FakeRuntimeSettings : IRuntimeSettingsService
{
    public bool AutoDownloadMusicVideos { get; set; }

    public Task<EffectiveSettings> GetAsync(CancellationToken ct = default) => Task.FromResult(Build());

    public Task<EffectiveSettings> UpdateAsync(RuntimeSettingsUpdate update, CancellationToken ct = default) =>
        Task.FromResult(Build());

    private EffectiveSettings Build() => new(
        EnableAcoustIdProvider: false, EnableMusicBrainzWebProvider: false, EnableSpotifyApiProvider: false,
        EnableTrackerProvider: false, EnableDeezerProvider: false, EnableAppleMusicProvider: false,
        QualityGradingEnabled: false, AutoDownloadWishlist: false, AlbumCompletionEnabled: false,
        ReleaseStagedSourcesEnabled: false, UpdatedAtUtc: null,
        AutoDownloadMusicVideos: AutoDownloadMusicVideos);
}
