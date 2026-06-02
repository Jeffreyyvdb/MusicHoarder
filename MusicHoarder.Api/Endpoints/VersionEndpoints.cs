using MusicHoarder.Api.Snapshots;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// Exposes the running build's version (the semantic-release / Docker-tag semver baked into the
/// assembly at release time). Anonymous so the unauthenticated marketing page can read it — the path
/// is allowlisted in <see cref="Auth.Middleware.RequireAuthMiddleware"/>.
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

        return app;
    }

    // Reuse the same resolution the snapshot timeline uses so /api/version and the snapshots agree.
    private static IResult GetVersion() =>
        Results.Ok(new { version = EnrichmentSnapshotService.ResolveVersion(null) });
}
