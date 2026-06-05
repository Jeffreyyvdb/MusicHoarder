using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Persistence;

public class SongMetadataCanonicalCorrectionTests
{
    private static readonly DateTime EnrichedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static SongMetadata Song() => new()
    {
        SourcePath = "/x.flac",
        FileName = "x.flac",
        Extension = ".flac",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        EnrichmentStatus = EnrichmentStatus.Matched,
        EnrichedAtUtc = EnrichedAt,
        Album = "A Love Letter to You 4",
        Title = "Sickening",
        TrackNumber = 12,
        DiscNumber = 1,
        Year = 2020,
    };

    [Fact]
    public void ApplyCanonicalCorrection_CapturesOriginals_AppliesChanges_AndLeavesEnrichmentUntouched()
    {
        var song = Song();

        var changes = song.ApplyCanonicalCorrection("A Love Letter To You 4 (Deluxe)", 2019, 20, 1);

        Assert.Equal("A Love Letter To You 4 (Deluxe)", song.Album);
        Assert.Equal(2019, song.Year);
        Assert.Equal(20, song.TrackNumber);
        Assert.Equal(1, song.DiscNumber);

        // Disc was already 1 -> not reported; the other three changed.
        Assert.Equal(3, changes.Count);
        Assert.Contains(changes, c => c.Field == nameof(SongMetadata.Album) && c.OldValue == "A Love Letter to You 4");
        Assert.Contains(changes, c => c.Field == nameof(SongMetadata.Year) && c.OldValue == "2020" && c.NewValue == "2019");
        Assert.Contains(changes, c => c.Field == nameof(SongMetadata.TrackNumber) && c.OldValue == "12" && c.NewValue == "20");

        // Originals captured so the change is reversible.
        Assert.True(song.OriginalMetadataCaptured);
        Assert.Equal("A Love Letter to You 4", song.OriginalAlbum);
        Assert.Equal(2020, song.OriginalYear);
        Assert.Equal(12, song.OriginalTrackNumber);

        // Grade-staleness guard: enrichment status/timestamp untouched.
        Assert.Equal(EnrichmentStatus.Matched, song.EnrichmentStatus);
        Assert.Equal(EnrichedAt, song.EnrichedAtUtc);
    }

    [Fact]
    public void ApplyCanonicalCorrection_IsIdempotent()
    {
        var song = Song();
        song.ApplyCanonicalCorrection("A Love Letter To You 4 (Deluxe)", 2019, 20, 1);

        var second = song.ApplyCanonicalCorrection("A Love Letter To You 4 (Deluxe)", 2019, 20, 1);
        Assert.Empty(second);
    }

    [Fact]
    public void ApplyCanonicalCorrection_RestoreOriginalMetadata_Reverts()
    {
        var song = Song();
        song.ApplyCanonicalCorrection("A Love Letter To You 4 (Deluxe)", 2019, 20, 1);

        song.RestoreOriginalMetadata();

        Assert.Equal("A Love Letter to You 4", song.Album);
        Assert.Equal(2020, song.Year);
        Assert.Equal(12, song.TrackNumber);
    }

    [Fact]
    public void ApplyCanonicalCorrection_IgnoresNullOrNonPositiveInputs()
    {
        var song = Song();
        var changes = song.ApplyCanonicalCorrection(album: null, year: null, trackNumber: null, discNumber: null);

        Assert.Empty(changes);
        Assert.Equal("A Love Letter to You 4", song.Album);
        Assert.Equal(12, song.TrackNumber);
    }
}
