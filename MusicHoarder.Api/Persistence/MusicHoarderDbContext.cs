using Microsoft.EntityFrameworkCore;
namespace MusicHoarder.Api.Persistence;

public class MusicHoarderDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<SongMetadata> Songs { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<SongMetadata>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SourcePath).IsUnique();
            entity.HasIndex(e => new { e.DeletedAtUtc, e.LastModifiedUtc });
            entity.HasIndex(e => new { e.DeletedAtUtc, e.EnrichmentStatus, e.Id });
        });
    }
}