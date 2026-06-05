using MusicHoarder.Api.Snapshots;
using MusicHoarder.Api.Version;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// Exposes the running build's version (the semantic-release / Docker-tag semver baked into the
/// assembly at release time) and the latest published release. Anonymous so the unauthenticated
/// marketing page can read it — both paths ride the <c>/api/version</c> prefix allowlisted in
/// <see cref="Auth.Middleware.RequireAuthMiddleware"/>.
/// </summary>
public static class VersionEndpoints
{
    public static IEndpointRouteBuilder MapVersionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/version", GetVersion)
            .WithName("GetVersion")
            .WithSummary("The running build's version (clean semver, matching the GitHub release / Docker tag).")
            .WithTags("Version")
            .AllowAnonymous();

        app.MapGet("/api/version/latest", GetLatestVersion)
            .WithName("GetLatestVersion")
            .WithSummary("Latest published release (cached GitHub check) compared against the running build.")
            .WithTags("Version")
            .AllowAnonymous();

        return app;
    }

    // Reuse the same resolution the snapshot timeline uses so /api/version and the snapshots agree.
    private static IResult GetVersion() =>
        Results.Ok(new { version = EnrichmentSnapshotService.ResolveVersion(null) });

    internal static IResult GetLatestVersion(IReleaseUpdateMonitor monitor)
    {
        var current = EnrichmentSnapshotService.ResolveVersion(null);
        var snapshot = monitor.Current;
        return Results.Ok(new
        {
            current,
            latest = snapshot.LatestVersion,
            updateAvailable = SemVerComparer.IsUpdateAvailable(current, snapshot.LatestVersion),
            releaseUrl = snapshot.ReleaseUrl,
            publishedAt = snapshot.PublishedAt,
        });
    }
}
