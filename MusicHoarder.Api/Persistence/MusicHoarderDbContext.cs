using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MusicHoarder.Api.Auth;
namespace MusicHoarder.Api.Persistence;

public class MusicHoarderDbContext : DbContext
{
    private readonly ICurrentUserAccessor? _currentUser;

    /// <summary>
    /// Used by EF design-time tooling and tests that don't need query-filter scoping.
    /// Pass an <see cref="ICurrentUserAccessor"/> in production for multi-tenant filtering.
    /// </summary>
    public MusicHoarderDbContext(DbContextOptions options) : base(options)
    {
    }

    public MusicHoarderDbContext(DbContextOptions options, ICurrentUserAccessor currentUser)
        : base(options)
    {
        _currentUser = currentUser;
    }

    /// <summary>
    /// Used by <see cref="UserAwareModelCacheKeyFactory"/> to vary the compiled-model cache by the
    /// captured user id. <c>"anon"</c> for design-time / background-service contexts.
    /// </summary>
    internal string ModelCacheKeySegment =>
        _currentUser?.User is { } u ? u.Id.ToString("N") : "anon";

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, UserAwareModelCacheKeyFactory>();
    }

    public DbSet<SongMetadata> Songs { get; set; } = null!;
    public DbSet<SongProviderAttempt> SongProviderAttempts { get; set; } = null!;
    public DbSet<CanonicalAlbum> CanonicalAlbums { get; set; } = null!;
    public DbSet<CanonicalAlbumTrack> CanonicalAlbumTracks { get; set; } = null!;
    public DbSet<SongMetadataChange> SongMetadataChanges { get; set; } = null!;
    public DbSet<SongQualityGrade> SongQualityGrades { get; set; } = null!;
    public DbSet<DirectoryPreference> DirectoryPreferences { get; set; } = null!;
    public DbSet<SpotifySettings> SpotifySettings { get; set; } = null!;
    public DbSet<SpotifyTrackLibraryMatch> SpotifyTrackLibraryMatches { get; set; } = null!;
    public DbSet<RuntimeSettings> RuntimeSettings { get; set; } = null!;
    public DbSet<IngestRun> IngestRuns { get; set; } = null!;
    public DbSet<EnrichmentSnapshot> EnrichmentSnapshots { get; set; } = null!;
    public DbSet<EnrichmentSnapshotSong> EnrichmentSnapshotSongs { get; set; } = null!;
    public DbSet<User> Users { get; set; } = null!;
    public DbSet<Session> Sessions { get; set; } = null!;
    public DbSet<MagicLinkToken> MagicLinkTokens { get; set; } = null!;
    public DbSet<WebAuthnCredential> WebAuthnCredentials { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // EF query filters can reference instance state but per-EF-docs you should capture it
        // into locals so the compiled query doesn't NRE when the accessor is null (design-time,
        // tests, hosted-service scope). Combined with the IModelCacheKeyFactory below, this gives
        // one cached model per (hasUser, userId) tuple — fine for our 2-user scale.
        var hasUser = _currentUser is not null;
        var userId = _currentUser?.UserId ?? Guid.Empty;

        modelBuilder.Entity<SongMetadata>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.OwnerUserId, e.SourcePath }).IsUnique();
            entity.HasIndex(e => new { e.DeletedAtUtc, e.LastModifiedUtc });
            entity.HasIndex(e => new { e.DeletedAtUtc, e.EnrichmentStatus, e.Id });
            entity.HasIndex(e => new { e.DeletedAtUtc, e.EnrichmentStatus, e.LibraryBuildStatus, e.Id });
            entity.HasIndex(e => new { e.DeletedAtUtc, e.AlbumArtist, e.Album, e.Year, e.Id });
            entity.HasIndex(e => e.DestinationPath);
            entity.HasIndex(e => e.Fingerprint).HasMethod("hash");
            entity.HasIndex(e => new { e.DeletedAtUtc, e.IsDuplicate });
            entity.HasIndex(e => new { e.OwnerUserId, e.DeletedAtUtc });
            // Supports identifier-based lookups / dedupe by ISRC.
            entity.HasIndex(e => e.Isrc);

            entity.HasOne(e => e.DuplicateOf)
                .WithMany()
                .HasForeignKey(e => e.DuplicateOfId)
                .OnDelete(DeleteBehavior.SetNull);

            // Global query filter: scope every read to the current user. Background services
            // bypass via .IgnoreQueryFilters().
            entity.HasQueryFilter(s => !hasUser || s.OwnerUserId == userId);
        });

        modelBuilder.Entity<SongProviderAttempt>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.SongId, e.Provider }).IsUnique();
            entity.HasIndex(e => new { e.Status, e.RetryAfterUtc });

            entity.HasOne(e => e.Song)
                .WithMany(s => s.ProviderAttempts)
                .HasForeignKey(e => e.SongId)
                .OnDelete(DeleteBehavior.Cascade);

            // Mirror Song's tenancy filter so this required dependent is filtered out exactly when
            // its principal would be (otherwise EF warns about the required relationship). Background
            // services that read this DbSet directly bypass via .IgnoreQueryFilters().
            entity.HasQueryFilter(e => !hasUser || e.Song.OwnerUserId == userId);
        });

        // Canonical album tracklists reconciled across providers. Catalog/reference data shared across
        // users — no OwnerUserId query filter (unlike Songs). The fetch service sweeps by Status.
        modelBuilder.Entity<CanonicalAlbum>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ArtistKey, e.AlbumKey }).IsUnique();
            entity.HasIndex(e => new { e.Status, e.NextRetryAfterUtc });

            entity.HasMany(e => e.Tracks)
                .WithOne(t => t.CanonicalAlbum)
                .HasForeignKey(t => t.CanonicalAlbumId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CanonicalAlbumTrack>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.CanonicalAlbumId, e.DiscNumber, e.TrackNumber });
            entity.HasIndex(e => e.MusicBrainzRecordingId);
        });

        modelBuilder.Entity<SongMetadataChange>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.SongId, e.CreatedAtUtc });

            entity.HasOne(e => e.Song)
                .WithMany()
                .HasForeignKey(e => e.SongId)
                .OnDelete(DeleteBehavior.Cascade);

            // Mirror Song's tenancy filter so this required dependent is filtered out exactly when
            // its principal would be (otherwise EF warns about the required relationship).
            entity.HasQueryFilter(e => !hasUser || e.Song.OwnerUserId == userId);
        });

        modelBuilder.Entity<SongQualityGrade>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Latest-grade-per-song lookups and rollups order by GradedAtUtc within a song.
            entity.HasIndex(e => new { e.SongId, e.GradedAtUtc });
            entity.HasIndex(e => new { e.OwnerUserId, e.GradedAtUtc });
            entity.HasIndex(e => e.Verdict);

            entity.HasOne(e => e.Song)
                .WithMany()
                .HasForeignKey(e => e.SongId)
                .OnDelete(DeleteBehavior.Cascade);

            // Mirror Song's tenancy filter so this dependent is filtered exactly when its principal
            // would be. Background services bypass via .IgnoreQueryFilters().
            entity.HasQueryFilter(e => !hasUser || e.OwnerUserId == userId);
        });

        modelBuilder.Entity<DirectoryPreference>(entity =>
        {
            entity.HasKey(e => e.Id);
            // One preference row per (user, folder); looked up by path when toggling and rendering the tree.
            entity.HasIndex(e => new { e.OwnerUserId, e.Path }).IsUnique();

            entity.HasQueryFilter(p => !hasUser || p.OwnerUserId == userId);
        });

        modelBuilder.Entity<SpotifySettings>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.OwnerUserId).IsUnique();

            entity.HasQueryFilter(s => !hasUser || s.OwnerUserId == userId);
        });

        modelBuilder.Entity<SpotifyTrackLibraryMatch>(entity =>
        {
            // Composite PK: one match cache row per (user, spotify track).
            entity.HasKey(e => new { e.OwnerUserId, e.SpotifyTrackId });
            entity.HasIndex(e => e.UpdatedAtUtc);
            entity.HasIndex(e => e.MatchStatus);

            entity.HasQueryFilter(m => !hasUser || m.OwnerUserId == userId);
        });

        modelBuilder.Entity<RuntimeSettings>(entity =>
        {
            entity.HasKey(e => e.Id);
        });

        modelBuilder.Entity<IngestRun>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.OwnerUserId, e.StartedAtUtc });

            entity.HasQueryFilter(r => !hasUser || r.OwnerUserId == userId);
        });

        modelBuilder.Entity<EnrichmentSnapshot>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Timeline reads order by capture time within an owner.
            entity.HasIndex(e => new { e.OwnerUserId, e.CapturedAtUtc });

            entity.HasMany(e => e.Songs)
                .WithOne(s => s.Snapshot)
                .HasForeignKey(s => s.SnapshotId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasQueryFilter(e => !hasUser || e.OwnerUserId == userId);
        });

        modelBuilder.Entity<EnrichmentSnapshotSong>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.SnapshotId, e.SongId });

            // Mirror the parent snapshot's tenancy filter so a child is filtered exactly when its
            // principal would be (otherwise EF warns about the required relationship).
            entity.HasQueryFilter(e => !hasUser || e.Snapshot.OwnerUserId == userId);
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.EmailNormalized).IsUnique();

            entity.HasData(
                new User
                {
                    Id = WellKnownUsers.OwnerId,
                    Email = WellKnownUsers.OwnerPlaceholderEmail,
                    EmailNormalized = User.Normalize(WellKnownUsers.OwnerPlaceholderEmail),
                    DisplayName = "Owner",
                    Role = UserRole.Owner,
                    IsDisabled = false,
                    CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                },
                new User
                {
                    Id = WellKnownUsers.DemoId,
                    Email = WellKnownUsers.DemoPlaceholderEmail,
                    EmailNormalized = User.Normalize(WellKnownUsers.DemoPlaceholderEmail),
                    DisplayName = "Demo",
                    Role = UserRole.Demo,
                    IsDisabled = false,
                    CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                });
        });

        modelBuilder.Entity<Session>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.ExpiresAtUtc);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MagicLinkToken>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => new { e.UserId, e.ConsumedAtUtc, e.ExpiresAtUtc });

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WebAuthnCredential>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Looked up by the authenticator-issued id during assertion; unique across all users.
            entity.HasIndex(e => e.CredentialId).IsUnique();
            entity.HasIndex(e => e.UserId);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
