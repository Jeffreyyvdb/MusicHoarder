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

        app.MapPost("/api/albums/canonical-status", GetCanonicalStatuses)
            .WithName("GetAlbumCanonicalStatuses")
            .WithSummary("Batch link-status (linked / localOnly / pending) for a list of albums, for the library grid badges.")
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

        // Not linked yet: report the state (localOnly vs still-pending) so the UI can be explicit
        // instead of silently falling back. The frontend renders its owned-only view either way.
        if (canonical is null || canonical.Status != CanonicalAlbumStatus.Fetched)
            return Results.Ok(new { status = DeriveStatus(canonical), providers = Array.Empty<string>() });

        // Owned songs in this album group, mirroring the frontend's (albumArtist ?? artist, album) grouping.
        var artistLower = artist.ToLowerInvariant();
        var albumLower = album.ToLowerInvariant();
        var ownedSongs = await db.Songs
            .AsNoTracking()
            .Where(s => s.DeletedAtUtc == null
                && s.Album != null && s.Album.ToLower() == albumLower
                && ((s.AlbumArtist ?? s.Artist) ?? "").ToLower() == artistLower)
            .Select(s => new OwnedTrackInfo(s.Id, s.MusicBrainzId, s.DiscNumber, s.TrackNumber, s.Title))
            .ToListAsync(ct);

        var orderedTracks = canonical.Tracks
            .OrderBy(t => t.DiscNumber)
            .ThenBy(t => t.TrackNumber)
            .ToList();

        var matches = AlbumOwnedTrackMatcher.Match(orderedTracks, ownedSongs, options.Value.IdentityTitleThreshold);

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
            status = "linked",
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

    internal static async Task<IResult> GetCanonicalStatuses(
        CanonicalStatusRequest request,
        MusicHoarderDbContext db,
        CancellationToken ct)
    {
        var albums = request.Albums ?? [];
        if (albums.Count == 0)
            return Results.Ok(Array.Empty<object>());

        // Normalize each requested pair the same way storage does, so the lookup matches exactly.
        var keyed = albums
            .Select(a => (a.Artist, a.Album,
                ArtistKey: TitleNormalizer.NormalizeForSearch(a.Artist),
                AlbumKey: TitleNormalizer.NormalizeForSearch(a.Album)))
            .ToList();

        var artistKeys = keyed.Select(k => k.ArtistKey).Where(k => k.Length > 0).Distinct().ToList();
        var rows = await db.CanonicalAlbums
            .AsNoTracking()
            .Where(a => artistKeys.Contains(a.ArtistKey))
            .Select(a => new { a.Id, a.ArtistKey, a.AlbumKey, a.Status, a.SourcesJson })
            .ToListAsync(ct);
        var byKey = rows.ToDictionary(r => (r.ArtistKey, r.AlbumKey));

        // Latest reconciliation verdict per album (owner-filtered) so the grid can flag Wrong albums.
        var albumIds = rows.Select(r => r.Id).ToList();
        var grades = await db.CanonicalAlbumQualityGrades
            .AsNoTracking()
            .Where(g => albumIds.Contains(g.CanonicalAlbumId)
                && !db.CanonicalAlbumQualityGrades.Any(g2 => g2.CanonicalAlbumId == g.CanonicalAlbumId && g2.GradedAtUtc > g.GradedAtUtc))
            .Select(g => new { g.CanonicalAlbumId, g.Verdict })
            .ToListAsync(ct);
        var verdictByAlbum = grades.ToDictionary(g => g.CanonicalAlbumId, g => g.Verdict.ToString());

        var results = keyed.Select(k =>
        {
            byKey.TryGetValue((k.ArtistKey, k.AlbumKey), out var row);
            var status = row is null
                ? "pending"
                : row.Status switch
                {
                    CanonicalAlbumStatus.Fetched => "linked",
                    CanonicalAlbumStatus.NotFound => "localOnly",
                    _ => "pending",
                };
            return (object)new
            {
                artist = k.Artist,
                album = k.Album,
                status,
                providers = status == "linked" ? WinningProviderNames(row!.SourcesJson) : [],
                verdict = row is not null && verdictByAlbum.TryGetValue(row.Id, out var v) ? v : null,
            };
        }).ToArray();

        return Results.Ok(results);
    }

    /// <summary>linked = Fetched, localOnly = NotFound, pending = no row / Pending / Failed.</summary>
    private static string DeriveStatus(CanonicalAlbum? row) => row?.Status switch
    {
        CanonicalAlbumStatus.Fetched => "linked",
        CanonicalAlbumStatus.NotFound => "localOnly",
        _ => "pending",
    };

    /// <summary>Distinct provider names that won the reconciliation cluster (for the UI badge/chip).</summary>
    private static string[] WinningProviderNames(string? sourcesJson)
    {
        if (string.IsNullOrWhiteSpace(sourcesJson))
            return [];
        try
        {
            var stored = JsonSerializer.Deserialize<List<StoredSource>>(sourcesJson);
            return stored is null
                ? []
                : stored.Where(s => s.InWinningCluster).Select(s => s.Provider.ToString()).Distinct().ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public sealed record CanonicalStatusRequest(List<AlbumIdentity>? Albums);
    public sealed record AlbumIdentity(string Artist, string Album);

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

    private sealed record StoredSource(EnrichmentProvider Provider, string? AlbumId, int TrackCount, bool InWinningCluster);
}
