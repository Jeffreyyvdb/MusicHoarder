using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

public class SongMetadata
{
    [Key]
    public int Id { get; set; }
    public required string FilePath { get; set; }
    public required string FileName { get; set; }
    public required long FileSize { get; set; }
    public required string Extension { get; set; }
    public required DateTime LastModified { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? Title { get; set; }
    public int? Year { get; set; }
    public int? TrackNumber { get; set; }
    public string? Fingerprint { get; set; }
    public int? Duration { get; set; }
    public required DateTime IndexedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
}