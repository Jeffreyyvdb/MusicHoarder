using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Sync;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// Instance-to-instance sync surface. <c>/check</c> and <c>/upload</c> are machine-to-machine,
/// gated exclusively by <see cref="SyncApiKeyFilter"/> (they're allowlisted past the cookie-session
/// middleware and 404 unless this instance is a configured receiver). <c>/status</c> is a normal
/// cookie-authed, owner-only read for the settings UI.
/// </summary>
public static class SyncEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static void MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sync").WithTags("Sync");

        var m2m = group.MapGroup("")
            .AddEndpointFilter<SyncApiKeyFilter>();

        m2m.MapPost("/check", Check);
        m2m.MapPost("/upload", Upload).DisableAntiforgery();

        group.MapGet("/status", Status).RequireOwner();
    }

    private static async Task<IResult> Check(
        SyncCheckRequest request, ISyncIngestService ingest, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Extension))
            return Results.UnprocessableEntity(new { error = "extension_required" });
        var response = await ingest.CheckAsync(request, ct);
        return Results.Ok(response);
    }

    /// <summary>
    /// Multipart upload: part <c>metadata</c> = <see cref="SyncTrackPayload"/> JSON, part
    /// <c>file</c> = audio bytes. Size limits come from <see cref="SyncOptions.MaxUploadBytes"/> —
    /// Kestrel's 30 MB default is far below a FLAC track — applied per-request so the cap is
    /// config-driven rather than a compile-time attribute.
    /// </summary>
    private static async Task<IResult> Upload(
        HttpRequest request,
        ISyncIngestService ingest,
        IOptionsMonitor<SyncOptions> options,
        CancellationToken ct)
    {
        var maxBytes = options.CurrentValue.MaxUploadBytes;

        var bodySizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is { IsReadOnly: false })
            bodySizeFeature.MaxRequestBodySize = maxBytes;
        request.HttpContext.Features.Set<IFormFeature>(new FormFeature(request, new FormOptions
        {
            MultipartBodyLengthLimit = maxBytes,
            ValueLengthLimit = 4 * 1024 * 1024, // metadata JSON incl. full synced lyrics
        }));

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
        {
            return Results.Json(new { error = "upload_too_large_or_malformed", detail = ex.Message },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var metadataJson = form["metadata"].ToString();
        if (string.IsNullOrWhiteSpace(metadataJson))
            return Results.UnprocessableEntity(new { error = "metadata_part_required" });

        SyncTrackPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SyncTrackPayload>(metadataJson, JsonOpts);
        }
        catch (JsonException)
        {
            payload = null;
        }
        if (payload is null || string.IsNullOrWhiteSpace(payload.Extension))
            return Results.UnprocessableEntity(new { error = "invalid_metadata" });

        // A track with no identity at all can never be matched or meaningfully replaced later.
        var hasAnyIdentity = !string.IsNullOrWhiteSpace(payload.Fingerprint)
            || !string.IsNullOrWhiteSpace(payload.AcoustIdTrackId)
            || !string.IsNullOrWhiteSpace(payload.MusicBrainzId)
            || (!string.IsNullOrWhiteSpace(payload.Artist) && !string.IsNullOrWhiteSpace(payload.Title));
        if (!hasAnyIdentity)
            return Results.UnprocessableEntity(new { error = "payload_has_no_identity" });

        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
            return Results.UnprocessableEntity(new { error = "file_part_required" });
        if (file.Length > maxBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        await using var stream = file.OpenReadStream();
        var response = await ingest.IngestAsync(payload, stream, ct);
        return Results.Ok(response);
    }

    private static IResult Status(IOptionsMonitor<SyncOptions> options)
    {
        var opts = options.CurrentValue;
        return Results.Ok(new
        {
            mode = opts.Mode.ToString(),
            receiveConfigured = opts.IsReceiveConfigured,
            pushConfigured = opts.IsPushConfigured,
        });
    }
}
