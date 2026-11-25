using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Persistence;

public class SongChangeAuditTests
{
    [Fact]
    public async Task Record_AddsOneAppliedRowStampedWithTheBatch()
    {
        await using var db = NewContext();
        var stamp = new DateTime(2026, 11, 25, 9, 30, 0, DateTimeKind.Utc);

        SongChangeAudit.Record(db, SongChangeAudit.AlbumMerge, stamp, 7, nameof(SongMetadata.Album), "Blueprint 3", "The Blueprint 3");
        SongChangeAudit.Record(db, SongChangeAudit.AlbumMerge, stamp, 8, nameof(SongMetadata.Album), null, "The Blueprint 3");
        await db.SaveChangesAsync();

        var rows = await db.SongMetadataChanges.OrderBy(c => c.SongId).ToListAsync();
        Assert.Equal(
            [
                (7, "Album", (string?)"Blueprint 3", (string?)"The Blueprint 3"),
                (8, "Album", null, "The Blueprint 3"),
            ],
            rows.Select(c => (c.SongId, c.FieldName, c.OldValue, c.NewValue)));
        Assert.All(rows, c =>
        {
            Assert.Equal("album-merge", c.Source);
            Assert.Equal(1.0, c.Confidence);
            Assert.Equal(stamp, c.CreatedAtUtc);
            Assert.Equal(stamp, c.AppliedAtUtc);
            Assert.Null(c.RevertedAtUtc);
        });
    }

    [Fact]
    public void Record_OnlyStagesTheRow_TheCallerSavesItWithItsOwnWrites()
    {
        using var db = NewContext();

        SongChangeAudit.Record(db, SongChangeAudit.ArtistMerge, DateTime.UtcNow, 1, nameof(SongMetadata.Artist), "JAYZ", "JAY-Z");

        Assert.Equal(EntityState.Added, Assert.Single(db.ChangeTracker.Entries<SongMetadataChange>()).State);
    }

    // Rows in every database carry these spellings, and the dedup history, the History feed and the
    // credit repair find them by it: a rename here orphans every row written before it.
    [Theory]
    [InlineData(SongChangeAudit.ArtistMerge, "artist-merge")]
    [InlineData(SongChangeAudit.ArtistCreditSplit, "artist-credit-split")]
    [InlineData(SongChangeAudit.AlbumMerge, "album-merge")]
    [InlineData(SongChangeAudit.AlbumIdentityHeal, "album-identity-heal")]
    [InlineData(SongChangeAudit.ArtistCreditHeal, "artist-credit-heal")]
    [InlineData(SongChangeAudit.CanonicalAlbum, "canonical-album")]
    [InlineData(SongChangeAudit.AlbumArtistRepair, "album-artist-repair")]
    [InlineData(SongChangeAudit.ArtistMergeRepair, "artist-merge-repair")]
    [InlineData(SongChangeAudit.CreditSplitRepair, "credit-split-repair")]
    [InlineData(SongChangeAudit.ArtistAliasRepair, "artist-alias-repair")]
    public void Sources_KeepTheirPersistedSpelling(string source, string persisted) =>
        Assert.Equal(persisted, source);

    [Fact]
    public void Sources_AreDistinctAndFitTheColumn()
    {
        var sources = typeof(SongChangeAudit)
            .GetFields()
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(10, sources.Count);
        Assert.Equal(sources.Count, sources.Distinct(StringComparer.Ordinal).Count());
        Assert.All(sources, s => Assert.InRange(s.Length, 1, 64)); // SongMetadataChange.Source is [MaxLength(64)]
    }

    private static MusicHoarderDbContext NewContext() => new(
        new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
}
