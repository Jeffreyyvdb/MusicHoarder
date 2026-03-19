namespace MusicHoarder.Api.Contracts;

public record BulkApproveRequest(
    double MinConfidence = 0.75);
