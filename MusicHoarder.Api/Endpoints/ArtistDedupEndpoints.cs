using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Library;

namespace MusicHoarder.Api.Endpoints;

public record ArtistMergeRequest(string CanonicalName, string[] VariantNames);

public record ArtistSplitCreditRequest(string CreditName);

public record ArtistDismissRequest(string[] Names);

/// <summary>
/// Artist-level dedup: detect variant spellings of one artist ("JAY-Z" / "JAYZ" / "Jaÿ-z") and
/// combined credits registered as a single artist ("JAY-Z &amp; Kanye West", an album artist of
/// "Hef met Jayh"), merge spellings onto a canonical one or split a credit into its artists (tags
/// are rewritten via the re-tag pipeline), or dismiss false positives.
/// </summary>
public static class ArtistDedupEndpoints
{
    public static IEndpointRouteBuilder MapArtistDedupEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/library/artists/duplicates", Detect)
            .WithName("GetArtistDuplicates")
            .WithSummary("Clusters of artist-name spellings that likely refer to the same artist, plus combined-credit candidates.")
            .WithTags("Library").RequireAdmin();

        app.MapPost("/api/library/artists/merge", Merge)
            .WithName("MergeArtists")
            .WithSummary("Merge variant spellings onto a canonical artist name; rewrites tags and re-queues built files for re-tag.")
            .WithTags("Library").RequireAdmin();

        app.MapPost("/api/library/artists/split-credit", SplitCredit)
            .WithName("SplitArtistCredit")
            .WithSummary("Split a combined credit (\"A & B\", \"Hef met Jayh\"): backfill blank discrete Artists lists with its artists and move an album artist equal to the credit to the lead; re-queues built files for re-tag.")
            .WithTags("Library").RequireAdmin();

        app.MapPost("/api/library/artists/dismiss", Dismiss)
            .WithName("DismissArtistDuplicates")
            .WithSummary("Mark artist-name spellings as NOT the same artist; the decision persists across detections.")
            .WithTags("Library").RequireAdmin();

        return app;
    }

    internal static async Task<IResult> Detect(
        IArtistDuplicateService service, ICurrentUserAccessor currentUser, CancellationToken ct)
    {
        var report = await service.DetectAsync(currentUser.UserId, ct);
        return Results.Ok(report);
    }

    internal static async Task<IResult> Merge(
        ArtistMergeRequest request, IArtistDuplicateService service, ICurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CanonicalName))
            return Results.BadRequest(new { message = "A canonical name is required." });
        if (request.VariantNames is not { Length: > 0 })
            return Results.BadRequest(new { message = "At least one variant name is required." });

        try
        {
            var result = await service.MergeAsync(currentUser.UserId, request.CanonicalName, request.VariantNames, ct);
            return Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    internal static async Task<IResult> SplitCredit(
        ArtistSplitCreditRequest request, IArtistDuplicateService service, ICurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CreditName))
            return Results.BadRequest(new { message = "A credit name is required." });

        try
        {
            var result = await service.SplitCreditAsync(currentUser.UserId, request.CreditName, ct);
            return Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    internal static async Task<IResult> Dismiss(
        ArtistDismissRequest request, IArtistDuplicateService service, ICurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        if (request.Names is not { Length: >= 2 })
            return Results.BadRequest(new { message = "At least two names are required." });

        var added = await service.DismissAsync(currentUser.UserId, request.Names, ct);
        return Results.Ok(new { PairsDismissed = added });
    }
}
