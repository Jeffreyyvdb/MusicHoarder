using Microsoft.EntityFrameworkCore;
namespace MusicHoarder.Api.Persistence;

public class MusicHoarderDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<SongMetadata> Songs { get; set; }
    public DbSet<SpotifySettings> SpotifySettings { get; set; }
    public DbSet<SpotifyTrackLibraryMatch> SpotifyTrackLibraryMatches { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<SongMetadata>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SourcePath).IsUnique();
            entity.HasIndex(e => new { e.DeletedAtUtc, e.LastModifiedUtc });
            entity.HasIndex(e => new { e.DeletedAtUtc, e.EnrichmentStatus, e.Id });
            entity.HasIndex(e => new { e.DeletedAtUtc, e.EnrichmentStatus, e.LibraryBuildStatus, e.Id });
            entity.HasIndex(e => new { e.DeletedAtUtc, e.AlbumArtist, e.Album, e.Year, e.Id });
            entity.HasIndex(e => e.DestinationPath);
            entity.HasIndex(e => e.Fingerprint).HasMethod("hash");
            entity.HasIndex(e => new { e.DeletedAtUtc, e.IsDuplicate });

            entity.HasOne(e => e.DuplicateOf)
                .WithMany()
                .HasForeignKey(e => e.DuplicateOfId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<SpotifySettings>(entity =>
        {
            entity.HasKey(e => e.Id);
        });

        modelBuilder.Entity<SpotifyTrackLibraryMatch>(entity =>
        {
            entity.HasKey(e => e.SpotifyTrackId);
            entity.HasIndex(e => e.UpdatedAtUtc);
            entity.HasIndex(e => e.MatchStatus);
        });
    }
}