# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) and other AI coding agents when working with code in this repository. It is the single source of truth for project conventions; `AGENTS.md` is a symlink to this file so all tools read the same guidance.

## This is a public, open-source repository

This repo is public on GitHub under the MIT license. Treat everything you commit as world-readable, permanent, and indexed.

- **Never commit secrets or credentials.** API keys, OAuth client secrets, Postgres passwords, Resend keys, etc. always come from environment variables, AppHost parameters, or user-secrets — never from tracked files. The committed `appsettings*.json` keep these fields empty; keep them that way.
- **No private/personal data.** No personal emails, internal hostnames, IP addresses, private URLs, server names, or deployment endpoints in tracked files. Deployment targets (Dokploy URL/key, etc.) live only in GitHub Actions secrets.
- **No local planning artifacts.** Don't commit scratch design docs, plan files, transcripts, or `.claude/` decision notes — those belong in your local environment, not the public history.
- If you're unsure whether something is safe to publish, leave it out and ask.

## Commands

All commands run from the repo root unless noted.

```bash
# Run the full stack (Aspire dashboard at https://localhost:17072)
# Provisions PostgreSQL in Docker, starts API + frontend, auto-applies EF migrations.
dotnet run --project MusicHoarder.AppHost

# First run: required values are modeled as AppHost parameters and prompted in the
# dashboard. To pre-seed (recommended for repeatable boots) set them as AppHost
# user-secrets — note the `Parameters:` prefix and the AppHost project:
dotnet user-secrets set "Parameters:source-directory" "/tmp/musichoarder-source" --project MusicHoarder.AppHost
dotnet user-secrets set "Parameters:destination-directory" "/tmp/musichoarder-dest" --project MusicHoarder.AppHost
# Optional (otherwise dashboard prompts as blank, providers gracefully degrade):
dotnet user-secrets set "Parameters:acoustid-api-key" "..." --project MusicHoarder.AppHost
dotnet user-secrets set "Parameters:spotify-client-id" "..." --project MusicHoarder.AppHost
dotnet user-secrets set "Parameters:spotify-client-secret" "..." --project MusicHoarder.AppHost

# Tests (xUnit, in-memory EF provider — no Postgres/Docker required)
dotnet test MusicHoarder.Api.Tests/MusicHoarder.Api.Tests.csproj

# Run a single test class / test
dotnet test MusicHoarder.Api.Tests/MusicHoarder.Api.Tests.csproj --filter "FullyQualifiedName~EnrichmentOrchestratorTests"
dotnet test MusicHoarder.Api.Tests/MusicHoarder.Api.Tests.csproj --filter "DisplayName~matches_song_via_acoustid"

# Frontend standalone (point at API port from the Aspire dashboard)
cd frontend && MUSICHOARDER_API_URL=http://localhost:<api-port> PORT=3000 bun run dev
cd frontend && bun run build        # SvelteKit + adapter-node build
cd frontend && bun run check        # svelte-check + TypeScript
cd frontend && bun run lint         # ESLint (flat config)
```

CI (`.github/workflows/ci.yml`) builds and tests `MusicHoarder.Api.Tests` and the frontend; the Android client has its own path-filtered `android.yml` (unit tests + both APK variants, only when `android/**` changes). Only `dotnet` and `frontend` are required status checks on `main` — which is what makes the Android path filter safe. A separate `release.yml` runs unified semantic-release (API + frontend) on every push to `main` — see **Releases** below. Docker must be running locally before `AppHost` starts, because Aspire provisions PostgreSQL as a container.

## Solution layout

- **`MusicHoarder.Api`** — ASP.NET Core minimal API. Composition root is `Program.cs` → `AddMusicHoarderServices()` + `MapMusicHoarderEndpoints()`. Hosts the full pipeline as `BackgroundService`s and EF Core persistence (Npgsql).
- **`MusicHoarder.AppHost`** — Aspire entry point. Wires Postgres (`ContainerLifetime.Persistent` + named data volume), API, and the SvelteKit frontend (`AddViteApp(...).WithBun()` with an HTTPS endpoint and Aspire dev cert). All required secrets/paths are modeled as `AddParameter(...)` and injected into the API as env vars (`MusicEnricher__*`, `Spotify__*`); the dashboard prompts for any missing values on first run. Frontend gets `MUSICHOARDER_API_URL` (HTTP for the internal Node→ASP.NET proxy hop); API gets `Frontend__PublicBaseUrl` (HTTPS, this env's own origin — the Spotify relay bounces the browser back here). Spotify OAuth uses one registered relay URI shared by every env (`Spotify__OAuthRelayUrl`); the relay route lives on the frontend (`/api/spotify/relay`) and is gated by a shared HMAC-signed `state` (`Spotify__OAuthStateSigningKey` / frontend `SPOTIFY_OAUTH_STATE_SIGNING_KEY`) plus a return-origin allowlist (`SPOTIFY_RETURN_ORIGIN_ALLOWLIST`) — see README "Spotify OAuth (relay)". `AddDockerComposeEnvironment("compose")` lets `aspire publish` emit a `docker-compose.yml` for Dokploy.
- **`MusicHoarder.ServiceDefaults`** — Shared OpenTelemetry / health-check / resilient-HTTP defaults; `MapDefaultEndpoints()` is called from the API.
- **`frontend/`** — SvelteKit 2 + Svelte 5 (runes) + Bun. All backend calls go through the same-origin proxy route `/api/mh/[...path]` defined in `frontend/src/routes/api/mh/[...path]/+server.ts` so the browser never needs CORS. The `(app)` route group sets `ssr = false` because the audio player reads browser-only state; the marketing `/` route keeps SSR. The only demo is the API-backed demo account (`/login` → "Try the demo" → `POST /api/auth/demo-login`).
- **`android/`** — Native Android client (Kotlin, Compose, Media3/ExoPlayer, Gradle — built separately from the .NET solution, not in CI). It talks to the **frontend** origin, not the API's: `/api/mh/*` is proxied header-for-header, so a paired phone needs only the host the browser already uses. Three sign-in paths, all ending at `pair()` with a proven bearer: a QR code from Settings → Account → Mobile app (`POST /api/auth/device-token` → `musichoarder://pair?v=1&url=&token=`); in-app email sign-in (`POST /api/auth/request-link` with `client: "app"` → emailed link lands on the frontend's `/auth/callback` handoff page → `musichoarder://auth?token=&url=` deep link → `POST /api/auth/token` exchanges the one-time token for a bearer); and **passkey** (`POST /api/auth/webauthn/authenticate/native/{begin,complete}` — the cookie-free ceremony variant: the in-flight challenge round-trips through the response body as a time-limited data-protected blob instead of a cookie, and the reply is a bearer instead of `Set-Cookie`). The bearer token is attached by a *network* interceptor that re-checks the host on every redirect hop so it can never follow a redirect to an external CDN. **In-app passkeys need two env values, not one**: the frontend's `ANDROID_ASSETLINKS_FINGERPRINTS` (the assetlinks statement list carries `get_login_creds` alongside `handle_all_urls`, so Credential Manager will offer the origin's passkeys) *and* the API's `ANDROID_APK_KEY_HASH_ORIGIN` → `WebAuthn:Origins[0]`, because an in-app assertion's origin is `android:apk-key-hash:<base64url-sha256>` and fido2-net-lib compares non-URL origins as exact strings. Both blank is the safe default and costs only the in-app passkey. See `android/README.md`.
- **`MusicHoarder.Api.Tests`** — xUnit + `Microsoft.EntityFrameworkCore.InMemory`. Mirror the source folder layout (`Enrichment/`, `Jobs/`, `Library/`, `Scanner/`, `Spotify/`).

## Pipeline architecture

The pipeline is a state machine over `SongMetadata` (`MusicHoarder.Api/Persistence/SongMetadata.cs`), driven by four hosted services that each sweep the DB for rows in the status they handle:

```
Scanner → Fingerprint → Enrichment (multi-provider) → Duplicate detection → LibraryBuilder
```

Key status enums on `SongMetadata` — treat them as the contract between stages:
- `EnrichmentStatus`: `Pending → Matched | NeedsReview | Failed`
- `LibraryBuildStatus`: `Pending → Copied → Tagged → Done` (or `Failed`)
- `LyricsStatus`: `NotFetched → Fetched | Instrumental | NotFound | Failed`

Readiness gates are expressed as computed properties (`IsReadyForEnrichment`, `IsReadyForBuild`, `IsReadyForLyricsFetch`) — prefer extending those rather than duplicating the predicates in queries. `SoftDelete()` sets `DeletedAtUtc`; `IsDeleted` is derived — never physically delete rows.

Enrichment is **multi-provider**, not a single call. Each `IEnrichmentProvider` (AcoustID, MusicBrainz web, Spotify API, community trackers) writes a `SongProviderAttempt` row, and `SongMetadata.ComputeSummaryStatus(enabledProviders)` derives the overall `EnrichmentStatus` from the set of attempts + the currently-enabled providers (`MusicEnricherOptions.EnableXxxProvider`). When adding or touching a provider, go through `EnrichmentOrchestrator` / `EnrichmentPipelineChannel` and update `ComputeSummaryStatus` if new terminal states are introduced.

Before modifying enrichment metadata on a song, call `CaptureOriginalMetadata()` (or go through `ApplyEnrichmentMatch`, which does it for you). `ResetEnrichment(restoreOriginal: true)` is the supported way to re-run enrichment for a song — it also clears `ProviderAttempts` and lyrics.

Because enrichment is per-song, tracks of one album can carry inconsistent album-IDENTITY tags (release id, album, year…). At build time `AlbumIdentityReconciler` elects one canonical `AlbumIdentity` per destination album folder (from the full folder membership, not just the batch) and the tag writer applies it to every track — so a server's MusicBrainz-release grouping (e.g. Navidrome's default `PID.Album`) can't split one on-disk album. It's **build-time, non-persisted** (DB rows keep their per-track enrichment, no grade-staleness impact) and gated by `MusicEnricher:EnableAlbumIdentityReconciliation` (default on). Already-built files aren't re-tagged automatically (the build skips `Done` rows) — `POST /api/enrichment/rebuild/album?artist=&album=` re-queues an album's `Done` tracks via `SongMetadata.RequeueForRetag()` (keeps `DestinationPath`, no re-enrichment) so the next build re-tags them in place; the album page's "Re-tag" button calls it.

Progress is surfaced via per-stage singletons (`ScanProgressTracker`, `FingerprintProgressTracker`, `EnrichmentProgressTracker`, `LibraryBuilderProgressTracker`) plus a central `JobManager` that enforces **one job at a time per step** — steps are independent and run concurrently, so a Build doesn't block a Scan (`/api/enrichment/scan|enrich|fingerprint|build` return `409 Conflict` only if *that same step* is already running). Progress is streamed to the frontend via SSE endpoints under `/api/enrichment/*`.

The source library is re-scanned automatically every `MusicEnricher:AutoScanIntervalMinutes` (default 15, `0` disables) by `AutoScanBackgroundService`, so files copied onto the share are ingested without a manual Scan — otherwise a scan only fires on startup, on a source offline→online edge, or after a download. `MusicEnricher:ScanSettleSeconds` (default 60) makes a scan skip files touched more recently than that, so a scan landing mid-copy doesn't index a half-written file. Auto-triggered scans respect a paused Scan step; note `JobManager.TryStartJob` deliberately *clears* the pause flag (it models a user action), so any new auto-trigger must check `IsStepPaused` first.

## Configuration

Everything non-secret lives under the `MusicEnricher` config section (`MusicEnricherOptions.cs`). It uses `ValidateDataAnnotations().ValidateOnStart()`, so missing `SourceDirectory` / `DestinationDirectory` will fail the app on boot — these (and the AcoustID + Spotify credentials) come from AppHost parameters (`Parameters:source-directory`, `Parameters:destination-directory`, `Parameters:acoustid-api-key`, `Parameters:spotify-client-id`, `Parameters:spotify-client-secret`, plus the Spotify OAuth relay trio `Parameters:spotify-oauth-relay-url`, `Parameters:spotify-oauth-state-key`, `Parameters:spotify-return-origin-allowlist`) stored in the AppHost user-secrets store. Concurrency knobs (`SmbConcurrency`, `FingerprintConcurrency`, `EnrichmentWorkerConcurrency`, `LibraryBuilderWorkerConcurrency`, per-provider concurrency/rps) and Spotify matching thresholds (`SpotifyApiMatchedThreshold`, `SpotifyApiIsrcConfidenceBoost`, `SpotifyApiDurationMismatchPenalty`) live in `appsettings.json` — prefer adding options over hardcoding.

Env var form uses the double-underscore convention (`MusicEnricher__AcoustIdApiKey`, `ConnectionStrings__musichoarderdb`). Aspire injects the Postgres connection string automatically in dev. Frontend env vars exposed to the browser use SvelteKit's `PUBLIC_*` prefix (e.g. `PUBLIC_UMAMI_WEBSITE_ID`) — *not* `NEXT_PUBLIC_*`.

## Persistence

`MusicHoarderDbContext` is the only EF context. Schema changes always go through an EF migration under `MusicHoarder.Api/Persistence/Migrations/`; `ApplyPendingMigrationsAsync()` runs on startup, so don't ship manual SQL. `SongMetadata` is the hub entity and has `ProviderAttempts` as a collection — a `ResetEnrichment` must clear it.

## Accounts, tenancy, and sharing

**Three roles: `Admin`, `Demo`, `Member`** (`UserRole`, values 0/1/2 — the numbers are the DB contract and must never move). Admin and Demo are seeded at migration time (`WellKnownUsers`, frozen GUIDs); **Member** rows are runtime-created by accepting an admin-minted, email-bound `Invite` (hash-only token, `POST /api/invite/accept` — the *only* runtime `User` insert path). Note the deliberate split between **role** and **tenancy**: the word "Owner" survives only in tenancy names (`OwnerUserId`, `WellKnownUsers.OwnerId`, `Auth__OwnerEmail`), which answer *whose rows are these*, never *who runs the instance*.

**What an account may do is a `[Flags] Capability` column on `User`** (`DownloadMusic`, `TrackListening`, `ManageOwnShares`, `Administer`), not a consequence of its role. Always authorize through `CurrentUser.Can()` / `.Effective`, never the raw column: an Admin implicitly holds every flag, which is what stops a fresh instance (seeded `Capabilities = 0`) locking its own admin out. `.RequireAdmin()` (over `RequireCapabilityFilter`) gates every pipeline and curation endpoint; `.RequireNonDemo()` gates passkey self-enrolment. `DownloadMusic` is **defined but not wired** — the pipeline is still single-tenant, so do not gate `/api/wishlist` on it yet.

**One API surface.** Members read the *ordinary* endpoints (`/songs`, stream/cover/lyrics/video/like/played); there is no parallel surface and no client-side "library mode". `/api/shared/*` survives only as a deprecated alias over the same handlers, for already-installed Android builds. THE rule: **the ambient EF query filter always means "rows I own"; every cross-tenant read goes through `ILibraryScopeResolver`, which re-scopes explicitly to a named grantor id.** Never widen the global query filters to include grants — grants are rows, the compiled model is cached per user id, and a baked-in grant predicate goes stale the moment one is revoked. A member owns zero `SongMetadata` rows, so the own-rows half of every query is empty for them by construction; that is the property that makes the unification safe. Grantee-facing rows go through `SharedSongRowDto` (a separate type, so a new column is excluded by default and `SharedProjectionSurfaceTests` fails on any widening) — and remember that DTO pins the LIST only: redact per-response bodies (a 404 naming a path, a yt-dlp `LastError`) by hand.

**Likes and plays** follow one sentence: *write the song row's own columns if you own it, a `UserSongState` row if you do not.* Branch on `slice.IsSelf`, never on role — an admin can hold a grant too. The Navidrome and instance-sync enqueues stay strictly inside the owns-it branch; they mirror the library owner's taste to their own servers, so a guest's like must never reach them.

**Writes are deny-by-default for members** (`MemberWriteGuardMiddleware`): every unsafe verb is rejected unless it matches an explicit anchored per-verb rule, and a rule may additionally require a capability. Extend the allowlist; never weaken the default, and never make it prefix-based — allowing `POST /songs/{id}/like` by prefix would also allow `DELETE /songs/{id}`.

**Wire compatibility:** `/api/auth/me` still emits the OLD role words (`Owner`/`Demo`/`Friend`) via `WireRole`, because shipped Android builds branch on them. New clients must read `isAdmin` and `capabilities` and ignore `role`.

**Multi-account sign-in** (Google-style) lets one browser/phone remember several accounts and switch between them. Web: `mh_session` stays the active session; a second HttpOnly cookie `mh_session_alts` holds the parked sessions' protected ids (distinct DataProtection purpose, recency-ordered, capped at 4). Every cookie-writing login flow (magic-link consume, demo-login, passkey complete, invite accept) goes through `AccountSwitchService.SignInAsync`, which parks a still-valid session for a *different* user instead of discarding it (same-user re-login replaces). `GET /api/auth/accounts` lists them (pruning dead entries); `POST /api/auth/switch {userId}` swaps active↔parked — possession of the alts cookie **is** the credential: it never mints a session and is allowlisted in *both* read-only middlewares so Friend/Demo can switch back. Logout revokes the active session and falls back to the newest parked account; `?all=true` revokes only the active *user's* sessions and forgets (never revokes) other users' parked ones. The frontend always hard-reloads (`location.assign`) after a switch/login/fallback — the `(app)` group's module singletons must not survive an identity change. Android mirrors this: `SessionStore` keeps an accounts list under one `accounts_json` DataStore key (single string, so the synchronous `runBlocking` init that `PlaybackService` depends on stays cheap; legacy `base_url`/`token`/`role` keys migrate on first persist), each account is its own pairing (own device token, possibly its own server), and a 401 evicts only the offending account, falling back to the next before dropping to the pair screen.

Frontend-wise a member **reuses the admin's Listen routes and components** (`/overview`, `/library`, `/artists`, `/tracks`) with no data-layer switch — the endpoints already scope to the caller. Branch on `isAdmin(user)` / `can(user, cap)` from `$lib/auth/capabilities`, never on `user.role` (that carries the legacy vocabulary and will change). `App.PageData` types `page.data.user`, so no component casts it. `navGroupsFor(user)` narrows every nav surface and `allowedPathPrefixesFor(user)` **derives** the `(app)` route guard from the same data, so the two cannot drift — and **the Demo account is deliberately NOT narrowed**: it exists to show the whole product and is already write-blocked server-side. "Shared by X" attribution comes from the `grantors[]` array on the songs response, mirrored into `songsStore` (a rune) — read `songsStore.grantors` / `.grantorOf`, never the api-client's plain module copy, which cannot be reactive.

## Two clients, one product

There are two full clients over the same API — `frontend/` (SvelteKit) and `android/` (Compose) — and the Android one is a deliberate **line-by-line port** of the web's library modules, not an independent app. `ArtistGrouping.kt` / `LibraryFold.kt` are ports of `api-client.ts` and `LibraryV2.svelte`; `NowPlayingScreen.kt` is a port of `TrackPanel.svelte`; the Compose theme tokens in `ui/theme/Color.kt` are the OKLCH values from `app.css`. The whole point is that the two agree about what the library contains and what a tap does.

**So: when you change one client, work out what the change means for the other in the same piece of work.** Not "file an issue" — plan both, and say which you did.

- **Grouping, filtering, sorting and readiness rules are shared semantics.** An artist label, what the Tracks list covers, what "built" means: change it on one side and the two clients disagree about the library itself. These live in `api-client.ts` + `LibraryV2.svelte` on the web and in `data/` on Android, and Android's unit tests exist to pin them against their JavaScript originals.
- **Album grouping is the exception, and the direction of travel.** It is no longer ported: `GET /api/albums` (`Library/AlbumProjection.cs`) decides what an album is — folder grouping, the name merge, the year election, the added-date rule, the aggregates — and both clients fetch cards and join `trackIds` against the `/songs` list they already hold. It moved because two copies of those rules produced the same bug twice (PR #453) and had silently drifted in six ways. `AlbumGrouping.kt` and the web's album helpers now only order and join. Two traps if you touch the projection: it must NOT reuse `AlbumGroupKey` (that is the pipeline's *logical* album, which folds a credit to its lead artist and would change the album count), and a granted slice must be grouped only from what `SharedSongRowDto` publishes — no destination path, and no Spotify save history, which an ordering would leak as surely as a field.
- **Navigation and affordances are shared too, adapted rather than copied.** A web link becomes a Compose tap target, and a hover-only affordance has to grow a resting one, because a finger cannot hover. Same destination, platform-appropriate signalling.
- **Server-side changes have two callers.** A new field, a renamed enum, a narrowed response, a new capability gate: check `android/app/src/main/java/com/musichoarder/app/data/` as well as `frontend/src/lib/`. Shipped Android builds are in the field and cannot be updated in lockstep, so wire compatibility is a real constraint (see the `WireRole` note above).
- **Genuinely one-sided work is fine** — the pipeline, Inbox, curation and settings screens are admin surfaces the phone does not carry. Say so rather than leaving it implicit.

CI reflects the split: `ci.yml` (dotnet + frontend) is required on `main`, and `android.yml` is path-filtered to `android/**`. That means an Android regression will **not** block a merge — the only thing keeping the phone working is remembering it here.

## Coding conventions

- **Minimal API composition**: keep `Program.cs` focused on composition (service registration, middleware, endpoint mapping) via the `AddMusicHoarderServices()` / `MapMusicHoarderEndpoints()` extensions. Prefer extension methods for cross-cutting concerns.
- **DI everywhere**: constructor injection for services, options, and `DbContext`. Decouple behind interfaces (`IEnrichmentProvider`, `IFileScanner`, etc.). Register long-running workers via `AddHostedService<T>()`.
- **Records for DTOs**: use records for small immutable carriers (`ScanRequest`, progress/result types) with names that map to domain concepts. Prefer explicit enums + status fields over magic strings.
- **Background processing**: derive workers from `BackgroundService`; decouple HTTP requests from heavy work via channels/queues; every long-running op takes and respects a `CancellationToken`. Use bounded concurrency (`SemaphoreSlim`) for IO-heavy work (SMB, dataset streaming, external APIs) with limits from configuration, and batch DB writes.
- **Logging**: structured logging with context properties (job/scan id, file path, counts) flowing through `ServiceDefaults` observability. Never log secrets or full URLs containing key query parameters.

## Safety and data handling

- **Non-destructive by default**: the library builder only reads from source and writes new copies to the destination — it must never modify source files. Use soft-delete (`SoftDelete()` / derived `IsDeleted`) for removed/missing files; never physically delete rows.
- **Safe paths in dev**: point scanners/builders at local test directories, not real NAS shares, unless explicitly configured.
- **External services**: respect rate limits and set appropriate user agents when scraping trackers or calling APIs; use retries with backoff for transient failures and stop at error thresholds.

## Branches and commits

- Commit messages must follow [Conventional Commits](https://www.conventionalcommits.org/) — they drive the shared semantic-release version (see **Releases**). Use a descriptive, lowercase scope where it helps (`feat(spotify): ...`, `fix(apphost): ...`).
- Use short, descriptive branch names (e.g. `feat/spotify-isrc-matching`, `fix/oauth-redirect`). Reference a GitHub issue number in the PR when one exists.

## Frontend flex / scrolling gotcha

This comes up repeatedly in `frontend/`: lists look right but do not scroll because flex items default to `min-height: auto`. Any flex child that should take remaining height and contain a scrollable region (bits-ui `ScrollArea`, `Tabs.Content`) needs `min-h-0` on the child **and every intermediate flex ancestor** between `h-screen`/`flex-1` and the scroll viewport. The shadcn-svelte `scroll-area` and `tabs` primitives already include `min-h-0`; the fix is almost always further up the tree. Pages live under `src/routes/(app)/<name>/+page.svelte`.

## Pipeline dependencies

`fpcalc` (from `libchromaprint-tools`) must be on `PATH` or configured via `MusicEnricher:FpcalcPath`. Without it, songs get indexed but with `Fingerprint = null` and `DurationSeconds = null`, which means the AcoustID provider skips them and the library builder never promotes them to `Destination`. Without `MusicEnricher:AcoustIdApiKey`, the AcoustID provider falls back and songs typically land in `NeedsReview` rather than `Matched`. The frontend Library page's **Destination** view only shows rows where `LibraryBuildStatus == Done` and `DestinationPath` is set.

## Releases

The whole repo (API **and** frontend together) is versioned by [semantic-release](https://github.com/semantic-release/semantic-release) as a single line. Every push to `main` runs `.github/workflows/release.yml`, which gates on the frontend (`bun run check` + `bun run lint` + `bun run build`) **and** the API (`dotnet test`), then analyzes all Conventional Commits since the last release and, if warranted, publishes one [GitHub Release](https://github.com/Jeffreyyvdb/MusicHoarder/releases) with a fresh tag of the form `v${version}` covering both deployables. The Releases page is the canonical changelog; `frontend/CHANGELOG.md` is a stub pointing at it.

On a release, `release.yml` dispatches `aspire-deploy.yml` with the new version, which builds the api + frontend images, tags them `:X.Y.Z` / `:X.Y` / `:X` (plus `:latest`), and triggers the Dokploy redeploy. **Images build and deploy on releases only** — non-release pushes to `main` (chore/docs/refactor) no longer deploy. The per-commit `docker-publish.yml` image (`ghcr.io/.../musichoarder-api:sha-<commit>`) still ships on every commit and is independent of the semver line. The same release run also builds the Android client at that version — the APK is attached to the Release, and the App Bundle is uploaded to Google Play (track from the `ANDROID_PLAY_TRACK` repo variable, default `internal`, because the production track does not fit a repo that releases several times a day). Both Android steps are best-effort: they run after semantic-release so an Android breakage never holds back the API/frontend deploy. See `android/README.md`.

**Commit messages are load-bearing** for every commit (API or frontend): they must follow [Conventional Commits](https://www.conventionalcommits.org/), since any commit can now drive the shared version.

| Prefix on the commit subject                       | Release bump      |
| -------------------------------------------------- | ----------------- |
| `fix:` / `fix(scope):`                             | patch (0.0.**X**) |
| `feat:` / `feat(scope):`                           | minor (0.**Y**.0) |
| `feat!:` / any commit with `BREAKING CHANGE:` foot | major (**X**.0.0) |
| `chore:`, `docs:`, `refactor:`, `test:`, `style:`  | no release        |

To dry-run locally from `frontend/` (where the semantic-release toolchain is installed): `bun run release:dry` (requires Node ≥ v22.14 on PATH — semantic-release v25+ doesn't run under Bun's Node-compat layer; CI installs Node 24 alongside Bun and invokes `npx semantic-release` for that one step). `frontend/package.json`'s `version` field is intentionally stale — the canonical version is the latest `v*` git tag (a `v1.9.1` bridge tag continues the line from the retired `frontend-v*` tags). No release commit is pushed back to `main`, so the `main` branch's required-status-check rules need no bypass actor; tags and Releases are created via the GitHub Releases API. The downstream build is triggered with `gh workflow run` (a `workflow_dispatch`, the one event the default `GITHUB_TOKEN` is allowed to fire), so no PAT is needed. Following the [semantic-release maintainers' recommendation](https://semantic-release.gitbook.io/semantic-release/support/faq#making-commits-during-the-release-process-adds-significant-complexity), `@semantic-release/git` and `@semantic-release/changelog` are not used.

Dependabot (`.github/dependabot.yml`) covers three ecosystems with a release-age cooldown (3d patch / 5d minor / 7d major where the ecosystem supports per-semver levels): `bun` for `frontend/` deps, `github-actions` for workflow versions, and `nuget` for the .NET projects. Bun's prod-deps PRs use the `fix(deps)` prefix and *will* cut a patch release when they land — that's intentional. Nuget bumps use `chore(deps)` so they never cut a release on their own (avoids release churn from routine .NET bumps — bump to `fix(deps)` if you want API dependency updates to ship a version). The `dependabot-auto-merge.yml` workflow squash-auto-merges bun patch + minor grouped PRs only; nuget and github-actions wait for human review.
