namespace MusicHoarder.Api.Contracts;

public record EnrichmentResetRequest(
    string Target = "all",
    bool RestoreOriginalMetadata = true);
