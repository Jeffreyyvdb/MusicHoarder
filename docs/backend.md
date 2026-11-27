# Backend architecture (MusicHoarder.Api)

The backend is a single ASP.NET Core minimal-API service, `MusicHoarder.Api`, that owns the music library: scanning, fingerprinting, enrichment, library building, downloads, playback, and every surface the SvelteKit frontend and mobile shells call. It runs under [Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/) orchestration (`MusicHoarder.AppHost`) with shared runtime wiring in `MusicHoarder.ServiceDefaults`, backed by PostgreSQL via EF Core.

> Companion docs: [`SELF_HOSTING.md`](./SELF_HOSTING.md) covers deployment and configuration of the published compose stack; this page covers how the code is put together.

## Projects at a glance

| Project | Role |
| --- | --- |
| `MusicHoarder.Api` | The whole HTTP + background-work service (one process). |
| `MusicHoarder.AppHost` | Aspire orchestration: provisions Postgres, starts the API and the frontend, models all configuration as parameters. Also the source of the published Docker Compose output. |
| `MusicHoarder.ServiceDefaults` | Shared `AddServiceDefaults()`: OpenTelemetry (metrics, traces, logs), health-check endpoints, service discovery, resilience handlers. |
| `MusicHoarder.Api.Tests` | xUnit tests on the in-memory EF provider — no Postgres or Docker required (`dotnet test MusicHoarder.Api.Tests`). |

## Bootstrap (`MusicHoarder.Api/Program.cs`)

`Program.cs` is small and deliberately ordered. In sequence it:

1. **Raises the thread-pool floor** before anything else (`ThreadPool.SetMinThreads`). The pipeline workers do synchronous TagLib file I/O at high `Parallel.ForEachAsync` parallelism; on a low-vCPU host the pool grows ~1 thread/sec and the blocked workers crowd out Kestrel request continuations, so the floor is derived from the configured concurrency knobs (`MusicEnricher:SmbConcurrency`, `FingerprintConcurrency`, `LibraryBuilderWorkerConcurrency`) plus headroom.
2. Calls `builder.AddServiceDefaults()` (OpenTelemetry, health checks, resilience).
3. Filters the OTel logger away from `ConsoleMagicLinkSender` — sign-in links printed to the console for operators without email must never reach a telemetry backend.
4. Registers all services via `AddMusicHoarderServices()` (`MusicHoarder.Api/Composition/ServiceCollectionExtensions.cs`).
5. Registers the EF `DbContext` by hand (non-pooled) so a second constructor can take `ICurrentUserAccessor` for **per-user global query filters** (multi-tenancy is enforced at query level, not by SQL), then layers Aspire's connection-string wiring on top. Retry-on-failure is set on `UseNpgsql` itself and Aspire's own retry setup is disabled (`EnrichNpgsqlDbContext(..., DisableRetry = true)`) — see the comment about the Npgsql EF 11 incompatibility.
6. Configures OpenAPI pinned to spec version 3.1, with a cookie security-scheme transformer. In Development the app also maps Scalar (`/scalar`).
7. Builds, forces `BackgroundWorkMetrics` to exist, maps `MapDefaultEndpoints()` (health), and **applies pending EF migrations** (`ApplyPendingMigrationsAsync` in `MusicHoarder.Api/Composition/WebApplicationExtensions.cs`) with a 10-minute command timeout so a backfill on a slow disk can't abort startup. It also one-time-encrypts legacy secret columns (`LegacySecretColumns.ProtectAsync`).
8. Logs a one-line **capability banner** (what optional subsystems actually resolved: Spotify, AcoustID, grading, wishlist auto-download, etc.) and, when no Resend key is configured, announces that magic links will be printed to the log.
9. Installs the middleware chain, then `MapMusicHoarderEndpoints()`.

### Middleware chain (order matters)

```
RequestIdMiddleware          → x-request-id for API + frontend correlation (Observability/)
AuthenticationMiddleware     → reads session cookie, stashes CurrentUser; never rejects
RequireAuthMiddleware        → 401 for any non-allowlisted path without a session
DemoReadOnlyMiddleware       → demo session: safe methods + tiny POST allowlist only
MemberWriteGuardMiddleware   → member session: deny-by-default write rules by route pattern
```

All five live in `MusicHoarder.Api/Auth/Middleware/` and `MusicHoarder.Api/Observability/RequestIdMiddleware.cs`. Note there is **no** `UseHttpsRedirection`: TLS terminates at the reverse proxy in every deployment shape, and the API listens on HTTP only.

## AppHost orchestration (`MusicHoarder.AppHost/AppHost.cs`)

The AppHost is the single source of truth for configuration and deployment:

- **Resources:** a pinned Postgres (`17.6`, `ContainerLifetime.Persistent`, named data volume) and the API project, which waits for the database. The frontend is added as a Vite/Bun app (`AddViteApp`) with an HTTPS dev endpoint, proxying to the API's HTTP endpoint (`MUSICHOARDER_API_URL`) to avoid cross-runtime dev-cert trust.
- **Per-branch dev volumes:** in run mode, the Postgres data volume name is derived from the git branch (or `MUSICHOARDER_DB_KEY`), so parallel worktrees never collide on `__EFMigrationsHistory`.
- **Parameters:** `source-directory`, `destination-directory`, `owner-email`, and many optional secrets (`acoustid-api-key`, `spotify-client-id/secret`, `resend-api-key`, `quality-grading-api-key`, `lyrics-transcription-*`, `slskd-*`, `streaming-flac-sidecar-url`, …). Every optional integration is `.AsOptional()` and **degrades gracefully when blank** — the provider simply reports itself off.
- **Publish mode:** `builder.AddDockerComposeEnvironment("compose")` with `DEPLOY_TARGET` choosing the compose shape (swarm / compose preview / self-host template, via `ComposeFileExtensions.cs`); the Aspire dashboard is never shipped. GHCR container registry + `MH_IMAGE_TAG` produce the image references Dokploy pulls.
- **Spotify OAuth:** all environments route OAuth through one registered relay on the prod frontend (`/api/spotify/relay`) because Spotify forbids wildcard/localhost redirect URIs; the relay's browser bounce is guarded by a shared HMAC state key and a return-origin allowlist.

## ServiceDefaults (`MusicHoarder.ServiceDefaults/Extensions.cs`)

Standard Aspire service defaults with MusicHoarder-specific hardening:

- OTel metrics/traces/logs exported to OTLP only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (the Aspire dashboard in dev; the host's collector in prod).
- Meters `MusicHoarder.Pipeline` and `MusicHoarder.BackgroundWork` are registered by name (the string contract with `MusicHoarder.Api`'s meter classes).
- `IncludeScopes = false` on OTel logs and `Microsoft.AspNetCore.Hosting.Diagnostics` filtered out: ASP.NET's request lines carry the raw URL, which can hold an invite or share token; the trace already records the path redacted.
- Trace enrichment redacts any route parameter named `token`, and health-check paths are excluded from tracing.
- `MapDefaultEndpoints()`: `/alive` is exposed in **every** environment, tag-filtered to `live` checks so Docker/Dokploy healthchecks gate zero-downtime rollouts; `/health` (all checks) is Development-only.

## HTTP endpoints (`MusicHoarder.Api/Endpoints/`)

All routes are registered in one place: `MapMusicHoarderEndpoints()` in `MusicHoarder.Api/Composition/WebApplicationExtensions.cs`, which calls one `Map*Endpoints()` extension per file. Routes are minimal-API route groups with OpenAPI `WithTags`. The whole API lives under `/api/…` except anonymous share pages' beacons, which also live there.

| Group | Route prefix(s) | Access | What it exposes |
| --- | --- | --- | --- |
| `AuthEndpoints` | `/api/auth` | anonymous (login surfaces) + session | magic-link request/consume, demo login, `/me`, logout, account switching |
| `WebAuthnEndpoints` | `/api/auth/webauthn` | mixed | passkey register/authenticate ceremonies, credential list/delete |
| `InviteEndpoints` | `/api/invite` | anonymous token | friend-invite peek/accept |
| `FriendsEndpoints` | `/api/friends` | admin | member management, invite creation, capability grants |
| `SongsEndpoints` | `/api/songs`, `/api/tracks` | session (grants-aware) | library song list, lyrics, provenance, per-song state |
| `AlbumsEndpoints` | `/api/albums` | session | album list, detail, timeline, canonical-track acquire |
| `ArtistsEndpoints` / `ArtistDiscographyEndpoints` | `/api/artists` | session / admin | artist images; discographies, "get album", followed artists |
| `ListeningEndpoints`, `HistoryEndpoints` | `/api/listening`, `/api/history` | session / admin | stats and the merged activity feed |
| `PlaybackEndpoints` | `/api/playback` | session | device player state, remote commands, streaming handoff |
| `RadioEndpoints` | `/api/radio` | session | server-side "similar songs" radio continuation |
| `PlaylistsEndpoints` / `PlaylistSyncEndpoints` | `/api/playlists`, `/api/playlist-sync` | session / admin | local playlists (written as `.m3u8` into the library) and remote playlist sync |
| `DiscoverEndpoints` | `/api/discover` | session | discovery/new-music surface |
| `EnrichmentEndpoints` | `/api/enrichment` | mostly admin | trigger/enrich songs, folders, provider state (21 `RequireAdmin`/capability gates) |
| `DashboardEndpoints`, `StorageEndpoints`, `RunsEndpoints` | `/api/dashboard`, `/api/storage`, `/api/runs` | admin | overview, storage usage, ingest-run history |
| `BackgroundWorkEndpoints` (in `BackgroundWork/`) | `/api/background-work`, `/api/background-work/{id}` | admin | per-job status rows and detail for the Pipeline page |
| `SnapshotsEndpoints` | `/api/snapshots` | admin | capture/list/compare/delete enrichment-quality snapshots |
| `VersionEndpoints` | `/api/version`, `/api/version/latest` | anonymous | running version and update-available check |
| `DuplicatesEndpoints`, `ArtistDedupEndpoints`, `AlbumDedupEndpoints`, `DedupActionsEndpoints`, `InboxCountsEndpoints` | `/api/library/…`, `/api/inbox/…` | admin | duplicate detection/resolve/dismiss, artist & album merge/split, dedup history and inbox counts |
| `QualityEndpoints`, `AlbumQualityEndpoints`, `QualityTrialEndpoints` | `/api/quality/…` | admin | AI quality grading (song/album grade, progress, exports, trial runs) |
| `CleanEditEndpoints` | `/api/clean-edits` | admin | explicit-content clean-edit checks |
| `MusicVideoEndpoints` | `/api/music-videos` | admin | music-video candidates and downloads |
| `ImportEndpoints`, `WishlistEndpoints`, `DownloadEndpoints` area | `/api/import`, `/api/wishlist` | admin | URL import (yt-dlp probe), wishlist management |
| `SoulseekEndpoints` | `/api/soulseek` | admin | slskd integration status/search |
| `SpotifyEndpoints` | `/api/spotify` | mixed | connect, OAuth callback/relay, liked-songs sync state |
| `SyncEndpoints` | `/api/sync` | cookie + `X-Sync-Key` m2m subgroup | instance-to-instance sync (push/receive) |
| `SharesEndpoints` | `/api/shares` (admin), `/api/share` (anonymous token) | both | mint share links; anonymous token-scoped share pages |
| `ChatEndpoints` | `/api/chat` | session | in-instance chat between accounts |
| `PushEndpoints` | `/api/push` | session | Web Push config, subscribe/unsubscribe, test |
| `SettingsEndpoints` | `/api/settings` | admin-capable | read/write effective runtime settings |
| `LegacyScanEndpoints`, `DebugEndpoints` | `/api/scan`, `/api/debug` | admin | legacy scan triggers; debug diagnostics |
| `SongReportsEndpoints` | `/api/songs/{id}/report`, `/api/song-reports` | mixed | user problem reports, admin listing |

Authorization is applied per endpoint, not per group, in most files: `.RequireAdmin()` (an extension that applies the `Administer` capability filter), `.RequireCapability(...)` for finer bits, and `.AllowDemoRead()` to let the demo account through an admin gate on a read that only returns its own filtered data.

## Authentication & authorization (`MusicHoarder.Api/Auth/`)

There is no ASP.NET auth scheme; auth is a custom, cookie-session system:

- **Model** (`User.cs`, `UserRole.cs`, `Capability.cs`, `Session.cs`): three roles — `Admin = 0`, `Demo = 1`, `Member = 2` — plus a persisted `[Flags]` `Capability` bit mask (`DownloadMusic`, `TrackListening`, `ManageOwnShares`, `Administer`). Roles answer "what kind of account", capabilities answer "what may it do"; an Admin always holds every capability implicitly so it can never lock itself out. New members start with `TrackListening` only.
- **Tenancy:** `OwnerUserId` on rows + EF global query filters resolved from `ICurrentUserAccessor` (`HttpContextCurrentUserAccessor`). Members read the admin's music through `LibraryShareGrant` rows (see [Sharing](#sharing-musichoarder-apisharing)).
- **Sign-in** (`AuthService.cs`, `MagicLinkToken.cs`): passwordless. Magic links (TTL 15 min, per-account send limits — counted in the database because every request arrives via the frontend proxy, so the API has no trustworthy client IP) emailed via Resend (`ResendMagicLinkSender`) or printed to the console fallback (`ConsoleMagicLinkSender`), chosen at startup in `ServiceCollectionExtensions.cs`. Passkeys via FIDO2/WebAuthn (`WebAuthnService.cs`), with the relying-party config derived from the frontend's public origin. Sessions are 30-day data-protected cookies (`SessionCookieService.cs`), keys persisted under `Auth:DataProtectionKeysPath` (`/data/dpkeys`).
- **Enforcement:** `RequireAuthMiddleware` allowlists a small fixed set of anonymous surfaces (`/api/auth/`, `/api/share/`, `/api/invite/`, OpenAPI/Scalar, `/api/version*`, and the m2m sync paths). Two **deny-by-default write guards** follow: `DemoReadOnlyMiddleware` (403 for any unsafe method outside a tiny exact-path allowlist) and `MemberWriteGuardMiddleware` (per-rule verb + anchored route-pattern + required-capability allowances — the doc comment is explicit: *extend the allowlist, never weaken the default*).
- **Endpoint filters** (`Auth/EndpointFilters/`): `RequireCapabilityFilter` (with `DemoReadable` opt-in metadata) and `SyncApiKeyFilter` — the machine-to-machine gate for instance sync, which 404s unless the instance is a receiver, compares `X-Sync-Key` fixed-time, and 429s repeat failures per address.
- **Demo seeding:** `DemoSeederHostedService` seeds the read-only demo account and its synthetic library (`Auth/Resources/demo-songs.json`), optionally from a real-audio directory (`demo-media-directory` parameter).

## Settings and options (`MusicHoarder.Api/Options/`, `MusicHoarder.Api/Settings/`)

Two layers:

1. **Static options** — `IOptions`-bound sections over `appsettings.json` + environment variables (the AppHost injects every parameter as `Section__Property` env vars). `MusicEnricherOptions.cs` (~1,300 lines) is the hub: directories, pipeline concurrency, auto-start/auto-scan, provider toggles and per-provider rate budgets, wishlist/quality/lyrics switches. Each is registered with `ValidateDataAnnotations().ValidateOnStart()`; custom `IValidateOptions<T>` validators live in `OptionsValidators.cs` (e.g. `SyncOptionsValidator` enforces ≥32-char sync keys).
2. **Runtime settings** (`Settings/RuntimeSettingsService.cs`) — the small subset the Settings UI can flip **without redeploying**: provider enable flags, grading, auto-download, album completion, lyrics auto-sync, music-video auto-download. Stored as a single `RuntimeSettings` DB row; a DB column with a non-null value overlays the configured default, and results are cached in memory (`EffectiveSettings`). Consumers read through this overlay rather than raw options when a runtime toggle exists.

## Background work: how long-running work runs

The pipeline has no central scheduler; it is a set of `BackgroundService`s coordinated through `JobManager` and channels.

### JobManager (`MusicHoarder.Api/Jobs/JobManager.cs`)

A thread-safe singleton that tracks six independent steps — `JobType { Scan, Fingerprint, Enrich, Build, Purge, Download }` — each with its own `StepState` (status, job id, timestamps, CTS, paused flag). `TryStartJob` refuses to start a step already running, so different steps can run concurrently while each is single-instance. Each triggerable step has a bounded `Channel<Guid>` (capacity 1, `DropOldest`) exposed as `ScanTriggers` / `FingerprintTriggers` / `BuildTriggers` / `DownloadTriggers`; background services consume these to start work. A `MarkForcePipelineCascade` flag lets explicit acquisitions (URL import, wishlist download) cascade scan→fingerprint→enrich→build even when `AutoStartPipeline` is off.

### The ingest pipeline

Each stage is its own hosted service registered in `Composition/ServiceCollectionExtensions.cs`:

```
AutoScanBackgroundService ──▶ ScannerBackgroundService ──▶ FingerprintBackgroundService
                                                                   │
                     EnrichmentPipelineChannel (song ids)          ▼
         ┌──────────────────────────────────────────────── EnrichmentBackgroundService
         │  (priority lane for fresh downloads)                        │
         ▼                                                             ▼
   LibraryBuilderBackgroundService ◀─────────────────────── EnrichmentOrchestrator
         (writes the organized destination library)          (per-song provider chain)
```

- `EnrichmentPipelineChannel.cs` is the **single choke point** for enrichment: every producer (manual endpoints, startup/retry sweeps, fingerprint completion) enqueues song ids, and the always-running workers are the only consumer. It flips `JobType.Enrich` Running↔Idle in `JobManager`, deduplicates waiting songs (issue #550), and maintains a **priority lane** so a fresh import isn't stuck behind a thousands-deep backfill sweep.
- `EnrichmentBackgroundService` runs startup sweeps only when `AutoStartPipeline` is on, then spawns `EnrichmentWorkerConcurrency` workers over the channel.
- Sweeps that poll for new work share one cadence helper, `IdleBackoffSweepLoop` (wait head start → sweep → double the delay after idle sweeps, capped), so an idle or disabled sweep doesn't hammer the database at base cadence.

### Tracking

- **Live progress:** per-stage `*ProgressTracker` singletons (`ScanProgressTracker`, `FingerprintProgressTracker`, `EnrichmentProgressTracker`, `LibraryBuilderProgressTracker`, `DownloadProgressTracker`) plus `ProgressSnapshotFactory`/`PipelineSnapshot` produce the `ProgressSnapshot` the frontend polls and the SSE stream pushes.
- **Runs history:** `IngestRunMonitor` watches `JobManager` for the idle→running edge; since the pipeline has no single "run" concept, a run is defined as *the window where any step is running*, persisted as `IngestRun` rows with counters, status, duration, throughput, and a log tail, tagged with the owner id.
- **The "Background work" panel** (`MusicHoarder.Api/BackgroundWork/`): a second, orthogonal taxonomy. Eight named jobs (`BackgroundJobIds`: lyrics, transcription-allowance, cover-art, music-videos, music-video-downloads, quality-grading, enrichment-retries, album-fill-follows) each report a state (`off / idle / working / waiting / failing / unknown`) via `BackgroundWorkStatus`, and scoped `IBackgroundJobInsight` implementations (`Insights/`) build each job's row + chart data (with `Charts/EtaEstimator`, `TimeBuckets`, `LibraryMapBinner`). `GET /api/background-work` aggregates them for the Pipeline page.
- **Failure handling:** `Jobs/FailureBackoffTracker` gives repeatedly-failing work exponential backoff; `RetryAfterHeader` caps how long a rate-limited provider call waits inline (30 s) before the song is deferred instead of holding a worker.

### Housekeeping (`MusicHoarder.Api/Pipeline/`)

`PipelinePurgeService` resets or wipes a library (post-fingerprint reset / full purge) as a tracked `JobType.Purge` job, cancelling the staged-source release tracker before deleting files; `PurgeStatusTracker` reports progress. `DirectoryAvailabilityMonitor` (registered as both singleton and hosted service) probes the source/destination directories, and `LibraryDirectoriesHealthCheck` surfaces that as a health check that reports **Degraded**, not Unhealthy — an unreachable home-network share must not fail the container's liveness probe, and it isn't tagged `live` so it never leaks into `/alive`.

### Other hosted services

Around 30 `BackgroundService`s are registered, including `DownloadBackgroundService` (wishlist/provider downloads), `QualityGradingBackgroundService` + `QualityUpgradeBackgroundService` (AI grading, automatic quality upgrades), `MusicVideoBackgroundService`, `SpotifyTokenRefreshService` / `SpotifyLibraryMatchBackgroundService` / `WishlistSyncBackgroundService`, `TrackSyncBackgroundService` (instance sync push, with persistent outbox `TrackSyncState`), `AlbumCompletionBackgroundService`, `ArtistMonitorBackgroundService`, `StorageUsageBackgroundService`, `PlaybackSessionFlushService`, `ChatPushService`, and `ReleaseUpdateMonitor` (below).

## Rate limiting (`MusicHoarder.Api/RateLimiting/`)

Outbound provider throttling, not inbound API limiting (the API sits behind the frontend proxy and cannot trust client addresses):

- `ReconfigurableRateLimiter` — a token-bucket per **static** provider instance (AcoustID, MusicBrainz, Spotify, Deezer, Apple Music), so budgets stay isolated and survive transient service instances. Rates can change at runtime; the underlying limiter is lazily rebuilt on the first acquisition after a change. Supports sub-1-rps budgets (`1 permit per 4 s` for the ~20/min iTunes API).
- `RequestRateGate` — the strictly-smooth counterpart: callers claim the next free slot with no bursting; the rate is supplied per call so a reloaded option takes effect immediately.
- `RetryAfterHeader` — parses `Retry-After` in both spec forms and caps inline waiting; longer waits become `ProviderRateLimitedException` so the song is deferred and the worker freed.

## Observability (`MusicHoarder.Api/Observability/`, `MusicHoarder.Api/Logging/`)

- `RequestIdMiddleware` — honors/assigns `x-request-id` (minted by the frontend's Looping reporter or proxy), echoes it as a response header, opens a `rid:{id}` log scope for the single-line console formatter, and tags the current span so a problem report links its frontend entry, API log lines, and trace.
- `PipelineMetrics` — OTel meter `MusicHoarder.Pipeline`: `mh.enrich.queue_depth` (gauge fed by `EnrichmentPipelineChannel.InFlight`), `mh.song.terminal` counter, `mh.stage.duration_seconds` histogram. Tags are strictly low-cardinality (`stage`, `outcome`) — never per-song or per-owner. `BackgroundWorkMetrics` is the sibling meter `MusicHoarder.BackgroundWork`.
- `LogSanitizer` — strips CR/LF/tab from externally-sourced strings before structured logging (log-forging defense).
- Privacy posture (in ServiceDefaults): OTel log scopes off, ASP.NET hosting diagnostics filtered from the OTel exporter, `token` route parameters redacted from spans. The API never receives a client IP it can trust (everything comes via the frontend proxy); `ShareVisitor` is the only place a forwarded IP appears, and it is used only to derive a daily visitor key, never stored.

## Snapshots and versioning

### Enrichment snapshots (`MusicHoarder.Api/Snapshots/`)

`EnrichmentSnapshotService` captures point-in-time, library-wide snapshots of enrichment-pipeline quality (status counts, duplicate counts, average match confidence, provider attempt outcomes, AI grades) together with a **config fingerprint + hash** of the enrichment/grading options that produced them. Captures are skipped when nothing changed (same fingerprint, identical aggregates), pruned to 50 per owner, and diffed by `SnapshotComparison` — per-song classification into `Improved/Unchanged/Regressed` where a regression in any dimension dominates (e.g. a ≥10-point AI-score drop counts as regression even if the status held). `SnapshotConfigDiff` explains what changed between two configs. Surfaced by `/api/snapshots` (list, get, `compare`, capture, delete).

### Release version monitoring (`MusicHoarder.Api/Version/`)

`ReleaseUpdateMonitor` is a hosted `BackgroundService` polling GitHub's Releases API (named HTTP client with required `User-Agent`, **ETag-cached** so one instance hits GitHub a few times a day) to power the in-app "update available" banner. `SemVerComparer.IsUpdateAvailable` is deliberately conservative — anything it can't confidently parse as "strictly newer" returns false, and the `dev` sentinel is never behind a release — so an ambiguous comparison never nags. All network failures are swallowed; the last good snapshot is kept, so air-gapped deploys degrade to "no banner".

## Notifications (`MusicHoarder.Api/Notifications/`)

Web Push implemented from the RFCs (8030/8291/8292), no third-party push library:

- `VapidKeyStore` resolves the instance's VAPID identity once per process: explicit config → stored pair (data-protected in the DB) → newly generated and stored. A stored key that no longer unprotects (lost key ring) is unrecoverable, so the store rotates and deletes dead subscriptions; browsers re-subscribe because the client compares the key it subscribed with to the one the API now serves.
- `WebPushSender` encrypts and delivers one message per subscription. Its doc comment calls out a real security property: the push endpoint is a **user-chosen URL**, the one place the API requests an address a user picked, so it is validated against a known push-service allowlist over HTTPS both at subscription save time and at send time — a subscription can't be abused to POST into the server's own network.
- `WebPushMessage` supports TTL, urgency, and a `Topic` so a burst of messages to an offline phone collapses into one. `ChatPushService` (hosted service) and artist-release notifications are the senders; `/api/push` handles subscription lifecycle.

## Sharing (`MusicHoarder.Api/Sharing/`)

Three distinct mechanisms, all built on the tenancy query-filter model:

1. **Library grants (members):** admins grant members access to a whole library or specific artists/albums via `LibraryShareGrant` rows. `SharedLibraryGrantResolver` resolves the caller's active grants into `GrantSet`s, and `LibraryScope` splits everything the caller can read by grantor (`LibrarySlice`, with `IsSelf` as the flag every write path branches on — likes and plays go to `UserSongState` rows for granted music, never the granting admin's song row). `SharedSongProjection` is the single projector used by `GET /songs`, `GET /api/albums` and `GET /api/radio` for granted slices, so all three publish the same fields of another account's songs.
2. **Anonymous share links:** `SongShareFactory` mints token-scoped links (used by "Share link…" and by in-app chat, so a recipient can play a song whatever their access to the sender's library). `/api/share/…` is anonymous via the `RequireAuthMiddleware` allowlist but only ever reaches rows through the token.
3. **Visit counting:** `ShareVisitTracker` counts open/play beacons with a per-day salted visitor key (from `X-Forwarded-For` first hop — client-controlled, acceptable only here — plus user agent), never storing IP or UA; `ShareVisitSource` filters likely link-preview bots (which fetch og-tags and run no scripts, so beacons don't fire for them anyway) and classifies referrer/in-app-browser sources.
