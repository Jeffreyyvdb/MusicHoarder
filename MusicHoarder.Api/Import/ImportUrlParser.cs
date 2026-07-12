using System.Text.RegularExpressions;

namespace MusicHoarder.Api.Import;

/// <summary>Which kind of single-track reference <see cref="ImportUrlParser"/> recognized.</summary>
public enum ImportUrlKind
{
    /// <summary>A Spotify track — resolved to full metadata via the client-credentials catalog API.</summary>
    SpotifyTrack,

    /// <summary>A YouTube video — probed via yt-dlp and downloaded from that exact URL.</summary>
    YouTube,
}

/// <summary>
/// Parses a pasted single-track reference — a Spotify track URL/URI or a YouTube video URL — into a
/// <c>(kind, id)</c> pair. The single-track counterpart to <see cref="Discover.PlaylistUrlParser"/>.
/// For Spotify the id is the base62 track id; for YouTube it's the 11-char video id (tracking params
/// like <c>list=</c>/<c>t=</c>/<c>si=</c> are discarded so we fetch exactly the one video, not a mix).
/// </summary>
public static partial class ImportUrlParser
{
    public static bool TryParse(string? input, out ImportUrlKind kind, out string id)
    {
        kind = default;
        id = "";
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var s = input.Trim();

        // spotify:track:{id}
        var spUri = SpotifyTrackUriRegex().Match(s);
        if (spUri.Success)
        {
            kind = ImportUrlKind.SpotifyTrack;
            id = spUri.Groups[1].Value;
            return true;
        }

        // open.spotify.com/track/{id}[?...]
        var spWeb = SpotifyTrackWebRegex().Match(s);
        if (spWeb.Success)
        {
            kind = ImportUrlKind.SpotifyTrack;
            id = spWeb.Groups[1].Value;
            return true;
        }

        // youtu.be/{id}, (music.|m.|www.)youtube.com/(watch?v=|shorts/|embed/|v/|live/){id}
        var yt = YouTubeRegex().Match(s);
        if (yt.Success)
        {
            kind = ImportUrlKind.YouTube;
            id = yt.Groups[1].Value;
            return true;
        }

        return false;
    }

    /// <summary>Canonical clean watch URL for a parsed YouTube video id (no playlist/timestamp params).</summary>
    public static string YouTubeWatchUrl(string videoId) => $"https://www.youtube.com/watch?v={videoId}";

    /// <summary>Canonical open.spotify.com track URL for a parsed track id.</summary>
    public static string SpotifyTrackUrl(string trackId) => $"https://open.spotify.com/track/{trackId}";

    [GeneratedRegex(@"spotify:track:([A-Za-z0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SpotifyTrackUriRegex();

    [GeneratedRegex(@"open\.spotify\.com/(?:intl-[a-z]{2}/)?track/([A-Za-z0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SpotifyTrackWebRegex();

    // Accepts the common YouTube surfaces. `v=` may appear anywhere in the query string (e.g.
    // ?app=desktop&v=ID), so tolerate leading params before it. Video ids are 11 chars of [A-Za-z0-9_-].
    [GeneratedRegex(
        @"(?:youtu\.be/|(?:music\.|m\.|www\.)?youtube\.com/(?:watch\?(?:[^\s&]*&)*v=|shorts/|embed/|v/|live/))([A-Za-z0-9_-]{11})",
        RegexOptions.IgnoreCase)]
    private static partial Regex YouTubeRegex();
}
