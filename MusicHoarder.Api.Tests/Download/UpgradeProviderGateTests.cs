using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Audio;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Soulseek;

namespace MusicHoarder.Api.Tests.Download;

public class UpgradeProviderGateTests
{
    private static UpgradeFloor Floor(AudioCodecTier tier) => new(tier, (int)tier * 100_000, 200_000);

    [Fact]
    public void Spotiflac_CanUpgradeLossyButNotLossless_WhenConfigured()
    {
        var provider = new StreamingFlacDownloadProvider(
            sidecar: null!, catalogSearch: null!,
            spotifyOptions: Microsoft.Extensions.Options.Options.Create(new SpotifyOptions()),
            options: Monitor(new StreamingFlacOptions { Enabled = true, SidecarUrl = "http://sidecar:8080" }),
            logger: NullLogger<StreamingFlacDownloadProvider>.Instance);

        Assert.True(provider.CanUpgrade(Floor(AudioCodecTier.Lossy)));
        Assert.False(provider.CanUpgrade(Floor(AudioCodecTier.Lossless))); // can't beat an existing FLAC
    }

    [Fact]
    public void Spotiflac_CannotUpgrade_WhenUnconfigured()
    {
        var provider = new StreamingFlacDownloadProvider(
            sidecar: null!, catalogSearch: null!,
            spotifyOptions: Microsoft.Extensions.Options.Options.Create(new SpotifyOptions()),
            options: Monitor(new StreamingFlacOptions()), // Enabled=false
            logger: NullLogger<StreamingFlacDownloadProvider>.Instance);

        Assert.False(provider.CanUpgrade(Floor(AudioCodecTier.Lossy)));
    }

    [Fact]
    public void Slskd_CanUpgrade_TracksConfiguration()
    {
        var configured = new SlskdDownloadProvider(
            fetcher: null!,
            Monitor(new SlskdOptions { BaseUrl = "http://slskd:5030", ApiKey = "k", DownloadsDirectory = "/dl" }),
            NullLogger<SlskdDownloadProvider>.Instance);
        Assert.True(configured.CanUpgrade(Floor(AudioCodecTier.Lossy)));
        Assert.True(configured.CanUpgrade(Floor(AudioCodecTier.Lossless))); // may find higher-bitrate FLAC

        var unconfigured = new SlskdDownloadProvider(
            fetcher: null!, Monitor(new SlskdOptions()), NullLogger<SlskdDownloadProvider>.Instance);
        Assert.False(unconfigured.CanUpgrade(Floor(AudioCodecTier.Lossy)));
    }

    private static IOptionsMonitor<T> Monitor<T>(T value) => new StaticMonitor<T>(value);

    private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
