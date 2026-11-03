using System.Text.Json;
using MusicHoarder.Api.Metadata;

namespace MusicHoarder.Api.Spotify;

/// <summary>
/// Reads the Spotify Web API's catalog bodies (<c>/v1/search</c>, <c>/v1/tracks/{id}</c>,
/// <c>/v1/albums/{id}</c>) into the catalog records. Pure functions of the body string: no HTTP,
/// cache or options, so the mapping rules (which field wins, what a missing or wrong-typed field
/// defaults to, which rows are skipped) can be tested from raw JSON. A body that is not valid
/// JSON reads as empty or <c>null</c>; only <see cref="JsonException"/> is caught, so a valid body
/// of the wrong shape can still throw. <see cref="SpotifyCatalogSearchService"/> owns the URLs,
/// the search cache and the transport.
/// </summary>
internal static class SpotifyCatalogJson
{
    /// <summary>A <c>type=track</c> search body: every <c>tracks.items</c> row that reads as a track.</summary>
    internal static IReadOnlyList<SpotifyCatalogTrack> ParseSearchResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tracks", out var tracksEl))
                return [];

            if (!tracksEl.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return [];

            var list = new List<SpotifyCatalogTrack>();
            foreach (var track in items.EnumerateArray())
            {
                var parsed = ParseTrack(track);
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

    /// <summary>A <c>/v1/tracks/{id}</c> body.</summary>
    internal static SpotifyCatalogTrack? ParseTrackDetail(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            // A /v1/tracks/{id} object has the same shape as a search-result track item.
            return ParseTrack(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The <c>album.id</c> of a <c>/v1/tracks/{id}</c> body.</summary>
    internal static string? ParseTrackAlbumId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object &&
                album.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                return id.GetString();
        }
        catch (JsonException) { /* fall through */ }
        return null;
    }

    /// <summary>The first string id among a <c>type=album</c> search body's hits.</summary>
    internal static string? ParseFirstAlbumId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("albums", out var albums) &&
                albums.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        return id.GetString();
                }
            }
        }
        catch (JsonException) { /* fall through */ }
        return null;
    }

    /// <summary>A <c>type=album</c> search body's hits, each with its first named artist.</summary>
    internal static IReadOnlyList<SpotifyAlbumCandidate> ParseAlbumCandidates(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("albums", out var albums) ||
                !albums.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return [];

            var candidates = new List<SpotifyAlbumCandidate>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() is not { } albumIdValue)
                    continue;
                var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                string? artistName = null;
                if (item.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in artists.EnumerateArray())
                    {
                        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("name", out var an) && an.ValueKind == JsonValueKind.String)
                        {
                            artistName = an.GetString();
                            break;
                        }
                    }
                }

                candidates.Add(new SpotifyAlbumCandidate(albumIdValue, name, artistName));
            }

            return candidates;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>A <c>type=artist</c> search body's hits, each with its first (largest) image.</summary>
    internal static IReadOnlyList<SpotifyArtistCandidate> ParseArtistCandidates(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("artists", out var artists) ||
                !artists.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return [];

            var candidates = new List<SpotifyArtistCandidate>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var artistName = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                // Spotify orders images largest-first; the first url is the full-size portrait.
                string? imageUrl = null;
                if (item.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
                {
                    foreach (var img in images.EnumerateArray())
                    {
                        if (img.ValueKind == JsonValueKind.Object && img.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                        {
                            imageUrl = u.GetString();
                            break;
                        }
                    }
                }

                candidates.Add(new SpotifyArtistCandidate(artistName, imageUrl));
            }

            return candidates;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The first string id among a <c>type=track</c> search body's hits (the ISRC lookup).</summary>
    internal static string? ParseFirstTrackId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("tracks", out var tracks) &&
                tracks.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        return id.GetString();
                }
            }
        }
        catch (JsonException) { /* fall through */ }
        return null;
    }

    /// <summary>A <c>/v1/albums/{id}</c> body, with its tracklist.</summary>
    internal static SpotifyAlbumDetail? ParseAlbum(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
            if (string.IsNullOrEmpty(id)) return null;

            var name = root.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String ? nEl.GetString() : null;
            var releaseDateStr = root.TryGetProperty("release_date", out var rdEl) && rdEl.ValueKind == JsonValueKind.String
                ? rdEl.GetString() : null;
            int? year = ReleaseDateParser.ParseYear(releaseDateStr);

            var label = root.TryGetProperty("label", out var lblEl) && lblEl.ValueKind == JsonValueKind.String
                ? lblEl.GetString() : null;

            string? upc = null;
            if (root.TryGetProperty("external_ids", out var extIds) && extIds.ValueKind == JsonValueKind.Object &&
                extIds.TryGetProperty("upc", out var upcEl) && upcEl.ValueKind == JsonValueKind.String)
                upc = upcEl.GetString();

            // Keep the © copyright line; drop the ℗ phonogram line (SpotiFLAC's rule).
            string? copyright = null;
            if (root.TryGetProperty("copyrights", out var copyrightsEl) && copyrightsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in copyrightsEl.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object) continue;
                    var type = c.TryGetProperty("type", out var tEl) && tEl.ValueKind == JsonValueKind.String ? tEl.GetString() : null;
                    var text = c.TryGetProperty("text", out var txtEl) && txtEl.ValueKind == JsonValueKind.String ? txtEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    // Prefer the © (type "C") line; remember any line as a fallback if no © is present.
                    if (string.Equals(type, "C", StringComparison.OrdinalIgnoreCase)) { copyright = text; break; }
                    copyright ??= text;
                }
            }

            string? artist = null;
            if (root.TryGetProperty("artists", out var arts) && arts.ValueKind == JsonValueKind.Array)
                artist = string.Join(", ", arts.EnumerateArray()
                    .Where(a => a.TryGetProperty("name", out var an) && an.ValueKind == JsonValueKind.String)
                    .Select(a => a.GetProperty("name").GetString()));

            string? imageUrl = null;
            if (root.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
                foreach (var img in imgs.EnumerateArray())
                    if (img.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String) { imageUrl = u.GetString(); break; }

            var tracks = new List<SpotifyAlbumTrackItem>();
            if (root.TryGetProperty("tracks", out var tracksEl) && tracksEl.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in items.EnumerateArray())
                {
                    if (t.ValueKind != JsonValueKind.Object) continue;
                    var disc = t.TryGetProperty("disc_number", out var dn) && dn.ValueKind == JsonValueKind.Number ? dn.GetInt32() : 1;
                    var num = t.TryGetProperty("track_number", out var tn) && tn.ValueKind == JsonValueKind.Number ? tn.GetInt32() : 0;
                    var tName = t.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : null;
                    var dur = t.TryGetProperty("duration_ms", out var dms) && dms.ValueKind == JsonValueKind.Number ? dms.GetInt32() : 0;
                    var tId = t.TryGetProperty("id", out var ti) && ti.ValueKind == JsonValueKind.String ? ti.GetString() : null;
                    tracks.Add(new SpotifyAlbumTrackItem(disc, num, tName, dur, tId));
                }
            }

            return new SpotifyAlbumDetail(
                id!, name, string.IsNullOrWhiteSpace(artist) ? null : artist, year, imageUrl, tracks,
                ReleaseDate: ReleaseDateParser.Normalize(releaseDateStr),
                Label: string.IsNullOrWhiteSpace(label) ? null : label!.Trim(),
                Upc: string.IsNullOrWhiteSpace(upc) ? null : upc!.Trim(),
                Copyright: string.IsNullOrWhiteSpace(copyright) ? null : copyright!.Trim());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SpotifyCatalogTrack? ParseTrack(JsonElement track)
    {
        if (track.ValueKind != JsonValueKind.Object)
            return null;

        var id = track.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString() ?? ""
            : "";
        if (string.IsNullOrEmpty(id))
            return null;

        var name = track.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
            ? nameProp.GetString() ?? ""
            : "";

        var durationMs = track.TryGetProperty("duration_ms", out var dur) ? dur.GetInt32() : 0;

        var artist = "";
        string? artistsMulti = null;
        if (track.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
        {
            var names = new List<string>();
            foreach (var a in artists.EnumerateArray())
            {
                if (a.TryGetProperty("name", out var an) && an.ValueKind == JsonValueKind.String)
                    names.Add(an.GetString()!);
            }

            // Keep the comma-joined display string unchanged (the fuzzy scorer depends on it);
            // expose the discrete names separately for the multi-value ARTISTS tag.
            artist = string.Join(", ", names);
            artistsMulti = MultiValue.Join(names);
        }

        var albumName = "";
        int? releaseYear = null;
        string? albumType = null;
        int? totalTracks = null;
        string? albumId = null;
        string? albumArtUrl = null;
        if (track.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object)
        {
            if (album.TryGetProperty("name", out var alName) && alName.ValueKind == JsonValueKind.String)
                albumName = alName.GetString() ?? "";

            if (album.TryGetProperty("release_date", out var rd) && rd.ValueKind == JsonValueKind.String)
                releaseYear = ReleaseDateParser.ParseYear(rd.GetString());

            if (album.TryGetProperty("album_type", out var at) && at.ValueKind == JsonValueKind.String)
                albumType = at.GetString()?.ToLowerInvariant();

            if (album.TryGetProperty("total_tracks", out var tt) && tt.ValueKind == JsonValueKind.Number)
                totalTracks = tt.GetInt32();

            if (album.TryGetProperty("id", out var aid) && aid.ValueKind == JsonValueKind.String)
                albumId = aid.GetString();

            // First image is the largest (640px). Used by the single-track URL-import preview.
            if (album.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
                foreach (var img in imgs.EnumerateArray())
                    if (img.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                    {
                        albumArtUrl = u.GetString();
                        break;
                    }
        }

        int? trackNumber = null;
        if (track.TryGetProperty("track_number", out var tn) && tn.ValueKind == JsonValueKind.Number)
            trackNumber = tn.GetInt32();

        int? discNumber = null;
        if (track.TryGetProperty("disc_number", out var dn) && dn.ValueKind == JsonValueKind.Number)
            discNumber = dn.GetInt32();

        string? isrc = null;
        if (track.TryGetProperty("external_ids", out var ext) && ext.ValueKind == JsonValueKind.Object &&
            ext.TryGetProperty("isrc", out var isrcProp) && isrcProp.ValueKind == JsonValueKind.String)
            isrc = isrcProp.GetString();

        var isExplicit = track.TryGetProperty("explicit", out var ex) && ex.ValueKind == JsonValueKind.True;

        return new SpotifyCatalogTrack(
            id, name, artist, albumName, releaseYear, trackNumber, durationMs, isrc,
            Artists: artistsMulti, DiscNumber: discNumber, AlbumType: albumType, TotalTracks: totalTracks,
            AlbumId: albumId, AlbumArtUrl: albumArtUrl, Explicit: isExplicit);
    }
}
