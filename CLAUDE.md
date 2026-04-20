# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

See `AGENTS.md` for the long-form design notes, agent personas, and Linear-issue workflow conventions that still apply here. This file is the quick orientation.

## Commands

All commands run from the repo root unless noted.

```bash
# Run the full stack (Aspire dashboard at https://localhost:17072)
# Provisions PostgreSQL in Docker, starts API + frontend, auto-applies EF migrations.
dotnet run --project MusicHoarder.AppHost

# One-time required user-secrets before first run (validated on startup):
dotnet user-secrets set "MusicEnricher:SourceDirectory" "/tmp/musichoarder-source" --project MusicHoarder.Api
dotnet user-secrets set "MusicEnricher:DestinationDirectory" "/tmp/musichoarder-dest" --project MusicHoarder.Api

# Tests (xUnit, in-memory EF provider — no Postgres/Docker required)
dotnet test MusicHoarder.Api.Tests/MusicHoarder.Api.Tests.csproj

# Run a single test class / test
dotnet test MusicHoarder.Api.Tests/MusicHoarder.Api.Tests.csproj --filter "FullyQualifiedName~EnrichmentOrchestratorTests"
dotnet test MusicHoarder.Api.Tests/MusicHoarder.Api.Tests.csproj --filter "DisplayName~matches_song_via_acoustid"

# Frontend standalone (point at API port from the Aspire dashboard)
cd frontend && MUSICHOARDER_API_URL=http://localhost:<api-port> PORT=3000 pnpm dev
cd frontend && pnpm build           # ESLint-free build path
cd frontend && pnpm lint            # uses eslint.config.mjs
```

CI (`.github/workflows/ci.yml`) only builds and tests `MusicHoarder.Api.Tests`; it does not run the frontend. Docker must be running locally before `AppHost` starts, because Aspire provisions PostgreSQL as a container.

## Solution layout

- **`MusicHoarder.Api`** — ASP.NET Core minimal API. Composition root is `Program.cs` → `AddMusicHoarderServices()` + `MapMusicHoarderEndpoints()`. Hosts the full pipeline as `BackgroundService`s and EF Core persistence (Npgsql).
- **`MusicHoarder.AppHost`** — Aspire entry point. Wires Postgres, API, and the Next.js frontend (`.WithPnpm()`), injects `Frontend__PublicBaseUrl` into the API for Spotify OAuth redirects.
- **`MusicHoarder.ServiceDefaults`** — Shared OpenTelemetry / health-check / resilient-HTTP defaults; `MapDefaultEndpoints()` is called from the API.
- **`frontend/`** — Next.js 16 + React 19 app. All backend calls go through the same-origin proxy route `/api/mh/[...path]` defined in `frontend/app/api/mh/` so the browser never needs CORS. `NEXT_PUBLIC_DEMO_MODE=true` switches the UI to mock data (no API required).
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

Everything non-secret lives under the `MusicEnricher` config section (`MusicEnricherOptions.cs`). It uses `ValidateDataAnnotations().ValidateOnStart()`, so missing `SourceDirectory` / `DestinationDirectory` will fail the app on boot. Concurrency knobs (`SmbConcurrency`, `FingerprintConcurrency`, `EnrichmentWorkerConcurrency`, `LibraryBuilderWorkerConcurrency`, per-provider concurrency/rps) and Spotify matching thresholds (`SpotifyApiMatchedThreshold`, `SpotifyApiIsrcConfidenceBoost`, `SpotifyApiDurationMismatchPenalty`) all live here — prefer adding options over hardcoding.

Env var form uses the double-underscore convention (`MusicEnricher__AcoustIdApiKey`, `ConnectionStrings__musichoarderdb`). Aspire injects the Postgres connection string automatically in dev.

## Persistence

`MusicHoarderDbContext` is the only EF context. Schema changes always go through an EF migration under `MusicHoarder.Api/Persistence/Migrations/`; `ApplyPendingMigrationsAsync()` runs on startup, so don't ship manual SQL. `SongMetadata` is the hub entity and has `ProviderAttempts` as a collection — a `ResetEnrichment` must clear it.

## Branches and commits

- The Linear project ID prefix is `BRINK-`. Branch names and commit messages should reference the issue (`BRINK-36: ...`). Branch naming mirrors Linear's `gitBranchName` (e.g. `jeffreyvdbrink/brink-36-implement-enrichmentservice-orchestrator-background-service`).
- Prioritize earlier milestones (M1–M2) over later ones (M3–M5) unless the user directs otherwise.

## Frontend flex / scrolling gotcha

This comes up repeatedly in `frontend/`: lists look right but do not scroll because flex items default to `min-height: auto`. Any flex child that should take remaining height and contain a scrollable region (Radix `ScrollArea`, `TabsContent`) needs `min-h-0` on the child **and every intermediate flex ancestor** between `h-screen`/`flex-1` and the scroll viewport. Reference implementations: `frontend/app/spotify/page.tsx`, `frontend/components/file-browser/file-browser.tsx`, `frontend/app/review/page.tsx`. The shared `components/ui/scroll-area.tsx` and `components/ui/tabs.tsx` already include `min-h-0`; the fix is almost always further up the tree.

## Pipeline dependencies

`fpcalc` (from `libchromaprint-tools`) must be on `PATH` or configured via `MusicEnricher:FpcalcPath`. Without it, songs get indexed but with `Fingerprint = null` and `DurationSeconds = null`, which means the AcoustID provider skips them and the library builder never promotes them to `Destination`. Without `MusicEnricher:AcoustIdApiKey`, the AcoustID provider falls back and songs typically land in `NeedsReview` rather than `Matched`. The frontend Library page's **Destination** view only shows rows where `LibraryBuildStatus == Done` and `DestinationPath` is set.
