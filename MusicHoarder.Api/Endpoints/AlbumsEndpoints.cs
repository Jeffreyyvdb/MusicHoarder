using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Endpoints;

public static class AlbumsEndpoints
{
    public static IEndpointRouteBuilder MapAlbumsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/albums/tracklist", GetAlbumTracklist)
            .WithName("GetAlbumTracklist")
            .WithSummary("Reconciled multi-provider canonical tracklist for an album, each track annotated with the owned song (if any).")
            .WithTags("Library")
            .RequireOwner();

        return app;
    }

    internal static async Task<IResult> GetAlbumTracklist(
        string artist,
        string album,
        MusicHoarderDbContext db,
        IOptions<MusicEnricherOptions> options,
        CancellationToken ct)
    {
        var artistKey = TitleNormalizer.NormalizeForSearch(artist);
        var albumKey = TitleNormalizer.NormalizeForSearch(album);
        if (artistKey.Length == 0 || albumKey.Length == 0)
            return Results.NotFound(new { message = "Album not specified." });

        var canonical = await db.CanonicalAlbums
            .AsNoTracking()
            .Include(a => a.Tracks)
            .FirstOrDefaultAsync(a => a.ArtistKey == artistKey && a.AlbumKey == albumKey, ct);

        // Not yet fetched (or never will be) → 404 so the frontend falls back to its owned-only view.
        if (canonical is null || canonical.Status != CanonicalAlbumStatus.Fetched)
            return Results.NotFound(new { message = "Canonical tracklist not available for this album." });

        // Owned songs in this album group, mirroring the frontend's (albumArtist ?? artist, album) grouping.
        var artistLower = artist.ToLowerInvariant();
        var albumLower = album.ToLowerInvariant();
        var ownedSongs = await db.Songs
            .AsNoTracking()
            .Where(s => s.DeletedAtUtc == null
                && s.Album != null && s.Album.ToLower() == albumLower
                && ((s.AlbumArtist ?? s.Artist) ?? "").ToLower() == artistLower)
            .Select(s => new OwnedSong(s.Id, s.MusicBrainzId, s.DiscNumber, s.TrackNumber, s.Title))
            .ToListAsync(ct);

        var orderedTracks = canonical.Tracks
            .OrderBy(t => t.DiscNumber)
            .ThenBy(t => t.TrackNumber)
            .ToList();

        var matches = MatchOwnedSongs(orderedTracks, ownedSongs, options.Value.IdentityTitleThreshold);

        var tracks = orderedTracks
            .Select(t => new
            {
                discNumber = t.DiscNumber,
                trackNumber = t.TrackNumber,
                title = t.Title,
                durationMs = t.DurationMs,
                musicBrainzRecordingId = t.MusicBrainzRecordingId,
                corroboratingProviders = SplitProviders(t.CorroboratingProviders),
                corroborationCount = t.CorroborationCount,
                isContested = t.IsContested,
                ownedSongId = matches.TryGetValue(t.Id, out var songId) ? songId : (int?)null,
            })
            .ToList();

        return Results.Ok(new
        {
            artist = canonical.DisplayArtist,
            album = canonical.DisplayTitle,
            year = canonical.Year,
            coverArtUrl = canonical.CoverArtUrl,
            resolvedTrackCount = canonical.ResolvedTrackCount,
            trackCountContested = canonical.TrackCountContested,
            ownedCount = tracks.Count(t => t.ownedSongId is not null),
            totalCount = tracks.Count,
            sources = ParseSources(canonical.SourcesJson),
            tracks,
        });
    }

    private static string[] SplitProviders(string? csv)
        => string.IsNullOrWhiteSpace(csv) ? [] : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static object[] ParseSources(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            var stored = JsonSerializer.Deserialize<List<StoredSource>>(json);
            return stored is null
                ? []
                : stored.Select(s => (object)new
                {
                    provider = s.Provider.ToString(),
                    albumId = s.AlbumId,
                    trackCount = s.TrackCount,
                    inWinningCluster = s.InWinningCluster,
                }).ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Maps each canonical track to the owned song that represents it (track id → song id), in strength
    /// order across all tracks: every recording-MBID match resolves before any position match, and all
    /// position matches before any fuzzy-title match. Each owned song is consumed at most once.
    /// </summary>
    private static Dictionary<int, int> MatchOwnedSongs(
        List<CanonicalAlbumTrack> tracks, List<OwnedSong> ownedSongs, double titleThreshold)
    {
        var matched = new Dictionary<int, int>();
        var consumed = new HashSet<int>();

        foreach (var t in tracks)
        {
            if (string.IsNullOrEmpty(t.MusicBrainzRecordingId)) continue;
            var song = ownedSongs.FirstOrDefault(s => !consumed.Contains(s.Id) && s.MusicBrainzId == t.MusicBrainzRecordingId);
            if (song is not null) { matched[t.Id] = song.Id; consumed.Add(song.Id); }
        }

        foreach (var t in tracks)
        {
            if (matched.ContainsKey(t.Id)) continue;
            var song = ownedSongs.FirstOrDefault(s =>
                !consumed.Contains(s.Id) && (s.DiscNumber ?? 1) == t.DiscNumber && s.TrackNumber == t.TrackNumber);
            if (song is not null) { matched[t.Id] = song.Id; consumed.Add(song.Id); }
        }

        foreach (var t in tracks)
        {
            if (matched.ContainsKey(t.Id) || string.IsNullOrWhiteSpace(t.Title)) continue;
            OwnedSong? best = null;
            double bestScore = 0;
            foreach (var s in ownedSongs)
            {
                if (consumed.Contains(s.Id)) continue;
                var score = FuzzyTextMatch.Ratio(t.Title, s.Title) ?? 0;
                if (score > bestScore) { bestScore = score; best = s; }
            }

            if (best is not null && bestScore >= titleThreshold) { matched[t.Id] = best.Id; consumed.Add(best.Id); }
        }

        return matched;
    }

    private sealed record OwnedSong(int Id, string? MusicBrainzId, int? DiscNumber, int? TrackNumber, string? Title);

    private sealed record StoredSource(EnrichmentProvider Provider, string? AlbumId, int TrackCount, bool InWinningCluster);
}
