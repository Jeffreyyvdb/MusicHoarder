# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

See `AGENTS.md` for the long-form design notes, agent personas, and Linear-issue workflow conventions that still apply here. This file is the quick orientation.

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

CI (`.github/workflows/ci.yml`) only builds and tests `MusicHoarder.Api.Tests`. A separate `frontend-release.yml` runs semantic-release on `main` pushes that touch `frontend/`. Docker must be running locally before `AppHost` starts, because Aspire provisions PostgreSQL as a container.

## Solution layout

- **`MusicHoarder.Api`** — ASP.NET Core minimal API. Composition root is `Program.cs` → `AddMusicHoarderServices()` + `MapMusicHoarderEndpoints()`. Hosts the full pipeline as `BackgroundService`s and EF Core persistence (Npgsql).
- **`MusicHoarder.AppHost`** — Aspire entry point. Wires Postgres (`ContainerLifetime.Persistent` + named data volume), API, and the SvelteKit frontend (`AddViteApp(...).WithBun()` with an HTTPS endpoint and Aspire dev cert). All required secrets/paths are modeled as `AddParameter(...)` and injected into the API as env vars (`MusicEnricher__*`, `Spotify__*`); the dashboard prompts for any missing values on first run. Frontend gets `MUSICHOARDER_API_URL` (HTTP for the internal Node→ASP.NET proxy hop); API gets `Frontend__PublicBaseUrl` (HTTPS, used for Spotify OAuth redirect-back). `AddDockerComposeEnvironment("compose")` lets `aspire publish` emit a `docker-compose.yml` for Dokploy.
- **`MusicHoarder.ServiceDefaults`** — Shared OpenTelemetry / health-check / resilient-HTTP defaults; `MapDefaultEndpoints()` is called from the API.
- **`frontend/`** — SvelteKit 2 + Svelte 5 (runes) + Bun. All backend calls go through the same-origin proxy route `/api/mh/[...path]` defined in `frontend/src/routes/api/mh/[...path]/+server.ts` so the browser never needs CORS. `PUBLIC_DEMO_MODE=true` switches the UI to mock data (no API required). The `(app)` route group sets `ssr = false` because the audio player and demo-mode flag read browser-only state; the marketing `/` route keeps SSR.
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

Env var form uses the double-underscore convention (`MusicEnricher__AcoustIdApiKey`, `ConnectionStrings__musichoarderdb`). Aspire injects the Postgres connection string automatically in dev. Frontend env vars exposed to the browser use SvelteKit's `PUBLIC_*` prefix (e.g. `PUBLIC_DEMO_MODE`, `PUBLIC_UMAMI_WEBSITE_ID`) — *not* `NEXT_PUBLIC_*`.

## Persistence

`MusicHoarderDbContext` is the only EF context. Schema changes always go through an EF migration under `MusicHoarder.Api/Persistence/Migrations/`; `ApplyPendingMigrationsAsync()` runs on startup, so don't ship manual SQL. `SongMetadata` is the hub entity and has `ProviderAttempts` as a collection — a `ResetEnrichment` must clear it.

## Branches and commits

- The Linear project ID prefix is `BRINK-`. Branch names and commit messages should reference the issue (`BRINK-36: ...`). Branch naming mirrors Linear's `gitBranchName` (e.g. `jeffreyvdbrink/brink-36-implement-enrichmentservice-orchestrator-background-service`).
- Prioritize earlier milestones (M1–M2) over later ones (M3–M5) unless the user directs otherwise.

## Frontend flex / scrolling gotcha

This comes up repeatedly in `frontend/`: lists look right but do not scroll because flex items default to `min-height: auto`. Any flex child that should take remaining height and contain a scrollable region (bits-ui `ScrollArea`, `Tabs.Content`) needs `min-h-0` on the child **and every intermediate flex ancestor** between `h-screen`/`flex-1` and the scroll viewport. The shadcn-svelte `scroll-area` and `tabs` primitives already include `min-h-0`; the fix is almost always further up the tree. Pages live under `src/routes/(app)/<name>/+page.svelte`.

## Pipeline dependencies

`fpcalc` (from `libchromaprint-tools`) must be on `PATH` or configured via `MusicEnricher:FpcalcPath`. Without it, songs get indexed but with `Fingerprint = null` and `DurationSeconds = null`, which means the AcoustID provider skips them and the library builder never promotes them to `Destination`. Without `MusicEnricher:AcoustIdApiKey`, the AcoustID provider falls back and songs typically land in `NeedsReview` rather than `Matched`. The frontend Library page's **Destination** view only shows rows where `LibraryBuildStatus == Done` and `DestinationPath` is set.

## Releases

Only the **frontend** is versioned by [semantic-release](https://github.com/semantic-release/semantic-release). Every push to `main` that touches `frontend/**` runs `.github/workflows/frontend-release.yml`, which gates on `bun run check` + `bun run lint` + `bun run build` and then publishes a [GitHub Release](https://github.com/Jeffreyyvdb/MusicHoarder/releases) with a fresh tag of the form `frontend-v${version}` if the new commits warrant one. The Releases page is the canonical changelog; `frontend/CHANGELOG.md` is a stub pointing at it. The .NET API is **not** semver-tagged — it ships via `docker-publish.yml` as `ghcr.io/.../musichoarder-api:sha-<commit>`.

**Commit messages are load-bearing** for any commit that touches `frontend/`: they must follow [Conventional Commits](https://www.conventionalcommits.org/).

| Prefix on the commit subject                       | Release bump      |
| -------------------------------------------------- | ----------------- |
| `fix:` / `fix(scope):`                             | patch (0.0.**X**) |
| `feat:` / `feat(scope):`                           | minor (0.**Y**.0) |
| `feat!:` / any commit with `BREAKING CHANGE:` foot | major (**X**.0.0) |
| `chore:`, `docs:`, `refactor:`, `test:`, `style:`  | no release        |

To dry-run locally from `frontend/`: `bun run release:dry` (requires Node ≥ v22.14 on PATH — semantic-release v25+ doesn't run under Bun's Node-compat layer; CI installs Node 24 alongside Bun and invokes `npx semantic-release` for that one step). `frontend/package.json`'s `version` field is intentionally stale — the canonical version is the latest `frontend-v*` git tag. No release commit is pushed back to `main`, so the `main` branch's required-status-check rules need no bypass actor; tags and Releases are created via the GitHub Releases API. Following the [semantic-release maintainers' recommendation](https://semantic-release.gitbook.io/semantic-release/support/faq#making-commits-during-the-release-process-adds-significant-complexity), `@semantic-release/git` and `@semantic-release/changelog` are not used.

Dependabot (`.github/dependabot.yml`) covers three ecosystems with a release-age cooldown (3d patch / 5d minor / 7d major where the ecosystem supports per-semver levels): `bun` for `frontend/` deps, `github-actions` for workflow versions, and `nuget` for the .NET projects. Bun's prod-deps PRs use the `fix(deps)` prefix and *will* cut a frontend patch release when they land — that's intentional. Nuget bumps use `chore(deps)` so they never trigger a frontend release. The `dependabot-auto-merge.yml` workflow squash-auto-merges bun patch + minor grouped PRs only; nuget and github-actions wait for human review.
