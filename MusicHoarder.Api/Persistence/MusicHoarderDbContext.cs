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
    public DbSet<SongDuplicateLink> SongDuplicateLinks { get; set; } = null!;
    public DbSet<ArtistAlias> ArtistAliases { get; set; } = null!;
    public DbSet<DedupDismissal> DedupDismissals { get; set; } = null!;
    public DbSet<SongProviderAttempt> SongProviderAttempts { get; set; } = null!;
    public DbSet<SongMusicVideo> SongMusicVideos { get; set; } = null!;
    public DbSet<CanonicalAlbum> CanonicalAlbums { get; set; } = null!;
    public DbSet<AlbumCoverFetchAttempt> AlbumCoverFetchAttempts { get; set; } = null!;
    public DbSet<ArtistImage> ArtistImages { get; set; } = null!;
    public DbSet<CanonicalAlbumTrack> CanonicalAlbumTracks { get; set; } = null!;
    public DbSet<CanonicalAlbumQualityGrade> CanonicalAlbumQualityGrades { get; set; } = null!;
    public DbSet<AlbumCompletionState> AlbumCompletionStates { get; set; } = null!;
    public DbSet<SongMetadataChange> SongMetadataChanges { get; set; } = null!;
    public DbSet<SongQualityGrade> SongQualityGrades { get; set; } = null!;
    public DbSet<DirectoryPreference> DirectoryPreferences { get; set; } = null!;
    public DbSet<SpotifySettings> SpotifySettings { get; set; } = null!;
    public DbSet<SpotifyTrackLibraryMatch> SpotifyTrackLibraryMatches { get; set; } = null!;
    public DbSet<WishlistSource> WishlistSources { get; set; } = null!;
    public DbSet<WishlistItem> WishlistItems { get; set; } = null!;
    public DbSet<ExportedPlaylist> ExportedPlaylists { get; set; } = null!;
    public DbSet<RuntimeSettings> RuntimeSettings { get; set; } = null!;
    public DbSet<IngestRun> IngestRuns { get; set; } = null!;
    public DbSet<LibraryWriteEvent> LibraryWriteEvents { get; set; } = null!;
    public DbSet<EnrichmentSnapshot> EnrichmentSnapshots { get; set; } = null!;
    public DbSet<EnrichmentSnapshotSong> EnrichmentSnapshotSongs { get; set; } = null!;
    public DbSet<SongShare> SongShares { get; set; } = null!;
    public DbSet<LibraryShareGrant> LibraryShareGrants { get; set; } = null!;
    public DbSet<TrackSyncState> TrackSyncStates { get; set; } = null!;
    public DbSet<UpgradeRequest> UpgradeRequests { get; set; } = null!;
    public DbSet<User> Users { get; set; } = null!;
    public DbSet<Invite> Invites { get; set; } = null!;
    public DbSet<Session> Sessions { get; set; } = null!;
    public DbSet<MagicLinkToken> MagicLinkTokens { get; set; } = null!;
    public DbSet<WebAuthnCredential> WebAuthnCredentials { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // EF query filters can reference instance state but per-EF-docs you should capture it
        // into locals so the compiled query doesn't NRE when the accessor is null (design-time,
        // tests, hosted-service scope). Combined with the IModelCacheKeyFactory below, this gives
        // one cached model per (hasUser, userId) tuple — fine for Owner + Demo + a handful of
        // invited friends; revisit before tenant counts grow into dozens.
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
            // "My music" = WHERE OwnerUserId = x AND AcquisitionIntent = Explicit. A full composite,
            // not a partial index on "<> Explicit": the hot query wants the default value, which a
            // filtered index excluding it cannot serve.
            entity.HasIndex(e => new { e.OwnerUserId, e.AcquisitionIntent });
            // Supports identifier-based lookups / dedupe by ISRC.
            entity.HasIndex(e => e.Isrc);
            // Drives the lyrics backfill + re-check sweeps, which scan by lyrics state and take the
            // longest-overdue re-checks first (EnrichmentQueries.WhereReadyForLyricsRecheck).
            entity.HasIndex(e => new { e.DeletedAtUtc, e.LyricsStatus, e.LyricsNextRecheckAfterUtc });

            entity.HasOne(e => e.DuplicateOf)
                .WithMany()
                .HasForeignKey(e => e.DuplicateOfId)
                .OnDelete(DeleteBehavior.SetNull);

            // Global query filter: scope every read to the current user. Background services
            // bypass via .IgnoreQueryFilters().
            entity.HasQueryFilter(s => !hasUser || s.OwnerUserId == userId);
        });

        modelBuilder.Entity<SongDuplicateLink>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Pairs are stored in canonical (low, high) order, so this uniquely keys the pair.
            entity.HasIndex(e => new { e.SongIdLow, e.SongIdHigh }).IsUnique();
            entity.HasIndex(e => e.OwnerUserId);

            // A link is meaningless without both songs; songs are soft-deleted in practice, so
            // Cascade only fires on the rare hard delete (e.g. upgrade-merge provisional rows).
            entity.HasOne<SongMetadata>()
                .WithMany()
                .HasForeignKey(e => e.SongIdLow)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<SongMetadata>()
                .WithMany()
                .HasForeignKey(e => e.SongIdHigh)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasQueryFilter(e => !hasUser || e.OwnerUserId == userId);
        });

        modelBuilder.Entity<ArtistAlias>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.OwnerUserId, e.AliasKey }).IsUnique();

            entity.HasQueryFilter(e => !hasUser || e.OwnerUserId == userId);
        });

        modelBuilder.Entity<DedupDismissal>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.OwnerUserId, e.Kind, e.ScopeKey, e.KeyLow, e.KeyHigh }).IsUnique();

            entity.HasQueryFilter(e => !hasUser || e.OwnerUserId == userId);
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

        modelBuilder.Entity<SongMusicVideo>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SongId).IsUnique();

            entity.HasOne(e => e.Song)
                .WithOne(s => s.MusicVideo)
                .HasForeignKey<SongMusicVideo>(e => e.SongId)
                .OnDelete(DeleteBehavior.Cascade);

            // Mirror Song's tenancy filter (see SongProviderAttempt above).
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

        // External cover fetch cooldowns, keyed by destination album folder. Catalog-style (no
        // per-user filter); the sweep deletes a folder's row once a cover lands on disk.
        modelBuilder.Entity<AlbumCoverFetchAttempt>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.AlbumFolder).IsUnique();
        });

        // Cached artist portrait lookups, keyed by normalized artist name. Catalog-style (no
        // per-user filter): a portrait is the same for every tenant.
        modelBuilder.Entity<ArtistImage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.NormalizedName).IsUnique();
        });

        // Owner-scoped AI grade of an album's reconciliation (judged against the owner's library).
        modelBuilder.Entity<CanonicalAlbumQualityGrade>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.CanonicalAlbumId, e.GradedAtUtc });
            entity.HasIndex(e => new { e.OwnerUserId, e.GradedAtUtc });
            entity.HasIndex(e => e.Verdict);

            entity.HasOne(e => e.CanonicalAlbum)
                .WithMany()
                .HasForeignKey(e => e.CanonicalAlbumId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasQueryFilter(e => !hasUser || e.OwnerUserId == userId);
        });

        // Owner-scoped album-completion verdict + backfill cursor. Owner-scoped for the same reason
        // CanonicalAlbumQualityGrade is: the verdict depends on which tracks this owner holds.
        modelBuilder.Entity<AlbumCompletionState>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.OwnerUserId, e.CanonicalAlbumId }).IsUnique();
            // The sweep picks candidates by "never swept, or due for another look".
            entity.HasIndex(e => new { e.OwnerUserId, e.NextSweepAfterUtc });

            entity.HasOne(e => e.CanonicalAlbum)
                .WithMany()
                .HasForeignKey(e => e.CanonicalAlbumId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasQueryFilter(e => !hasUser || e.OwnerUserId == userId);
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

        modelBuilder.Entity<WishlistSource>(entity =>
        {
            entity.HasKey(e => e.Id);
            // One source row per (user, kind, playlist). LikedSongs has a null playlist id, so a user
            // can only register their Liked Songs once; playlists are keyed by their Spotify id.
            entity.HasIndex(e => new { e.OwnerUserId, e.SourceType, e.SpotifyPlaylistId }).IsUnique();
            // Deezer discover playlists are keyed by their Deezer id; the filtered unique index keeps
            // one row per (user, kind, deezer playlist) without colliding with the null-Deezer rows above.
            entity.HasIndex(e => new { e.OwnerUserId, e.SourceType, e.DeezerPlaylistId })
                .IsUnique()
                .HasFilter("\"DeezerPlaylistId\" IS NOT NULL");

            entity.HasQueryFilter(s => !hasUser || s.OwnerUserId == userId);
        });

        modelBuilder.Entity<WishlistItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Natural cross-source dedupe: one wishlist row per (user, spotify track). Filtered so the
            // many Deezer-sourced rows with a null Spotify id don't collide on the unique constraint.
            entity.HasIndex(e => new { e.OwnerUserId, e.SpotifyTrackId })
                .IsUnique()
                .HasFilter("\"SpotifyTrackId\" IS NOT NULL");
            // Cross-source dedupe by Deezer id (filtered — Spotify-sourced rows have a null Deezer id).
            entity.HasIndex(e => new { e.OwnerUserId, e.DeezerTrackId })
                .IsUnique()
                .HasFilter("\"DeezerTrackId\" IS NOT NULL");
            // The download worker sweeps by owner + status, then orders by origin (user-requested work
            // is claimed strictly before album completion), so the index covers all three.
            entity.HasIndex(e => new { e.OwnerUserId, e.Status, e.Origin });
            // AlbumCompletionSweep loads every item it has ever created for an album — any status — to
            // avoid re-queueing a track it already tried.
            entity.HasIndex(e => new { e.OwnerUserId, e.CanonicalAlbumId });

            // Removing a source keeps already-acquired tracks (the FK just nulls out).
            entity.HasOne(e => e.WishlistSource)
                .WithMany()
                .HasForeignKey(e => e.WishlistSourceId)
                .OnDelete(DeleteBehavior.SetNull);

            // Dropping a canonical album keeps the items it produced; they just lose their provenance.
            entity.HasOne(e => e.CanonicalAlbum)
                .WithMany()
                .HasForeignKey(e => e.CanonicalAlbumId)
                .OnDelete(DeleteBehavior.SetNull);

            // Songs are soft-deleted in practice; SetNull is defensive against a hard delete.
            entity.HasOne(e => e.DownloadedSong)
                .WithMany()
                .HasForeignKey(e => e.DownloadedSongId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasQueryFilter(e => !hasUser || e.OwnerUserId == userId);
        });

        modelBuilder.Entity<ExportedPlaylist>(entity =>
        {
            entity.HasKey(e => e.Id);
            // One export row per (user, kind, playlist). LikedSongs has a null playlist id, so the
            // owner's Liked Songs export is a singleton; playlists are keyed by their Spotify id, so a
            // rename keeps the same row (and the old .m3u8 is deleted when the computed path changes).
            entity.HasIndex(e => new { e.OwnerUserId, e.Kind, e.SpotifyPlaylistId }).IsUnique();

            entity.HasQueryFilter(e => !hasUser || e.OwnerUserId == userId);
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

        modelBuilder.Entity<TrackSyncState>(entity =>
        {
            entity.HasKey(e => e.Id);
            // One outbox row per song; the sweep joins Songs → TrackSyncStates on this.
            entity.HasIndex(e => e.SongId).IsUnique();
            entity.HasIndex(e => new { e.Status, e.NextAttemptAtUtc });

            // The outbox row is meaningless without its song; songs are soft-deleted in practice,
            // so Cascade only fires on the rare hard delete (e.g. upgrade-merge provisional rows).
            entity.HasOne(e => e.Song)
                .WithOne()
                .HasForeignKey<TrackSyncState>(e => e.SongId)
                .OnDelete(DeleteBehavior.Cascade);

            // Match the Songs filter (required end) so filtered joins stay consistent.
            entity.HasQueryFilter(e => !hasUser || e.Song!.OwnerUserId == userId);
        });

        modelBuilder.Entity<UpgradeRequest>(entity =>
        {
            entity.HasKey(e => e.Id);
            // "Is there an active request for this song?" + the merge sweep's status scan.
            entity.HasIndex(e => new { e.SongId, e.Status });
            entity.HasIndex(e => e.Status);

            entity.HasOne(e => e.Song)
                .WithMany()
                .HasForeignKey(e => e.SongId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasQueryFilter(e => !hasUser || e.OwnerUserId == userId);
        });

        modelBuilder.Entity<LibraryWriteEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Primary feed query: owner + date window, newest first.
            entity.HasIndex(e => new { e.OwnerUserId, e.WrittenAtUtc });
            // Per-album/artist filter within the feed.
            entity.HasIndex(e => new { e.OwnerUserId, e.AlbumArtist, e.Album, e.WrittenAtUtc });

            // SongId is optional (album-level cover events have none), so keep the events even if a
            // song row is ever hard-deleted (songs are soft-deleted in practice; SetNull is defensive).
            entity.HasOne(e => e.Song)
                .WithMany()
                .HasForeignKey(e => e.SongId)
                .OnDelete(DeleteBehavior.SetNull);

            // Filter on OwnerUserId directly (not via e.Song) because the relationship is optional —
            // the stamped owner id is always present even for song-less cover events.
            entity.HasQueryFilter(e => !hasUser || e.OwnerUserId == userId);
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

        modelBuilder.Entity<SongShare>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Public share resolution is a point lookup by token (via IgnoreQueryFilters).
            entity.HasIndex(e => e.Token).IsUnique();
            // The owner's share list sweeps active rows.
            entity.HasIndex(e => new { e.OwnerUserId, e.RevokedAtUtc });

            entity.HasOne(e => e.Song)
                .WithMany()
                .HasForeignKey(e => e.SongId)
                .OnDelete(DeleteBehavior.Cascade);

            // Owner-scoped for the management endpoints; the anonymous share endpoints resolve
            // tokens via .IgnoreQueryFilters() and re-scope by the share's own OwnerUserId.
            entity.HasQueryFilter(e => !hasUser || e.OwnerUserId == userId);
        });

        modelBuilder.Entity<LibraryShareGrant>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Friend-side resolution: "my active grants".
            entity.HasIndex(e => new { e.GranteeUserId, e.RevokedAtUtc });
            // Owner-side management: "what did I share with this friend".
            entity.HasIndex(e => new { e.OwnerUserId, e.GranteeUserId, e.RevokedAtUtc });

            // Both parties see exactly their rows: the owner for management, the grantee for
            // resolution — so the /api/shared endpoints need no IgnoreQueryFilters() on grants
            // (only on the subsequent Song reads, which are re-scoped to the grant's owner).
            entity.HasQueryFilter(e => !hasUser || e.OwnerUserId == userId || e.GranteeUserId == userId);
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

        modelBuilder.Entity<Invite>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Anonymous acceptance is a point lookup by hash (via IgnoreQueryFilters — the
            // clicker may carry a stale demo/friend cookie whose filter would hide the row).
            entity.HasIndex(e => e.TokenHash).IsUnique();
            // The owner's pending-invites list sweeps active rows.
            entity.HasIndex(e => new { e.CreatedByUserId, e.RevokedAtUtc });
            // Create-or-rotate looks up by the invited email.
            entity.HasIndex(e => e.EmailNormalized);

            // Owner-scoped for the management endpoints, same posture as SongShare.
            entity.HasQueryFilter(e => !hasUser || e.CreatedByUserId == userId);
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
