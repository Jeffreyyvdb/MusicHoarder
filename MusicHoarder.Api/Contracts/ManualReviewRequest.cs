namespace MusicHoarder.Api.Contracts;

public record ManualReviewRequest(
    string Decision,
    string? RejectReason = null,
    string? Artist = null,
    string? AlbumArtist = null,
    string? Album = null,
    string? Title = null,
    int? Year = null,
    int? TrackNumber = null);
