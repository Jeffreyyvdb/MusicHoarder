using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Artists;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// An artist's discography (every release the catalog lists, against what the owner has), "Get album"
/// / "Get discography", and following artists for their new releases. Admin-only: these queue
/// downloads, and the discography reads a third-party catalog for the instance.
/// </summary>
public static class ArtistDiscographyEndpoints
{
    public static IEndpointRouteBuilder MapArtistDiscographyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/artists").RequireAdmin().WithTags("Artists");

        group.MapGet("/discography", GetDiscography)
            .WithSummary("The artist's catalog releases, folded by edition, with what the owner has of each.");
        group.MapPost("/catalog-link", ChooseCatalogArtist)
            .WithSummary("Says which catalog artist a library artist is, when the name alone could not prove it.");
        group.MapPost("/acquire", Acquire)
            .WithSummary("Queues the missing tracks of one or more releases (\"Get album\", \"Get discography\").");
        group.MapGet("/search", Search)
            .WithSummary("Catalog artists by name, for following an artist that is not in the library yet.");

        group.MapGet("/following", ListFollowing)
            .WithSummary("Followed artists and the releases the monitor found for them.");
        group.MapPost("/following", Follow)
            .WithSummary("Follows a catalog artist; what it has released so far becomes the baseline.");
        group.MapPatch("/following/{id:int}", UpdateFollow);
        group.MapDelete("/following/{id:int}", Unfollow);
        group.MapPost("/following/{id:int}/check", CheckOne)
            .WithSummary("Checks one followed artist for new releases now.");

        return app;
    }

    internal static async Task<IResult> GetDiscography(string? artist, ArtistDiscographyService discography, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return Results.BadRequest(new { message = "artist is required" });
        return Results.Ok(await discography.BuildAsync(artist.Trim(), ct));
    }

    public sealed record CatalogLinkRequest(string Artist, string DeezerArtistId);

    internal static async Task<IResult> ChooseCatalogArtist(
        CatalogLinkRequest body, ArtistCatalogResolver resolver, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Artist) || string.IsNullOrWhiteSpace(body.DeezerArtistId))
            return Results.BadRequest(new { message = "artist and deezerArtistId are required" });
        var link = await resolver.ChooseAsync(body.Artist.Trim(), body.DeezerArtistId.Trim(), ct);
        return link is null
            ? Results.NotFound(new { message = "The catalog has no artist with that id." })
            : Results.Ok(new { status = "chosen", link.DeezerArtistId, link.Name });
    }

    public sealed record AcquireRelease(string? Key, IReadOnlyList<string> EditionIds, string? Kind);

    public sealed record AcquireRequest(string Artist, IReadOnlyList<AcquireRelease> Releases);

    public sealed record AcquireReleaseResult(
        string? Key, string? Title, int Queued, int Requeued, int AlreadyOwned, int AlreadyQueued, int Capped, string? Error);

    internal static async Task<IResult> Acquire(
        AcquireRequest body,
        ArtistReleaseQueuer queuer,
        ICurrentUserAccessor currentUser,
        JobManager jobManager,
        IOptions<MusicEnricherOptions> options,
        CancellationToken ct)
    {
        var opts = options.Value;
        if (!opts.EnableWishlistDownloads)
            return Results.Conflict(new { message = "Downloads are off on this server (MusicEnricher:EnableWishlistDownloads)." });
        if (string.IsNullOrWhiteSpace(body.Artist) || body.Releases is not { Count: > 0 })
            return Results.BadRequest(new { message = "artist and at least one release are required" });

        var budget = Math.Max(1, opts.ArtistRequestMaxTracks);
        var results = new List<AcquireReleaseResult>(body.Releases.Count);
        var skipped = 0;
        foreach (var release in body.Releases.Take(200))
        {
            // Budget spent: reading the rest from the catalog would only be to report them as capped.
            if (budget <= 0)
            {
                skipped++;
                continue;
            }

            if (release.EditionIds is not { Count: > 0 })
            {
                results.Add(new AcquireReleaseResult(release.Key, null, 0, 0, 0, 0, 0, "No edition ids."));
                continue;
            }

            var result = await queuer.QueueAsync(
                currentUser.UserId, body.Artist.Trim(), release.EditionIds, ArtistDiscography.KindOf(release.Kind),
                WishlistItemOrigin.ArtistRequest, budget, requeueFailed: true, ct);
            budget -= result.Queued;
            results.Add(new AcquireReleaseResult(
                release.Key, result.Title, result.Queued, result.Requeued, result.AlreadyOwned,
                result.AlreadyQueued, result.Capped, result.Error));
        }

        var queued = results.Sum(r => r.Queued + r.Requeued);
        var jobStarted = queued > 0 && jobManager.TryStartJob(JobType.Download, out _, out _);
        return Results.Ok(new
        {
            queued,
            capped = results.Sum(r => r.Capped),
            skippedReleases = skipped,
            jobStarted,
            results,
        });
    }

    internal static async Task<IResult> Search(string? q, IDeezerCatalogService deezer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Results.Ok(new { artists = Array.Empty<ArtistCandidateDto>() });
        var found = await deezer.SearchArtistCandidatesAsync(q.Trim(), ct);
        return Results.Ok(new
        {
            artists = found
                .Where(c => !string.IsNullOrEmpty(c.Id))
                .Select(c => new ArtistCandidateDto(c.Id!, c.Name, c.PictureUrl, c.Fans, c.AlbumCount))
                .ToList(),
        });
    }

    public sealed record FollowedReleaseDto(
        int Id,
        int FollowId,
        string Artist,
        string Title,
        string Kind,
        DateOnly? ReleaseDate,
        string? CoverUrl,
        string Decision,
        int TracksQueued,
        DateTime DecidedAtUtc);

    internal static async Task<IResult> ListFollowing(
        MusicHoarderDbContext db, IOptions<MusicEnricherOptions> options, CancellationToken ct)
    {
        var follows = await db.FollowedArtists.AsNoTracking().OrderBy(f => f.Name).ToListAsync(ct);
        var news = FollowedReleaseDecisions.News;
        var counts = await db.FollowedArtistReleases.AsNoTracking()
            .Where(r => news.Contains(r.Decision))
            .GroupBy(r => r.FollowedArtistId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var recent = await db.FollowedArtistReleases.AsNoTracking()
            .Where(r => news.Contains(r.Decision))
            .OrderByDescending(r => r.DecidedAtUtc)
            .Take(30)
            .Select(r => new
            {
                r.Id, r.FollowedArtistId, Artist = r.FollowedArtist.Name, r.Title, r.Kind, r.ReleaseDate,
                r.CoverUrl, r.Decision, r.TracksQueued, r.DecidedAtUtc,
            })
            .ToListAsync(ct);

        return Results.Ok(new
        {
            follows = follows.Select(f => ArtistDiscographyService.ToDto(f, counts.GetValueOrDefault(f.Id))).ToList(),
            recent = recent.Select(r => new FollowedReleaseDto(
                r.Id, r.FollowedArtistId, r.Artist, r.Title, ArtistDiscography.WireKind(r.Kind), r.ReleaseDate,
                r.CoverUrl, DecisionName(r.Decision), r.TracksQueued, r.DecidedAtUtc)).ToList(),
            monitorEnabled = options.Value.EnableArtistMonitor,
            downloadsEnabled = options.Value.EnableWishlistDownloads,
        });
    }

    public sealed record FollowRequest(string DeezerArtistId, string? Artist, IReadOnlyList<string>? ReleaseKinds, bool? AutoDownload);

    internal static async Task<IResult> Follow(
        FollowRequest body,
        MusicHoarderDbContext db,
        IDeezerCatalogService deezer,
        ArtistMonitorSweep monitor,
        ArtistDiscographyService discography,
        ICurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.DeezerArtistId))
            return Results.BadRequest(new { message = "deezerArtistId is required" });

        var deezerId = body.DeezerArtistId.Trim();
        var existing = await db.FollowedArtists.FirstOrDefaultAsync(f => f.DeezerArtistId == deezerId, ct);
        if (existing is not null)
            return Results.Ok(await discography.ToDtoAsync(existing, ct));

        var artist = await deezer.GetArtistAsync(deezerId, ct);
        if (artist is null || string.IsNullOrWhiteSpace(artist.Name))
            return Results.NotFound(new { message = "The catalog has no artist with that id." });

        var kinds = ArtistDiscographyService.ParseKinds(body.ReleaseKinds);
        var follow = new FollowedArtist
        {
            OwnerUserId = currentUser.UserId,
            DeezerArtistId = deezerId,
            Name = Truncate(artist.Name!, 512),
            // The page the follow was made from finds it by its own spelling of the name.
            ArtistKey = Truncate(TitleNormalizer.NormalizeForSearch(
                string.IsNullOrWhiteSpace(body.Artist) ? artist.Name : body.Artist), 512),
            PictureUrl = artist.PictureUrl is null ? null : Truncate(artist.PictureUrl, 1024),
            ReleaseKinds = kinds == ArtistReleaseKinds.None ? ArtistReleaseKinds.Album | ArtistReleaseKinds.Ep : kinds,
            AutoDownload = body.AutoDownload ?? true,
            FollowedAtUtc = DateTime.UtcNow,
        };
        db.FollowedArtists.Add(follow);
        await db.SaveChangesAsync(ct);

        // The baseline, now: everything already out is recorded and left alone. If the catalog cannot
        // be read the first scheduled check records it instead (LastCheckedAtUtc stays null until then).
        await monitor.CheckAsync(follow.Id, DateTime.UtcNow, ct);
        await db.Entry(follow).ReloadAsync(ct);

        return Results.Ok(await discography.ToDtoAsync(follow, ct));
    }

    public sealed record UpdateFollowRequest(IReadOnlyList<string>? ReleaseKinds, bool? AutoDownload);

    internal static async Task<IResult> UpdateFollow(
        int id, UpdateFollowRequest body, MusicHoarderDbContext db, ArtistDiscographyService discography, CancellationToken ct)
    {
        var follow = await db.FollowedArtists.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (follow is null) return Results.NotFound();

        if (body.ReleaseKinds is not null)
        {
            var kinds = ArtistDiscographyService.ParseKinds(body.ReleaseKinds);
            if (kinds == ArtistReleaseKinds.None)
                return Results.BadRequest(new { message = "Pick at least one kind of release." });
            follow.ReleaseKinds = kinds;
        }

        if (body.AutoDownload is { } auto) follow.AutoDownload = auto;
        await db.SaveChangesAsync(ct);
        return Results.Ok(await discography.ToDtoAsync(follow, ct));
    }

    internal static async Task<IResult> Unfollow(int id, MusicHoarderDbContext db, CancellationToken ct)
    {
        var follow = await db.FollowedArtists.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (follow is null) return Results.NotFound();
        // The releases go with it; anything it queued stays on the wishlist (no foreign key to the follow).
        db.FollowedArtists.Remove(follow);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    internal static async Task<IResult> CheckOne(
        int id, MusicHoarderDbContext db, ArtistMonitorSweep monitor, IOptions<MusicEnricherOptions> options, CancellationToken ct)
    {
        if (!options.Value.EnableArtistMonitor)
            return Results.Ok(ArtistMonitorResult.Idle(ArtistMonitorResult.IdleDisabled));
        // The ambient filter makes this the caller's own follow, or nothing.
        if (!await db.FollowedArtists.AnyAsync(f => f.Id == id, ct)) return Results.NotFound();
        return Results.Ok(await monitor.CheckAsync(id, DateTime.UtcNow, ct));
    }

    private static string DecisionName(FollowedReleaseDecision decision) => decision switch
    {
        FollowedReleaseDecision.Queued => "queued",
        FollowedReleaseDecision.AlreadyOwned => "owned",
        FollowedReleaseDecision.Noticed => "noticed",
        FollowedReleaseDecision.SkippedOld => "skipped-old",
        FollowedReleaseDecision.Failed => "failed",
        _ => "baseline",
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
