using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

public class SongMetadata
{
    [Key]
    public int Id { get; set; }
    public required string SourcePath { get; set; }
    public required long FileSizeBytes { get; set; }
    public required string FileName { get; set; }
    public required string Extension { get; set; }
    public required DateTime LastModifiedUtc { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? Title { get; set; }
    public int? Year { get; set; }
    public int? TrackNumber { get; set; }
    public int? DurationSeconds { get; set; }
    public required DateTime IndexedAtUtc { get; set; }
    public string? Fingerprint { get; set; }

    public string? Isrc { get; set; }
    public string? MusicBrainzId { get;set;}
    public string? SpotifyId { get;set;}

    public DateTime? DeletedAtUtc { get; set; }

    public bool IsDeleted => DeletedAtUtc.HasValue;

}
