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

CI (`.github/workflows/ci.yml`) only builds and tests `MusicHoarder.Api.Tests`. A separate `release.yml` runs unified semantic-release (API + frontend) on every push to `main` — see **Releases** below. Docker must be running locally before `AppHost` starts, because Aspire provisions PostgreSQL as a container.

## Solution layout

- **`MusicHoarder.Api`** — ASP.NET Core minimal API. Composition root is `Program.cs` → `AddMusicHoarderServices()` + `MapMusicHoarderEndpoints()`. Hosts the full pipeline as `BackgroundService`s and EF Core persistence (Npgsql).
- **`MusicHoarder.AppHost`** — Aspire entry point. Wires Postgres (`ContainerLifetime.Persistent` + named data volume), API, and the SvelteKit frontend (`AddViteApp(...).WithBun()` with an HTTPS endpoint and Aspire dev cert). All required secrets/paths are modeled as `AddParameter(...)` and injected into the API as env vars (`MusicEnricher__*`, `Spotify__*`); the dashboard prompts for any missing values on first run. Frontend gets `MUSICHOARDER_API_URL` (HTTP for the internal Node→ASP.NET proxy hop); API gets `Frontend__PublicBaseUrl` (HTTPS, used for Spotify OAuth redirect-back). `AddDockerComposeEnvironment("compose")` lets `aspire publish` emit a `docker-compose.yml` for Dokploy.
- **`MusicHoarder.ServiceDefaults`** — Shared OpenTelemetry / health-check / resilient-HTTP defaults; `MapDefaultEndpoints()` is called from the API.
- **`frontend/`** — SvelteKit 2 + Svelte 5 (runes) + Bun. All backend calls go through the same-origin proxy route `/api/mh/[...path]` defined in `frontend/src/routes/api/mh/[...path]/+server.ts` so the browser never needs CORS. The `(app)` route group sets `ssr = false` because the audio player reads browser-only state; the marketing `/` route keeps SSR. The only demo is the API-backed demo account (`/login` → "Try the demo" → `POST /api/auth/demo-login`).
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

Progress is surfaced via per-stage singletons (`ScanProgressTracker`, `FingerprintProgressTracker`, `EnrichmentProgressTracker`, `LibraryBuilderProgressTracker`) plus a central `JobManager` that enforces **one job at a time** (`/api/enrichment/scan|enrich|fingerprint|build` return `409 Conflict` if another job is running). Progress is streamed to the frontend via SSE endpoints under `/api/enrichment/*`.

## Configuration

Everything non-secret lives under the `MusicEnricher` config section (`MusicEnricherOptions.cs`). It uses `ValidateDataAnnotations().ValidateOnStart()`, so missing `SourceDirectory` / `DestinationDirectory` will fail the app on boot — these (and the AcoustID + Spotify credentials) come from AppHost parameters (`Parameters:source-directory`, `Parameters:destination-directory`, `Parameters:acoustid-api-key`, `Parameters:spotify-client-id`, `Parameters:spotify-client-secret`) stored in the AppHost user-secrets store. Concurrency knobs (`SmbConcurrency`, `FingerprintConcurrency`, `EnrichmentWorkerConcurrency`, `LibraryBuilderWorkerConcurrency`, per-provider concurrency/rps) and Spotify matching thresholds (`SpotifyApiMatchedThreshold`, `SpotifyApiIsrcConfidenceBoost`, `SpotifyApiDurationMismatchPenalty`) live in `appsettings.json` — prefer adding options over hardcoding.

Env var form uses the double-underscore convention (`MusicEnricher__AcoustIdApiKey`, `ConnectionStrings__musichoarderdb`). Aspire injects the Postgres connection string automatically in dev. Frontend env vars exposed to the browser use SvelteKit's `PUBLIC_*` prefix (e.g. `PUBLIC_UMAMI_WEBSITE_ID`) — *not* `NEXT_PUBLIC_*`.

## Persistence

`MusicHoarderDbContext` is the only EF context. Schema changes always go through an EF migration under `MusicHoarder.Api/Persistence/Migrations/`; `ApplyPendingMigrationsAsync()` runs on startup, so don't ship manual SQL. `SongMetadata` is the hub entity and has `ProviderAttempts` as a collection — a `ResetEnrichment` must clear it.

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

On a release, `release.yml` dispatches `aspire-deploy.yml` with the new version, which builds the api + frontend images, tags them `:X.Y.Z` / `:X.Y` / `:X` (plus `:latest`), and triggers the Dokploy redeploy. **Images build and deploy on releases only** — non-release pushes to `main` (chore/docs/refactor) no longer deploy. The per-commit `docker-publish.yml` image (`ghcr.io/.../musichoarder-api:sha-<commit>`) still ships on every commit and is independent of the semver line.

**Commit messages are load-bearing** for every commit (API or frontend): they must follow [Conventional Commits](https://www.conventionalcommits.org/), since any commit can now drive the shared version.

| Prefix on the commit subject                       | Release bump      |
| -------------------------------------------------- | ----------------- |
| `fix:` / `fix(scope):`                             | patch (0.0.**X**) |
| `feat:` / `feat(scope):`                           | minor (0.**Y**.0) |
| `feat!:` / any commit with `BREAKING CHANGE:` foot | major (**X**.0.0) |
| `chore:`, `docs:`, `refactor:`, `test:`, `style:`  | no release        |

To dry-run locally from `frontend/` (where the semantic-release toolchain is installed): `bun run release:dry` (requires Node ≥ v22.14 on PATH — semantic-release v25+ doesn't run under Bun's Node-compat layer; CI installs Node 24 alongside Bun and invokes `npx semantic-release` for that one step). `frontend/package.json`'s `version` field is intentionally stale — the canonical version is the latest `v*` git tag (a `v1.9.1` bridge tag continues the line from the retired `frontend-v*` tags). No release commit is pushed back to `main`, so the `main` branch's required-status-check rules need no bypass actor; tags and Releases are created via the GitHub Releases API. The downstream build is triggered with `gh workflow run` (a `workflow_dispatch`, the one event the default `GITHUB_TOKEN` is allowed to fire), so no PAT is needed. Following the [semantic-release maintainers' recommendation](https://semantic-release.gitbook.io/semantic-release/support/faq#making-commits-during-the-release-process-adds-significant-complexity), `@semantic-release/git` and `@semantic-release/changelog` are not used.

Dependabot (`.github/dependabot.yml`) covers three ecosystems with a release-age cooldown (3d patch / 5d minor / 7d major where the ecosystem supports per-semver levels): `bun` for `frontend/` deps, `github-actions` for workflow versions, and `nuget` for the .NET projects. Bun's prod-deps PRs use the `fix(deps)` prefix and *will* cut a patch release when they land — that's intentional. Nuget bumps use `chore(deps)` so they never cut a release on their own (avoids release churn from routine .NET bumps — bump to `fix(deps)` if you want API dependency updates to ship a version). The `dependabot-auto-merge.yml` workflow squash-auto-merges bun patch + minor grouped PRs only; nuget and github-actions wait for human review.
