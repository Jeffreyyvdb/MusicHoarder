using System.Text.RegularExpressions;

namespace MusicHoarder.Api.Discover;

/// <summary>
/// Parses a pasted Spotify or Deezer playlist reference — a full web URL (with or without query string),
/// a <c>spotify:playlist:{id}</c> URI, or a bare id — into a <c>(provider, playlistId)</c> pair. Deezer
/// playlist ids are numeric; Spotify ids are base62 (typically 22 chars).
/// </summary>
public static partial class PlaylistUrlParser
{
    public const string Spotify = "spotify";
    public const string Deezer = "deezer";

    public static bool TryParse(string? input, out string provider, out string playlistId)
    {
        provider = "";
        playlistId = "";
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var s = input.Trim();

        // spotify:playlist:{id}
        var spUri = SpotifyUriRegex().Match(s);
        if (spUri.Success)
        {
            provider = Spotify;
            playlistId = spUri.Groups[1].Value;
            return true;
        }

        // open.spotify.com/playlist/{id}[?...]  (also spotify.link short hosts resolve to this form)
        var spWeb = SpotifyWebRegex().Match(s);
        if (spWeb.Success)
        {
            provider = Spotify;
            playlistId = spWeb.Groups[1].Value;
            return true;
        }

        // deezer.com[/{lang}]/playlist/{id}[?...]
        var dz = DeezerWebRegex().Match(s);
        if (dz.Success)
        {
            provider = Deezer;
            playlistId = dz.Groups[1].Value;
            return true;
        }

        // Bare ids: Deezer playlist ids are numeric; Spotify ids are base62.
        if (NumericRegex().IsMatch(s))
        {
            provider = Deezer;
            playlistId = s;
            return true;
        }

        if (SpotifyBareIdRegex().IsMatch(s))
        {
            provider = Spotify;
            playlistId = s;
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"spotify:playlist:([A-Za-z0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SpotifyUriRegex();

    [GeneratedRegex(@"open\.spotify\.com/(?:intl-[a-z]{2}/)?playlist/([A-Za-z0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SpotifyWebRegex();

    [GeneratedRegex(@"deezer\.com/(?:[a-z]{2}/)?playlist/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex DeezerWebRegex();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex NumericRegex();

    [GeneratedRegex(@"^[A-Za-z0-9]{16,}$")]
    private static partial Regex SpotifyBareIdRegex();
}
