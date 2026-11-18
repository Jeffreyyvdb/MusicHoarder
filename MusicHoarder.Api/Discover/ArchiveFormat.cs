using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MusicHoarder.Api.Matching;

namespace MusicHoarder.Api.Discover;

/// <summary>
/// The Spotify Playlist Archive's file formats and the rules over them, with no I/O: parse a source's
/// metadata, merge sources, search, parse a playlist file.
/// </summary>
public static partial class ArchiveFormat
{
    /// <summary>A Spotify playlist or track id. Anything else never goes into a URL.</summary>
    public static bool IsSpotifyId(string? id) => id is not null && SpotifyIdRegex().IsMatch(id);

    public static IReadOnlyList<ArchivedPlaylist> ParseMetadata(Stream json, string sourceBaseUrl)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return [];

        var playlists = new List<ArchivedPlaylist>();
        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            if (!IsSpotifyId(entry.Name) || entry.Value.ValueKind != JsonValueKind.Object)
                continue;

            var value = entry.Value;
            var name = String(value, "original_name") ?? String(value, "unique_name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var description = String(value, "description");
            var owner = value.TryGetProperty("owner", out var o) && o.ValueKind == JsonValueKind.Object
                ? String(o, "name")
                : null;
            var followers = value.TryGetProperty("num_followers", out var f) && f.ValueKind == JsonValueKind.Number
                && f.TryGetInt64(out var n) ? n : 0;

            playlists.Add(new ArchivedPlaylist(
                entry.Name,
                name,
                string.IsNullOrWhiteSpace(description) ? null : description,
                followers,
                owner,
                String(value, "snapshot_id") ?? "",
                sourceBaseUrl));
        }

        return playlists;
    }

    /// <summary>Merges parsed sources listed oldest first: a later source overrides an earlier one for the same id.</summary>
    public static Dictionary<string, ArchivedPlaylist> Merge(IEnumerable<IReadOnlyList<ArchivedPlaylist>> sourcesOldestFirst)
    {
        var catalog = new Dictionary<string, ArchivedPlaylist>(StringComparer.Ordinal);
        foreach (var source in sourcesOldestFirst)
            foreach (var playlist in source)
                catalog[playlist.Id] = playlist;
        return catalog;
    }

    /// <summary>
    /// Playlists whose name and description carry every term of the query (the web's search rule), most
    /// followed first. A blank query keeps them all.
    /// </summary>
    public static IReadOnlyList<ArchivedPlaylist> Search(IEnumerable<ArchivedPlaylist> catalog, string? query, int limit)
    {
        var terms = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return catalog
            .Where(p => terms.Length == 0 || ContainsAll(Fold($"{p.Name} {p.Description}"), terms))
            .OrderByDescending(p => p.Followers)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .Take(Math.Max(0, limit))
            .ToList();
    }

    /// <summary>Tracks of an archived <c>pretty/{id}.json</c>. Local files and episodes have no track URL and are skipped.</summary>
    public static IReadOnlyList<ArchivedTrack> ParseTracks(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("tracks", out var tracks)
            || tracks.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<ArchivedTrack>();
        foreach (var track in tracks.EnumerateArray())
        {
            if (track.ValueKind != JsonValueKind.Object) continue;
            var trackId = TrackIdOf(String(track, "url"));
            var title = String(track, "name");
            if (trackId is null || string.IsNullOrWhiteSpace(title)) continue;

            var artists = new List<string>();
            if (track.TryGetProperty("artists", out var artistArray) && artistArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var artist in artistArray.EnumerateArray())
                    if (artist.ValueKind == JsonValueKind.Object && String(artist, "name") is { Length: > 0 } artistName)
                        artists.Add(artistName);
            }

            var album = track.TryGetProperty("album", out var a) && a.ValueKind == JsonValueKind.Object
                ? String(a, "name")
                : null;
            var durationMs = track.TryGetProperty("duration_ms", out var d) && d.ValueKind == JsonValueKind.Number
                && d.TryGetInt32(out var ms) ? ms : 0;

            result.Add(new ArchivedTrack(trackId, title, artists, album, durationMs, AddedAt(String(track, "added_at"))));
        }

        return result;
    }

    /// <summary>The credit line the Spotify API path builds for a track's artists.</summary>
    public static string ArtistCredit(IReadOnlyList<string> artists) => string.Join(", ", artists);

    private static string? TrackIdOf(string? url)
    {
        if (url is null) return null;
        var match = TrackUrlRegex().Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static DateTime? AddedAt(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool ContainsAll(string haystack, string[] terms) =>
        terms.All(t => haystack.Contains(t, StringComparison.Ordinal));

    /// <summary>The web's <c>normalizeForSearch</c>: lowercase letters and digits, diacritics stripped, apostrophes dropped.</summary>
    private static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var folded = TitleNormalizer.FoldDiacritics(value).ToLowerInvariant();
        folded = ApostropheRegex().Replace(folded, "");
        return NonAlphanumericRegex().Replace(folded, " ").Trim();
    }

    [GeneratedRegex(@"^[A-Za-z0-9]{22}\z")]
    private static partial Regex SpotifyIdRegex();

    [GeneratedRegex(@"^https://open\.spotify\.com/track/([A-Za-z0-9]{22})/?(?:\?.*)?\z")]
    private static partial Regex TrackUrlRegex();

    [GeneratedRegex("['‘’ʼ`´]")]
    private static partial Regex ApostropheRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonAlphanumericRegex();
}
