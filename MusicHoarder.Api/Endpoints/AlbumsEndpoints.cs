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
        // The read-only album GETs deliberately have no RequireOwner: the demo account (UserRole.Demo)
        // must be able to browse album data too. Auth is still mandatory (RequireAuthMiddleware) and
        // every owner-scoped table is filtered by the EF global query filter — same posture as the
        // songs GETs.
        app.MapGet("/api/albums/tracklist", GetAlbumTracklist)
            .WithName("GetAlbumTracklist")
            .WithSummary("Reconciled multi-provider canonical tracklist for an album, each track annotated with the owned song (if any).")
            .WithTags("Library");

        app.MapPost("/api/albums/canonical-status", GetCanonicalStatuses)
            .WithName("GetAlbumCanonicalStatuses")
            .WithSummary("Batch link-status (linked / localOnly / pending) for a list of albums, for the library grid badges.")
            .WithTags("Library")
            .RequireOwner();

        app.MapGet("/api/albums/timeline", GetAlbumTimeline)
            .WithName("GetAlbumTimeline")
            .WithSummary("Chronological provenance timeline for an album: discovery, per-provider enrichment rollups, canonical resolution, AI grades, and destination writes.")
            .WithTags("Library");

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

    /// <summary>
    /// Assembles the album's provenance timeline server-side (the inputs span five tables none of
    /// which the album page fetches raw). Per-track noise is rolled up: one event per enrichment
    /// provider ("matched x of y tracks"), one per write-event summary (via the History feed's
    /// <see cref="HistoryEndpoints.RollUp"/>), one per AI grade row. All grouping happens in memory
    /// after materializing (keeps the EF InMemory test provider happy and the queries trivial).
    /// </summary>
    internal static async Task<IResult> GetAlbumTimeline(
        string artist,
        string album,
        MusicHoarderDbContext db,
        CancellationToken ct)
    {
        var artistKey = TitleNormalizer.NormalizeForSearch(artist);
        var albumKey = TitleNormalizer.NormalizeForSearch(album);
        if (artistKey.Length == 0 || albumKey.Length == 0)
            return Results.NotFound(new { message = "Album not specified." });

        // Member songs resolved exactly like the tracklist endpoint: the frontend's
        // (albumArtist ?? artist, album) grouping. Owner-scoped via the EF global query filter.
        var artistLower = artist.ToLowerInvariant();
        var albumLower = album.ToLowerInvariant();
        var members = await db.Songs
            .AsNoTracking()
            .Where(s => s.DeletedAtUtc == null
                && s.Album != null && s.Album.ToLower() == albumLower
                && ((s.AlbumArtist ?? s.Artist) ?? "").ToLower() == artistLower)
            .Select(s => new MemberSong(s.Id, s.Title, s.IndexedAtUtc, s.LibraryBuiltAtUtc, s.ManuallyApprovedAtUtc, s.DestinationPath))
            .ToListAsync(ct);

        if (members.Count == 0)
            return Results.NotFound(new { message = "Album not found." });

        var memberIds = members.Select(m => m.Id).ToList();
        var trackCount = members.Count;
        var events = new List<AlbumTimelineEvent>
        {
            new(
                Key: "discovered",
                TimeUtc: members.Min(m => m.IndexedAtUtc),
                Stage: "SCAN",
                Tint: "neutral",
                Provider: null,
                Pct: null,
                Description: $"Discovered {Plural(trackCount, "track")} in source",
                MatchedCount: null,
                TotalCount: trackCount,
                FirstAtUtc: members.Min(m => m.IndexedAtUtc),
                LastAtUtc: members.Max(m => m.IndexedAtUtc)),
        };

        // One rollup event per enrichment provider that touched any member track.
        var attempts = await db.SongProviderAttempts
            .AsNoTracking()
            .Where(a => memberIds.Contains(a.SongId))
            .Select(a => new { a.SongId, a.Provider, a.Status, a.AttemptedAtUtc })
            .ToListAsync(ct);
        foreach (var group in attempts.GroupBy(a => a.Provider))
        {
            var matched = group.Where(a => a.Status == ProviderAttemptStatus.Matched)
                .Select(a => a.SongId).Distinct().Count();
            events.Add(new AlbumTimelineEvent(
                Key: $"provider:{group.Key}",
                TimeUtc: group.Max(a => a.AttemptedAtUtc),
                Stage: "METADATA",
                Tint: matched == trackCount ? "ok" : matched > 0 ? "warn" : "neutral",
                Provider: group.Key.ToString(),
                Pct: null,
                Description: $"Matched {matched} of {Plural(trackCount, "track")}",
                MatchedCount: matched,
                TotalCount: trackCount,
                FirstAtUtc: group.Min(a => a.AttemptedAtUtc),
                LastAtUtc: group.Max(a => a.AttemptedAtUtc)));
        }

        // Canonical album resolution — NotFound/Failed are provenance too, so no Fetched filter.
        var canonical = await db.CanonicalAlbums
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.ArtistKey == artistKey && a.AlbumKey == albumKey, ct);
        if (canonical?.FetchedAtUtc is { } fetchedAt)
        {
            events.Add(canonical.Status switch
            {
                CanonicalAlbumStatus.Fetched => new AlbumTimelineEvent(
                    Key: "canonical",
                    TimeUtc: fetchedAt,
                    Stage: "CANONICAL",
                    Tint: canonical.TrackCountContested ? "warn" : "ok",
                    Provider: null,
                    Pct: null,
                    Description: DescribeCanonical(canonical),
                    MatchedCount: null,
                    TotalCount: canonical.ResolvedTrackCount,
                    FirstAtUtc: null,
                    LastAtUtc: null),
                CanonicalAlbumStatus.Failed => new AlbumTimelineEvent(
                    "canonical", fetchedAt, "CANONICAL", "err", null, null,
                    "Canonical album fetch failed", null, null, null, null),
                _ => new AlbumTimelineEvent(
                    "canonical", fetchedAt, "CANONICAL", "neutral", null, null,
                    "No matching album found on any provider", null, null, null, null),
            });

            // Every grade row is one event (re-grades insert new rows, so this is the grade history).
            var grades = await db.CanonicalAlbumQualityGrades
                .AsNoTracking()
                .Where(g => g.CanonicalAlbumId == canonical.Id)
                .ToListAsync(ct);
            foreach (var grade in grades.OrderBy(g => g.GradedAtUtc))
            {
                events.Add(new AlbumTimelineEvent(
                    Key: $"grade:{grade.Id}",
                    TimeUtc: grade.GradedAtUtc,
                    Stage: "AI GRADE",
                    Tint: grade.Verdict switch
                    {
                        SongQualityVerdict.Wrong => "err",
                        SongQualityVerdict.Questionable => "warn",
                        SongQualityVerdict.Ungradeable => "neutral",
                        _ => "ok",
                    },
                    Provider: grade.Model,
                    Pct: grade.Score,
                    Description: string.IsNullOrWhiteSpace(grade.Summary)
                        ? $"Match graded {grade.Verdict}"
                        : $"Match graded {grade.Verdict} — {grade.Summary}",
                    MatchedCount: null,
                    TotalCount: null,
                    FirstAtUtc: null,
                    LastAtUtc: null));
            }
        }

        // Manual approvals, aggregated.
        var approved = members.Where(m => m.ManuallyApprovedAtUtc is not null).ToList();
        if (approved.Count > 0)
        {
            events.Add(new AlbumTimelineEvent(
                Key: "approved",
                TimeUtc: approved.Max(m => m.ManuallyApprovedAtUtc!.Value),
                Stage: "APPROVED",
                Tint: "ok",
                Provider: null,
                Pct: null,
                Description: $"{approved.Count} of {Plural(trackCount, "track")} manually approved",
                MatchedCount: approved.Count,
                TotalCount: trackCount,
                FirstAtUtc: approved.Min(m => m.ManuallyApprovedAtUtc!.Value),
                LastAtUtc: approved.Max(m => m.ManuallyApprovedAtUtc!.Value)));
        }

        // First build. Write events only capture changed fields, so a clean first build can have
        // few/no LibraryWriteEvent rows — this synthetic event marks the album landing on disk.
        var built = members.Where(m => m.LibraryBuiltAtUtc is not null).ToList();
        if (built.Count > 0)
        {
            events.Add(new AlbumTimelineEvent(
                Key: "built",
                TimeUtc: built.Min(m => m.LibraryBuiltAtUtc!.Value),
                Stage: "WRITE",
                Tint: "ok",
                Provider: null,
                Pct: null,
                Description: $"{built.Count} of {Plural(trackCount, "track")} written to destination",
                MatchedCount: built.Count,
                TotalCount: trackCount,
                FirstAtUtc: built.Min(m => m.LibraryBuiltAtUtc!.Value),
                LastAtUtc: built.Max(m => m.LibraryBuiltAtUtc!.Value)));
        }

        // Destination writes (re-tags, consolidations, renames, covers), filtered by member ids +
        // destination folders rather than name equality so events survive album/artist renames.
        var albumFolders = members
            .Select(m => m.DestinationPath is null ? null : Path.GetDirectoryName(m.DestinationPath))
            .Where(f => !string.IsNullOrEmpty(f))
            .Distinct()
            .ToList();
        var writeEvents = await db.LibraryWriteEvents
            .AsNoTracking()
            .Where(e => (e.SongId != null && memberIds.Contains(e.SongId.Value))
                || (e.Kind == LibraryWriteEventKind.AlbumCoverWritten && e.AlbumFolder != null && albumFolders.Contains(e.AlbumFolder)))
            .ToListAsync(ct);
        if (writeEvents.Count > 0)
        {
            var titleById = members.ToDictionary(m => m.Id, m => m.Title);
            foreach (var summary in HistoryEndpoints.RollUp(writeEvents, titleById))
            {
                var (stage, tint) = summary.Kind switch
                {
                    "consolidation" => ("CONSOLIDATE", "ok"),
                    "artist-rename" => ("RENAME", "ok"),
                    "year-correction" => ("YEAR FIX", "ok"),
                    "cover" => ("COVER", "info"),
                    _ => ("WRITE", "ok"),
                };
                events.Add(new AlbumTimelineEvent(
                    Key: $"write:{summary.Id}",
                    TimeUtc: summary.LatestWrittenAtUtc,
                    Stage: stage,
                    Tint: tint,
                    Provider: null,
                    Pct: null,
                    Description: summary.Headline,
                    MatchedCount: summary.TrackCount,
                    TotalCount: trackCount,
                    FirstAtUtc: summary.Changes.Min(c => c.WrittenAtUtc),
                    LastAtUtc: summary.LatestWrittenAtUtc));
            }
        }

        // External cover fetch failures (success leaves no row — the cover.* file is the marker).
        var coverFailures = await db.AlbumCoverFetchAttempts
            .AsNoTracking()
            .Where(a => albumFolders.Contains(a.AlbumFolder))
            .ToListAsync(ct);
        foreach (var failure in coverFailures)
        {
            events.Add(new AlbumTimelineEvent(
                Key: $"cover-fetch:{failure.Id}",
                TimeUtc: failure.LastAttemptAtUtc,
                Stage: "COVER",
                Tint: "warn",
                Provider: null,
                Pct: null,
                Description: failure.Status == AlbumCoverFetchStatus.NotFound
                    ? $"No external cover art found after {Plural(failure.AttemptCount, "attempt")}"
                    : $"External cover art fetch failed after {Plural(failure.AttemptCount, "attempt")}",
                MatchedCount: null,
                TotalCount: null,
                FirstAtUtc: null,
                LastAtUtc: null));
        }

        return Results.Ok(new AlbumTimelineResponse(
            trackCount,
            events.OrderBy(e => e.TimeUtc).ThenBy(e => e.Key).ToList()));
    }

    private static string DescribeCanonical(CanonicalAlbum canonical)
    {
        var winners = WinningProviderNames(canonical.SourcesJson);
        var description = $"Canonical album resolved · {Plural(canonical.ResolvedTrackCount, "track")}";
        if (winners.Length > 0)
            description += $" · from {string.Join(", ", winners)}";
        if (canonical.TrackCountContested)
            description += " · track count contested";
        return description;
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private sealed record MemberSong(
        int Id,
        string? Title,
        DateTime IndexedAtUtc,
        DateTime? LibraryBuiltAtUtc,
        DateTime? ManuallyApprovedAtUtc,
        string? DestinationPath);

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

/// <summary>
/// One event on the album provenance timeline. <see cref="Stage"/>/<see cref="Tint"/> mirror the
/// track timeline's vocabulary so the frontend reuses the same rendering; rollup events carry the
/// x/y counts and first/last span instead of per-track detail.
/// </summary>
public record AlbumTimelineEvent(
    string Key,
    DateTime TimeUtc,
    string Stage,
    string Tint,
    string? Provider,
    int? Pct,
    string Description,
    int? MatchedCount,
    int? TotalCount,
    DateTime? FirstAtUtc,
    DateTime? LastAtUtc);

public record AlbumTimelineResponse(int TrackCount, IReadOnlyList<AlbumTimelineEvent> Events);
