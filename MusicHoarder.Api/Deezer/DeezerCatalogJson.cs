using System.Text.Json;
using MusicHoarder.Api.Metadata;

namespace MusicHoarder.Api.Deezer;

/// <summary>
/// Reads the bodies of Deezer's catalog lookups (track search and detail, album search and
/// detail, artist search) and its quota-error body into records — the pure half of
/// <see cref="DeezerCatalogService"/>, which keeps the HTTP transport, rate limiting, retries
/// and caching. A body that is not valid JSON (<see cref="JsonException"/>) yields an empty list
/// or null rather than throwing, and Deezer's ids are accepted as a JSON number or a string. Deezer
/// reports durations in seconds; the readers convert them to milliseconds.
/// </summary>
internal static class DeezerCatalogJson
{
    // Deezer signals quota exhaustion with HTTP 200 + an error body (code 4), not just 429.
    private const int DeezerQuotaErrorCode = 4;

    /// <summary>True when the body is Deezer's quota-exhausted error object (<c>error.code == 4</c>).</summary>
    internal static bool IsQuotaError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.Object &&
                err.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.Number)
            {
                return code.GetInt32() == DeezerQuotaErrorCode;
            }
        }
        catch (JsonException)
        {
            // Treat unparseable body as non-quota; the caller's parse will yield no tracks.
        }

        return false;
    }

    // --- Tracks (search hits and full track detail) ---

    internal static IReadOnlyList<DeezerCatalogTrack> ParseSearchResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return [];

            var list = new List<DeezerCatalogTrack>();
            foreach (var item in data.EnumerateArray())
            {
                var parsed = ParseTrack(item, fullDetail: false);
                if (parsed is not null)
                    list.Add(parsed);
            }

            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static DeezerCatalogTrack? ParseTrackDetail(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseTrack(doc.RootElement, fullDetail: true);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DeezerCatalogTrack? ParseTrack(JsonElement track, bool fullDetail)
    {
        if (track.ValueKind != JsonValueKind.Object)
            return null;

        var id = track.TryGetProperty("id", out var idProp)
            ? idProp.ValueKind switch
            {
                JsonValueKind.Number => idProp.GetInt64().ToString(),
                JsonValueKind.String => idProp.GetString() ?? "",
                _ => "",
            }
            : "";
        if (string.IsNullOrEmpty(id))
            return null;

        var title = track.TryGetProperty("title", out var tProp) && tProp.ValueKind == JsonValueKind.String
            ? tProp.GetString() ?? ""
            : "";

        var durationSec = track.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number
            ? dur.GetInt32()
            : 0;

        var artist = "";
        if (track.TryGetProperty("artist", out var artistEl) && artistEl.ValueKind == JsonValueKind.Object &&
            artistEl.TryGetProperty("name", out var artistName) && artistName.ValueKind == JsonValueKind.String)
            artist = artistName.GetString() ?? "";

        var albumName = "";
        if (track.TryGetProperty("album", out var albumEl) && albumEl.ValueKind == JsonValueKind.Object &&
            albumEl.TryGetProperty("title", out var albumTitle) && albumTitle.ValueKind == JsonValueKind.String)
            albumName = albumTitle.GetString() ?? "";

        // ISRC, release date, track position and contributors are only present on full track detail.
        string? isrc = null;
        int? releaseYear = null;
        int? trackNumber = null;
        string? artists = null;
        if (fullDetail)
        {
            if (track.TryGetProperty("isrc", out var isrcProp) && isrcProp.ValueKind == JsonValueKind.String)
                isrc = isrcProp.GetString();

            if (track.TryGetProperty("release_date", out var rd) && rd.ValueKind == JsonValueKind.String)
                releaseYear = ReleaseDateParser.ParseYear(rd.GetString());

            if (track.TryGetProperty("track_position", out var tp) && tp.ValueKind == JsonValueKind.Number)
                trackNumber = tp.GetInt32();

            artists = ParseContributors(track);
        }

        return new DeezerCatalogTrack(id, title, artist, albumName, releaseYear, trackNumber, durationSec * 1000, isrc,
            Artists: artists);
    }

    /// <summary>
    /// Discrete credited artists from the full track detail's <c>contributors</c> array (the search
    /// payload only carries the single primary <c>artist</c>). Featured guests are listed with
    /// <c>role: "Featured"</c> and still belong in the per-artist credit, so every named contributor
    /// is kept (deduped, in payload order).
    /// </summary>
    private static string? ParseContributors(JsonElement track)
    {
        if (!track.TryGetProperty("contributors", out var contributors) || contributors.ValueKind != JsonValueKind.Array)
            return null;

        var names = new List<string>();
        foreach (var contributor in contributors.EnumerateArray())
        {
            if (contributor.ValueKind == JsonValueKind.Object
                && contributor.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(name.GetString())
                && !names.Contains(name.GetString()!.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name.GetString()!.Trim());
            }
        }

        return MusicHoarder.Api.Metadata.MultiValue.Join(names);
    }

    // --- Albums ---

    /// <summary>The first album search hit's id (<c>GET /search/album</c>), or null.</summary>
    internal static string? ParseFirstAlbumId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                        return id.ValueKind == JsonValueKind.Number ? id.GetInt64().ToString() : id.GetString();
                }
            }
        }
        catch (JsonException) { /* fall through */ }
        return null;
    }

    /// <summary>Album search hits (<c>GET /search/album</c>) that carry an id.</summary>
    internal static IReadOnlyList<DeezerAlbumCandidate> ParseAlbumCandidates(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return [];

            var candidates = new List<DeezerAlbumCandidate>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = ReadIdAsString(item);
                if (id is null) continue;
                var title = ReadString(item, "title");
                string? artistName = item.TryGetProperty("artist", out var aEl) && aEl.ValueKind == JsonValueKind.Object
                    ? ReadString(aEl, "name")
                    : null;
                candidates.Add(new DeezerAlbumCandidate(id, title, artistName));
            }

            return candidates;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static DeezerAlbumDetail? ParseAlbum(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var id = root.TryGetProperty("id", out var idEl)
                ? idEl.ValueKind switch
                {
                    JsonValueKind.Number => idEl.GetInt64().ToString(),
                    JsonValueKind.String => idEl.GetString() ?? "",
                    _ => "",
                }
                : "";
            if (string.IsNullOrEmpty(id)) return null;

            var title = root.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String ? tEl.GetString() : null;
            int? year = root.TryGetProperty("release_date", out var rdEl) && rdEl.ValueKind == JsonValueKind.String
                ? ReleaseDateParser.ParseYear(rdEl.GetString()) : null;
            string? artist = root.TryGetProperty("artist", out var aEl) && aEl.ValueKind == JsonValueKind.Object &&
                aEl.TryGetProperty("name", out var an) && an.ValueKind == JsonValueKind.String ? an.GetString() : null;
            string? cover = root.TryGetProperty("cover_xl", out var cEl) && cEl.ValueKind == JsonValueKind.String
                ? cEl.GetString()
                : root.TryGetProperty("cover_big", out var cbEl) && cbEl.ValueKind == JsonValueKind.String ? cbEl.GetString() : null;

            var tracks = new List<DeezerAlbumTrackItem>();
            if (root.TryGetProperty("tracks", out var tracksEl) && tracksEl.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array)
            {
                var ordinal = 0;
                foreach (var t in data.EnumerateArray())
                {
                    if (t.ValueKind != JsonValueKind.Object) continue;
                    ordinal++;
                    var disc = t.TryGetProperty("disk_number", out var dk) && dk.ValueKind == JsonValueKind.Number ? dk.GetInt32() : 1;
                    var pos = t.TryGetProperty("track_position", out var tp) && tp.ValueKind == JsonValueKind.Number ? tp.GetInt32() : ordinal;
                    var tTitle = t.TryGetProperty("title", out var ti) && ti.ValueKind == JsonValueKind.String ? ti.GetString() : null;
                    var durSec = t.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number ? du.GetInt32() : 0;
                    var tId = t.TryGetProperty("id", out var tid)
                        ? tid.ValueKind switch
                        {
                            JsonValueKind.Number => tid.GetInt64().ToString(),
                            JsonValueKind.String => tid.GetString(),
                            _ => null,
                        }
                        : null;
                    tracks.Add(new DeezerAlbumTrackItem(disc, pos, tTitle, durSec * 1000, tId));
                }
            }

            return new DeezerAlbumDetail(id, title, artist, year, cover, tracks);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // --- Artists ---

    internal static IReadOnlyList<DeezerArtistCandidate> ParseArtistCandidates(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return [];

            var candidates = new List<DeezerArtistCandidate>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                // Largest first: picture_xl is 1000x1000, picture_big 500x500.
                var picture = ReadString(item, "picture_xl") ?? ReadString(item, "picture_big") ?? ReadString(item, "picture_medium");
                candidates.Add(new DeezerArtistCandidate(ReadString(item, "name"), picture));
            }

            return candidates;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // --- Field readers ---

    private static string? ReadString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static string? ReadIdAsString(JsonElement obj) =>
        obj.TryGetProperty("id", out var id)
            ? id.ValueKind switch
            {
                JsonValueKind.Number => id.GetInt64().ToString(),
                JsonValueKind.String => id.GetString(),
                _ => null,
            }
            : null;
}
