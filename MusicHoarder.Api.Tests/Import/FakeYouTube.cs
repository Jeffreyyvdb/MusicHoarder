using MusicHoarder.Api.Import;

namespace MusicHoarder.Api.Tests.Import;

/// <summary>A playlist reader that serves canned playlists by id, and records each read.</summary>
public sealed class FakeYouTubePlaylistReader : IYouTubePlaylistReader
{
    public Dictionary<string, YouTubePlaylist> Playlists { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, every read fails with this hint.</summary>
    public string? FailWith { get; set; }

    public List<(string PlaylistId, int? MaxEntries)> Reads { get; } = [];

    public Task<YouTubePlaylistOutcome> ReadAsync(string playlistId, int? maxEntries, CancellationToken ct)
    {
        Reads.Add((playlistId, maxEntries));
        if (FailWith is not null)
            return Task.FromResult(YouTubePlaylistOutcome.Failed(FailWith, null));
        if (!Playlists.TryGetValue(playlistId, out var playlist))
            return Task.FromResult(YouTubePlaylistOutcome.Failed("That playlist does not exist, or it is private.", null));

        var entries = maxEntries is { } max ? playlist.Entries.Take(max).ToList() : playlist.Entries;
        return Task.FromResult(YouTubePlaylistOutcome.Success(playlist with { Entries = entries }));
    }

    public static YouTubePlaylist Playlist(string id, string title, params YouTubePlaylistEntry[] entries) =>
        new(id, title, $"https://i.ytimg.com/pl/{id}.jpg", entries.Length, entries);
}

/// <summary>A single-video probe that answers from a map keyed by watch URL; unknown videos fail.</summary>
public sealed class FakeYouTubeMetadataResolver : IYouTubeMetadataResolver
{
    public Dictionary<string, YouTubeProbeResult> Videos { get; } = new(StringComparer.Ordinal);

    public List<string> Probes { get; } = [];

    /// <summary>Runs during each probe — lets a test act as a second sync writing at that moment.</summary>
    public Func<string, Task>? OnProbe { get; set; }

    public async Task<YouTubeProbeOutcome> ProbeAsync(string url, CancellationToken ct = default)
    {
        Probes.Add(url);
        if (OnProbe is not null) await OnProbe(url);
        return Videos.TryGetValue(url, out var result)
            ? YouTubeProbeOutcome.Success(result)
            : YouTubeProbeOutcome.Failed("That video is unavailable.", null);
    }
}
