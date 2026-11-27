# Data model & persistence

How MusicHoarder stores its data: a single PostgreSQL database accessed through one EF Core
`DbContext` (`MusicHoarder.Api/Persistence/`), plus file trees on disk for audio and artwork.
This page covers the entity model and its relationships, database provisioning and migrations,
the EF InMemory provider used by tests, on-disk storage layout, and the snapshot feature.

For deployment-level concerns (Docker volumes, backing up the database, permissions on mounts),
see [SELF_HOSTING.md](SELF_HOSTING.md).

## Where things live

| Directory | Contents |
|---|---|
| `MusicHoarder.Api/Persistence/` | `MusicHoarderDbContext`, all entity classes, `Migrations/`, value/interceptor helpers |
| `MusicHoarder.Api/Persistence/Migrations/` | ~101 ordered EF migrations + `MusicHoarderDbContextModelSnapshot.cs` |
| `MusicHoarder.Api/Persistence/Interceptors/` | `RebuildOnMetadataChangeInterceptor` (DB write → destination file rebuild) |
| `MusicHoarder.Api/Storage/` | Disk-usage measurement of every managed root (`StorageUsageCalculator`, `StorageUsageSnapshotStore`, background refresh) |
| `MusicHoarder.Api/Artwork/` | Cover-art resolution & artist image prefetch (`CoverThumbnailService`, `ExternalCoverArtFetcher`, `AlbumCoverWriter`, …) |
| `MusicHoarder.Api/Audio/` | Audio analysis, not file management: chromaprint fingerprinting (`ChromaprintComparer`), PCM decoding, fingerprint similarity gating |
| `MusicHoarder.Api/StreamingFlac/` | HTTP client for the optional self-hosted lossless acquisition sidecar (`StreamingFlacSidecarClient`) |
| `MusicHoarder.Api/Data/` | Committed JSON catalogs (`yetracker.json`, `yetracker-art.json`, `yetracker-tracklists.json`) loaded by the YeTracker enrichment providers |
| `MusicHoarder.Api/Snapshots/` | `EnrichmentSnapshotService` — point-in-time enrichment-quality snapshots stored **in the database** (see [Snapshots](#snapshots-enrichment-timeline)) |

## The DbContext

`MusicHoarder.Api/Persistence/MusicHoarderDbContext.cs` is the single EF Core context for the
whole app. Three things about it matter for every feature that touches the database:

### 1. Multi-tenancy via global query filters

Every owner-scoped table carries an `OwnerUserId` column and is protected by an EF global query
filter created in `OnModelCreating`:

```csharp
entity.HasQueryFilter(s => !hasUser || s.OwnerUserId == FilterUserId);
```

- `FilterUserId` is read from an `ICurrentUserAccessor` **per query**, so one compiled model and
  one compiled query shape serve every signed-in user (the doc comment in the context explains
  that capturing the id in the model instead would rebuild a ~1s model per account per restart).
- A context built with the 1-argument constructor (tests, design-time tooling) has no accessor
  and sees every row. `UserAwareModelCacheKeyFactory`
  (`MusicHoarder.Api/Persistence/UserAwareModelCacheKeyFactory.cs`) varies the model cache key on
  exactly that difference ("unfiltered" vs "filtered") so the two variants never share a compiled
  model.
- Background/hosted services have no request user; they call `.IgnoreQueryFilters()` and filter
  by owner id explicitly (e.g. `Snapshots/EnrichmentSnapshotService.cs`).
- Required dependents of `SongMetadata` mirror the song's filter via navigation
  (`e.Song.OwnerUserId == FilterUserId`) so the dependent disappears exactly when its principal
  does.
- **Catalog/reference data has no filter**: `CanonicalAlbum`, `CanonicalAlbumTrack`,
  `ArtistImage`, `AlbumCoverFetchAttempt`, `ArtistCatalogLink`, `WebPushKeys`,
  `TranscriptionAudioSpend` — one row serves all tenants (a portrait or an official album
  tracklist is the same for every user).
- Two tables are deliberately *cross-tenant*: `PlaylistEntry` and `UserSongState` filter on the
  playlist's/state's own user, while the referenced song belongs to the grantor of a
  `LibraryShareGrant`. `LibraryShareGrant` itself is visible to both parties:
  `e.OwnerUserId == FilterUserId || e.GranteeUserId == FilterUserId`.

### 2. Secret column encryption

The 3-argument constructor takes an `ISecretColumnProtector`, baked into the model as a value
converter on `SpotifySettings.ClientSecret` / `.AccessToken` / `.RefreshToken`
(`MusicHoarder.Api/Persistence/SecretColumnProtector.cs`). The protector's identity is part of
the model cache key so a context with a different protector never shares a compiled model.
`LegacySecretColumns.ProtectAsync` migrates any still-plaintext rows at startup (see
[Migrations](#migrations-at-startup)).

### 3. SaveChanges hooks

- **Presence flags**: `SaveChanges` (`SyncSongPresenceFlags`) forces every UPDATE that touches a
  large text column (fingerprint, plain/synced/transcribed lyrics) to also write its derived
  `Has*` boolean in the same SQL statement, so concurrent writers can never desync a flag from
  its text.
- **`RebuildOnMetadataChangeInterceptor`**
  (`MusicHoarder.Api/Persistence/Interceptors/RebuildOnMetadataChangeInterceptor.cs`): when a
  tag-relevant column of a song with `LibraryBuildStatus.Done` changes, the destination build is
  reset so the library builder re-copies and re-tags the file on disk — keeping external players
  (Navidrome) in sync with enrichment results that arrive after the file was built.

### Row versions (change detection)

`MusicHoarder.Api/Persistence/RowVersions.cs` maps sequence-backed version columns used by the
library list and Inbox name counts to detect meaningful writes. The Postgres-side trigger draws
the next sequence value on every insert and on every update *except* writes whose changed columns
are all in that table's `RowVersion.Ignored` ignore list — so a two-minute wishlist sync stamp
doesn't invalidate every cached list. `MusicHoarder.Api.Tests/Persistence/RowVersionTests.cs`
guards the lists.

## Entity catalog

All entities live in `MusicHoarder.Api/Persistence/*.cs` (auth entities in `MusicHoarder.Api/Auth/*.cs`).
The context exposes ~60 `DbSet`s; grouped by role:

**Library core (owner-scoped)**

| Entity | Table role |
|---|---|
| `SongMetadata` | The track row. One per physical file: tags, enrichment results, fingerprint, lyrics, quality, build state. See below. |
| `SongDuplicateLink` | Duplicate pair in canonical (low, high) song-id order, unique per pair |
| `ArtistAlias` / `DedupDismissal` | Artist dedupe support (unique per `(Owner, AliasKey)` and per dismissal key) |
| `SongProviderAttempt` | Per-song, per-provider match attempts (unique `(SongId, Provider)`) |
| `SongMetadataChange` | Change log of metadata edits (change audit / album-identity healing) |
| `SongQualityGrade` | AI quality grades, latest-per-song via `GradedAtUtc` |
| `CleanEditCheck` | One clean-edits verdict per song |
| `GraderComparisonRun` / `GraderTrialLabel` | Shadow "grader trial" answers and labels, kept apart from real grades |
| `TrackSyncState` | Per-song Navidrome/external-sync outbox (unique per song) |

**Albums (partly shared catalog)**

| Entity | Scope | Role |
|---|---|---|
| `CanonicalAlbum` | catalog | Reconciled album identity/tracklist, unique per `(ArtistKey, AlbumKey)`, swept by `Status` |
| `CanonicalAlbumTrack` | catalog | Tracklist rows (disc/track number, MusicBrainz recording id) |
| `CanonicalAlbumQualityGrade` | owner | AI verdict on an album's reconciliation for this owner's library |
| `AlbumCompletionState` | owner | Per-album completion verdict + backfill sweep cursor |
| `AlbumCoverFetchAttempt` | catalog | External cover-fetch cooldown keyed by album folder (row deleted once a cover lands) |
| `ArtistImage` | catalog | Cached artist portraits keyed by normalized name |
| `ArtistCatalogLink` | catalog | Cached per-artist catalog status (Deezer id, fans, next check) |

**Wishlist & downloads (owner-scoped)**

| Entity | Role |
|---|---|
| `WishlistSource` | A registered source playlist (Spotify Liked Songs / playlist, Deezer, YouTube); unique per `(Owner, SourceType, playlist id)` with filtered unique indexes per provider id |
| `WishlistItem` | One wanted track with download state machine: origin, provider, attempts, retry backoff (`NextAttemptAtUtc`), resulting `DownloadedSongId` |
| `WishlistSourceTrack` | Snapshot of a source's tracks at last sync (cascade-deleted with source or item) |
| `ExportedPlaylist` | One `.m3u8` export row per `(Owner, Kind, playlist)` |

**Playlists & follows (owner-scoped)**

| Entity | Role |
|---|---|
| `Playlist` / `PlaylistEntry` | Native playlists; entries unique per `(Playlist, Song)`, ordered by `Position`; a playlist optionally links to a `WishlistSource` (unique, `SetNull` on removal) |
| `FollowedArtist` / `FollowedArtistRelease` | Deezer artist follows and the monitor's per-release decisions (baseline / queued / skipped), releases cascade |

**Sharing & social**

| Entity | Role |
|---|---|
| `SongShare` / `ShareVisit` | Public share links (unique token) and anonymous visit analytics |
| `LibraryShareGrant` | Friend grants a library slice (scope + optional artist/album key) |
| `UserSongState` | Per-(user, song) liked/played state — crosses tenants by design |
| `ChatConversation` / `ChatParticipant` / `ChatMessage` | Messaging between accounts; message visibility filter checks participation |
| `SongReport` | Listener reports on a song |
| `WebPushSubscription` | Push endpoints per user |

**Listening & history (append-mostly)**

| Entity | Role |
|---|---|
| `ListeningEvent` | Append-only play history, private to the listener; `SongId` is a plain number (no FK) so history outlives songs |
| `PlaybackSession` | One remembered playback session per user (unique `OwnerUserId`) |
| `IngestRun` | Scan/enrich/build run history |
| `LibraryWriteEvent` | Feed of library writes (newest-first per owner; optional `SongId` with `SetNull`) |

**Auth & settings**

| Entity | Role |
|---|---|
| `User` | Accounts; `Capabilities` stored as a raw `[Flags]` int; two seeded rows (owner, demo) via `HasData` |
| `Session`, `MagicLinkToken`, `WebAuthnCredential`, `Invite` | Cookie sessions, magic-link login (hashed tokens), passkeys, friend invites |
| `SpotifySettings` | Per-user Spotify OAuth state (encrypted columns), unique per owner |
| `SpotifyTrackLibraryMatch` | Match cache, composite PK `(Owner, SpotifyTrackId)` |
| `RuntimeSettings`, `DirectoryPreference` | Instance-wide toggles and per-folder UI preferences |
| `WebPushKeys`, `TranscriptionAudioSpend` | Instance-wide VAPID keys; transcription cost ledger (pruned after ~35 days) |

### `SongMetadata` in detail

`MusicHoarder.Api/Persistence/SongMetadata.cs` (~1700 lines) is the center of the model. Key
columns:

- **File identity**: `SourcePath` (unique per `(OwnerUserId, SourcePath)`), `FileSizeBytes`,
  `FileName`, `Extension`, `LastModifiedUtc` — the scanner reconciles these against disk.
- **Tags & enrichment**: `Artist`/`Artists`/`AlbumArtist`, `Album`, `Title`, `Year`,
  disc/track numbers, MusicBrainz/Spotify/AcoustID ids, `Isrc`, genre, label, copyright, plus
  `Original*` captured-before-enrichment columns and `LastEnrichmentAlgorithmVersion`.
- **Pipeline state**: `EnrichmentStatus` (`Pending`/`Matched`/`NeedsReview`/`Failed`),
  `MatchConfidence`, `MatchedBy`, `LibraryBuildStatus`, `DestinationPath` /
  `PreviousDestinationPath`.
- **Presence flags**: `HasFingerprint`, `HasPlainLyrics`, `HasNonBlankPlainLyrics`,
  `HasSyncedLyrics`, `HasNonBlankSyncedLyrics`, `HasTranscribedLyrics` — backed fields keep them
  in sync with the text properties on materialization and `SaveChanges` re-asserts them on every
  update (see [SaveChanges hooks](#3-savechanges-hooks)). The hot library lists query the
  booleans, never the large text columns (`SongMetadataLightQuery.cs`).
- **Acquisition intent**: `SongAcquisitionIntent` — `Explicit` (owner asked; the column default,
  so every pre-existing row is "mine" with no backfill) vs `AlbumFill` (added by
  `AlbumCompletionSweep` because the owner had another track from the album; excluded from
  "My music" until liked).
- **Soft delete**: `DeletedAtUtc` participates in almost every index; rows are rarely
  hard-deleted.

There is **no separate Artist or Album table**. Albums exist as reconciled `CanonicalAlbum`
identity rows (`ArtistKey` + `AlbumKey`) used for tracklists, completion and grading; artist
identity is a normalized *key* (`ArtistKey`, `ArtistAlias`, `ArtistCatalogLink`), not a foreign
key from songs. Song → album/artist aggregation happens in queries
(`MusicHoarder.Api/Library/AlbumGroupKey.cs`, `Library/AlbumProjection.cs`).

## ER diagram

Core entities and relationships (cards show EF delete behavior where configured):

```mermaid
erDiagram
    USER ||--o{ SESSION : "owns"
    USER ||--o{ MAGIC_LINK_TOKEN : ""
    USER ||--o{ WEB_AUTHN_CREDENTIAL : ""
    USER ||--o{ WEB_PUSH_SUBSCRIPTION : ""
    USER ||--o{ SPOTIFY_SETTINGS : "unique"
    USER ||--o{ LIBRARY_SHARE_GRANT : "grants / receives"

    SONG_METADATA ||--o{ SONG_PROVIDER_ATTEMPT : "Cascade"
    SONG_METADATA ||--|| SONG_MUSIC_VIDEO : "unique, Cascade"
    SONG_METADATA ||--o{ SONG_QUALITY_GRADE : "Cascade"
    SONG_METADATA ||--o{ SONG_METADATA_CHANGE : "Cascade"
    SONG_METADATA ||--o{ CLEAN_EDIT_CHECK : "unique, Cascade"
    SONG_METADATA ||--|| TRACK_SYNC_STATE : "unique outbox, Cascade"
    SONG_METADATA ||--o{ SONG_SHARE : "Cascade"
    SONG_METADATA ||--o{ PLAYLIST_ENTRY : "Cascade"
    SONG_METADATA |o--o{ SONG_METADATA : "DuplicateOf (SetNull)"
    SONG_METADATA |o--o| WISHLIST_ITEM : "DownloadedSong (SetNull)"

    CANONICAL_ALBUM ||--|{ CANONICAL_ALBUM_TRACK : "Cascade"
    CANONICAL_ALBUM ||--o{ CANONICAL_ALBUM_QUALITY_GRADE : "Cascade"
    CANONICAL_ALBUM |o--o| ALBUM_COMPLETION_STATE : "Cascade"
    CANONICAL_ALBUM |o--o{ WISHLIST_ITEM : "SetNull"
    CANONICAL_ALBUM |o--o| FOLLOWED_ARTIST_RELEASE : ""

    WISHLIST_SOURCE ||--o{ WISHLIST_SOURCE_TRACK : "Cascade"
    WISHLIST_ITEM |o--o| WISHLIST_SOURCE : "SetNull"
    WISHLIST_SOURCE_TRACK }o--|| WISHLIST_ITEM : "Cascade"
    WISHLIST_SOURCE |o--o|| PLAYLIST : "SetNull"
    PLAYLIST ||--o{ PLAYLIST_ENTRY : "Cascade"

    FOLLOWED_ARTIST ||--o{ FOLLOWED_ARTIST_RELEASE : "Cascade"

    ENRICHMENT_SNAPSHOT ||--|{ ENRICHMENT_SNAPSHOT_SONG : "Cascade"
    SONG_SHARE ||--o{ SHARE_VISIT : "Cascade"
    LIBRARY_WRITE_EVENT }o--o| SONG_METADATA : "optional, SetNull"
    USER_SONG_STATE }o--|| SONG_METADATA : "cross-tenant, Cascade"

    USER {
        Guid Id PK
        string EmailNormalized UK
        UserRole Role
        Capability Capabilities
    }
    SONG_METADATA {
        int Id PK
        Guid OwnerUserId
        string SourcePath
        string Artist
        string AlbumArtist
        string Album
        string Title
        int Year
        EnrichmentStatus EnrichmentStatus
        LibraryBuildStatus LibraryBuildStatus
        SongAcquisitionIntent AcquisitionIntent
        string DestinationPath
        DateTime DeletedAtUtc
    }
    CANONICAL_ALBUM {
        int Id PK
        string ArtistKey
        string AlbumKey
        CanonicalAlbumStatus Status
    }
    WISHLIST_ITEM {
        int Id PK
        Guid OwnerUserId
        WishlistItemOrigin Origin
        WishlistItemStatus Status
        string SpotifyTrackId
        string DeezerTrackId
        string YouTubeVideoId
        int DownloadedSongId FK
        DateTime NextAttemptAtUtc
    }
    PLAYLIST_ENTRY {
        int Id PK
        int PlaylistId FK
        int SongId FK
        int Position
    }
    LISTENING_EVENT {
        int Id PK
        Guid UserId
        int SongId "no FK"
        DateTime PlayedAtUtc
    }
```

Owner-scoped tables (not all shown) each carry `OwnerUserId` + a global query filter; catalog
tables (`CANONICAL_ALBUM*`, `ARTIST_IMAGE`, `ALBUM_COVER_FETCH_ATTEMPT`, `ARTIST_CATALOG_LINK`)
do not.

## Database provisioning (Aspire)

`MusicHoarder.AppHost/AppHost.cs` models the whole stack:

- `builder.AddPostgres("postgres")` with the image pinned to **17.6** (the comment explains: the
  Aspire default tag floats and a compose regen must never propose a silent major-version
  upgrade of a live data volume), `ContainerLifetime.Persistent`, and
  `.WithDataVolume(dataVolumeName)`.
- In **run mode** (local dev) the volume name gets a per-branch suffix
  (`ResolveDbKey` → git branch or short SHA) so parallel worktrees never share a
  `__EFMigrationsHistory`; in **publish mode** the stable `musichoarder-volume` name is kept
  (compose output later renames it to `postgres-data` — see
  `MusicHoarder.AppHost/ComposeFileExtensions.cs`).
- `postgres.AddDatabase("musichoarderdb")` produces the connection string the API reads;
  the API project `.WaitFor(postgresdb)` and `.WithReference(postgresdb)`.

In `MusicHoarder.Api/Program.cs` the context is registered **non-pooled** with
`AddDbContext` (to inject `ICurrentUserAccessor` + `ISecretColumnProtector`) using
`options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure())`, then
`builder.EnrichNpgsqlDbContext<MusicHoarderDbContext>(settings => settings.DisableRetry = true)`
applies Aspire's connection-string + OpenTelemetry wiring. The comment notes retries are enabled
manually because the Aspire integration's own retry path is incompatible with Npgsql EF 11.
The `RebuildOnMetadataChangeInterceptor` is added here too.

## Migrations at startup

`MusicHoarder.Api/Composition/WebApplicationExtensions.cs` — `ApplyPendingMigrationsAsync`,
called from `Program.cs` between `app.Build()` and endpoint mapping:

1. `dbContext.Database.SetCommandTimeout(TimeSpan.FromMinutes(10))` — migrations that backfill a
   column rewrite every song row in one statement; the default 30s would abort startup on a slow
   disk.
2. `await dbContext.Database.MigrateAsync()` — applies all pending migrations from
   `Persistence/Migrations/` (~101 at the time of writing, from
   `20260313155522_InitialSongMetadata` onward). Safe for the single-instance homelab deployments
   this app targets; `ComposeFileExtensions.cs` additionally forces Postgres **stop-first**
   updates so a container replacement can't race a migrating API.
3. `LegacySecretColumns.ProtectAsync(...)` — one-time encryption of any still-plaintext Spotify
   credential columns, logged when it touches rows.

Design-time tooling (`dotnet ef migrations add …`) uses
`MusicHoarder.Api/Persistence/MusicHoarderDbContextFactory.cs`, an `IDesignTimeDbContextFactory`
that builds an unfiltered context on a hardcoded local Postgres connection string; the runtime
path goes through the DI registration above instead.

## Tests: the EF InMemory provider

The default test database is **EF Core's InMemory provider** — no Postgres or Docker required
(`AGENTS.md`, `dotnet test MusicHoarder.Api.Tests`). Tests construct contexts with the 1-arg
constructor and a fresh per-test database name:

```csharp
new DbContextOptionsBuilder<MusicHoarderDbContext>()
    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
    .Options;
// → new MusicHoarderDbContext(options)  // unfiltered, or with a TestCurrentUserAccessor
```

~150+ test files use this pattern (grep `UseInMemoryDatabase` in `MusicHoarder.Api.Tests/`),
typically with `TestCurrentUserAccessor` to exercise the query-filter scoping.

What only Postgres actually does — real unique index enforcement, conflict clauses, the
row-version triggers — is covered separately by
`MusicHoarder.Api.Tests/Persistence/PostgresTestDatabase.cs`: a `PostgresFact` attribute skips
unless `MUSICHOARDER_TEST_POSTGRES` names a server; when set, the fixture creates a throwaway
database per test (`mh_test_<guid>`), runs `MigrateAsync()` against it, and drops it on dispose.
Note the InMemory provider cannot translate everything the service code guards against for
non-translatable providers (e.g. `Snapshots/EnrichmentSnapshotService.cs` deliberately does a
last-grade-per-song reduction in memory "because GroupBy→First in a projection isn't
translatable by every provider, incl. InMemory").

## File storage layout

MusicHoarder keeps state in two places: the Postgres database and ordinary directories on disk
(wired via `MusicEnricherOptions`, `MusicHoarder.Api/Options/MusicEnricherOptions.cs`, and
AppHost parameters). The roots the app manages, as enumerated by
`MusicHoarder.Api/Storage/StorageUsageCalculator.cs`:

```text
source-directory/        (read-only) your existing library; scanned, never modified
destination-directory/   the cleaned, organized copy the pipeline builds — regenerable
download-directory/      wishlist download staging (transient; files move into the library)
download-directory/
  └── videos/            auto-downloaded music videos (MusicVideoDownloader.ResolveVideoDirectory)
sync:synced-source-directory/   files received from another MusicHoarder instance (Sync feature)
slskd-downloads-directory/      slskd's completed-downloads dir; finished files move to staging
<data>/cover-thumbs/     regenerable WebP cover-thumbnail cache, sharded by hash prefix
                         (Artwork/CoverThumbnailService.cs; env Artwork__CoverCacheDir)
/data/dpkeys             ASP.NET DataProtection keys (session cookies + Spotify token encryption)
```

Per `docs/SELF_HOSTING.md`, the source mount is read-only by design and the destination library
is fully regenerable from source + catalog, so only the database and the DataProtection keys
volumes need backing up.

Inside the destination library, `MusicHoarder.Api/Library/DestinationPathResolver.cs` computes a
deterministic layout:

```text
<DestinationDirectory>/
  <AlbumArtist or "Various Artists">/
    [<year> - ]<Album>/
      <disc-track prefix><Title><ext>
  <AlbumArtist>/Unreleased/<Title><ext>       (IsUnreleased songs)
```

Folder segments come from the elected reconciled `AlbumIdentity` when present (not the drifting
per-track tags), so every track of one logical album lands in the same folder across build runs.
A frozen copy of the pre-reconciliation scheme locates files written by historic builds for
relocation. `LibraryWriteEvent` rows record what was written, and
`StorageUsageBackgroundService` + `StorageUsageSnapshotStore` measure the roots off the request
path (endpoints read the cached snapshot and can request a refresh).

The artwork pipeline (`MusicHoarder.Api/Artwork/`) writes cover art **into the destination
audio files / folders** (`AlbumCoverWriter`, embedded picture reading, external fetch sweeps
with per-folder cooldowns in the `AlbumCoverFetchAttempts` table) and caches only derived
thumbnails on disk. `MusicHoarder.Api/Audio/` holds the fingerprint/PCM analysis code
(chromaprint, ffmpeg decode), and `MusicHoarder.Api/StreamingFlac/StreamingFlacSidecarClient.cs`
calls an optional external acquisition sidecar over HTTP — it stores nothing itself; acquired
FLACs land in the shared download staging directory.

## Snapshots (enrichment timeline)

Despite the name, `MusicHoarder.Api/Snapshots/` is not database backup — it is the enrichment
quality timeline. `EnrichmentSnapshotService.CaptureAsync` (`Snapshots/EnrichmentSnapshotService.cs`)
measures the owner's library (status counts, duplicate count, average match confidence, per-provider
match counts, latest AI grade rollup) plus a config fingerprint (`ConfigJson`/`ConfigHash` — enabled
providers, thresholds, model + prompt version), and writes one immutable `EnrichmentSnapshot` row
with per-song rows in `EnrichmentSnapshotSong`. Snapshots are captured on pipeline/grading runs or
manually, retained up to **50 per owner** (older ones cascade-delete their song rows), and power
the `/api/snapshots` timeline endpoints (`MapSnapshotsEndpoints`) with
`SnapshotComparison`/`SnapshotConfigDiff` computing before/after deltas.

Actual **database backup** is an operator concern: `pg_dump` of the `postgres-data` volume, plus
the separate `musichoarder-dpkeys` volume — see [SELF_HOSTING.md](SELF_HOSTING.md#backups).
