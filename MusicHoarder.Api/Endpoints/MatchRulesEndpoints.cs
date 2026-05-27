using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Settings;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// Owner-only CRUD for user-defined metadata match rules, plus a stateless "test" endpoint that
/// powers the live pattern preview in the Settings UI. Mounted under <c>/api/settings/match-rules</c>.
/// </summary>
public static class MatchRulesEndpoints
{
    public static IEndpointRouteBuilder MapMatchRulesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings/match-rules").WithTags("Settings").RequireOwner();

        group.MapGet("", async (IMatchRuleService rules, CancellationToken ct) =>
            {
                var list = await rules.ListAsync(ct);
                return Results.Ok(list.Select(ToView));
            })
            .WithName("ListMatchRules")
            .WithSummary("Lists all metadata match rules, ordered by priority.");

        group.MapPost("", async (MatchRuleRequest request, IMatchRuleService rules, CancellationToken ct) =>
            {
                var (input, error) = Validate(request);
                if (input is null)
                    return Results.BadRequest(new { message = error });

                var created = await rules.CreateAsync(input, ct);
                return Results.Created($"/api/settings/match-rules/{created.Id}", ToView(created));
            })
            .WithName("CreateMatchRule")
            .WithSummary("Creates a metadata match rule. Validates the template pattern.");

        group.MapPut("/{id:int}", async (int id, MatchRuleRequest request, IMatchRuleService rules, CancellationToken ct) =>
            {
                var (input, error) = Validate(request);
                if (input is null)
                    return Results.BadRequest(new { message = error });

                var updated = await rules.UpdateAsync(id, input, ct);
                return updated is null ? Results.NotFound() : Results.Ok(ToView(updated));
            })
            .WithName("UpdateMatchRule")
            .WithSummary("Updates a metadata match rule. Validates the template pattern.");

        group.MapDelete("/{id:int}", async (int id, IMatchRuleService rules, CancellationToken ct) =>
            {
                var deleted = await rules.DeleteAsync(id, ct);
                return deleted ? Results.NoContent() : Results.NotFound();
            })
            .WithName("DeleteMatchRule")
            .WithSummary("Deletes a metadata match rule.");

        group.MapPost("/suggest", async (IMatchRuleSuggestionService suggester, CancellationToken ct) =>
            {
                var result = await suggester.SuggestAsync(ct);
                return Results.Ok(result);
            })
            .WithName("SuggestMatchRules")
            .WithSummary("Analyzes currently-unmatched songs and proposes match-rule presets (LLM-assisted, with a deterministic fallback).");

        group.MapPost("/test", (MatchRuleTestRequest request) =>
            {
                if (!MatchRulePattern.TryCompile(request.Pattern, out var compiled, out var error))
                    return Results.Ok(new MatchRuleTestResponse(Valid: false, Error: error, Matched: false, Extracted: null));

                var extraction = MatchRulePattern.Match(compiled!, request.Sample);
                return Results.Ok(extraction is null
                    ? new MatchRuleTestResponse(Valid: true, Error: null, Matched: false, Extracted: null)
                    : new MatchRuleTestResponse(
                        Valid: true,
                        Error: null,
                        Matched: true,
                        Extracted: new MatchRuleExtractionView(extraction.Artist, extraction.Title, extraction.Album, extraction.AlbumArtist)));
            })
            .WithName("TestMatchRule")
            .WithSummary("Compiles a template pattern and tests it against a sample string, returning the captured fields.");

        return app;
    }

    private static (MatchRuleInput? Input, string? Error) Validate(MatchRuleRequest? request)
    {
        if (request is null)
            return (null, "Request body required.");
        if (string.IsNullOrWhiteSpace(request.Name))
            return (null, "Name is required.");
        if (!MatchRulePattern.TryCompile(request.Pattern, out _, out var patternError))
            return (null, patternError);
        if (!TryParseSourceField(request.SourceField, out var sourceField))
            return (null, "SourceField must be 'title' or 'filename'.");

        return (new MatchRuleInput(
            Name: request.Name!,
            Pattern: request.Pattern!,
            SourceField: sourceField,
            Enabled: request.Enabled ?? true,
            Priority: request.Priority ?? 100,
            AlbumOverride: TrimToNull(request.AlbumOverride),
            AlbumArtistOverride: TrimToNull(request.AlbumArtistOverride)), null);
    }

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static MatchRuleView ToView(MetadataMatchRule r) => new(
        r.Id, r.Name, r.Enabled, r.Priority, r.Pattern, SourceFieldToString(r.SourceField),
        r.AlbumOverride, r.AlbumArtistOverride, r.CreatedAtUtc, r.UpdatedAtUtc);

    private static string SourceFieldToString(MatchRuleSourceField field) =>
        field == MatchRuleSourceField.FileName ? "filename" : "title";

    private static bool TryParseSourceField(string? value, out MatchRuleSourceField field)
    {
        field = MatchRuleSourceField.Title;
        if (string.IsNullOrWhiteSpace(value))
            return true;
        switch (value.Trim().ToLowerInvariant())
        {
            case "title":
                field = MatchRuleSourceField.Title;
                return true;
            case "filename":
                field = MatchRuleSourceField.FileName;
                return true;
            default:
                return false;
        }
    }
}

public sealed record MatchRuleRequest(
    string? Name, string? Pattern, string? SourceField, bool? Enabled, int? Priority,
    string? AlbumOverride, string? AlbumArtistOverride);
public sealed record MatchRuleView(
    int Id, string Name, bool Enabled, int Priority, string Pattern, string SourceField,
    string? AlbumOverride, string? AlbumArtistOverride, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
public sealed record MatchRuleTestRequest(string? Pattern, string? Sample);
public sealed record MatchRuleTestResponse(bool Valid, string? Error, bool Matched, MatchRuleExtractionView? Extracted);
public sealed record MatchRuleExtractionView(string? Artist, string? Title, string? Album, string? AlbumArtist);
