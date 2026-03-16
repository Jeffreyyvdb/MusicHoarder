## AGENTS for MusicHoarder

MusicHoarder is a .NET Aspire-style backend that scans a large music library (including NAS SMB shares), fingerprints and enriches tracks using external datasets (AcoustID, MusicBrainz, Spotify, community trackers), and then builds a clean, organized destination library. This file explains how AI agents should collaborate in this repo, how they should use Linear issues, and which coding and architectural conventions they must follow.

This document is written for code-focused AI agents working in Cursor and for humans who want to steer them effectively.

---

## How to use AI agents in this repo

- **Start from Linear**: When picking up work, first identify the relevant Linear issue(s) in the `MusicHoarder` project (IDs like `BRINK-28`, `BRINK-36`, etc.). Treat those as the source of truth for requirements, especially for pipeline behavior and API shape.
- **Anchor every change**: For any non-trivial change, explicitly note which Linear issue you are implementing in your working notes, branch name, and (when requested) commit/PR descriptions.
- **Respect existing architecture**: Follow the patterns already used in `MusicHoarder.Api`, `MusicHoarder.AppHost`, and `MusicHoarder.ServiceDefaults`. Prefer extending these patterns over inventing new ones.
- **Propose, then implement**: For large changes (new background services, controllers, or DB schema changes), briefly outline the approach in comments or description to the user before making sweeping edits.
- **Keep the pipeline coherent**: Always think about where your change lives in the end-to-end flow: scanning → fingerprinting → enrichment → duplicate detection → manual review → library building.

---

## Architecture and solution overview

- **Projects**
  - **`MusicHoarder.Api`**: ASP.NET Core minimal API project. Hosts HTTP endpoints (e.g. `/scan`, future `/api/library/*`, `/api/enrichment/*`, `/api/tracks/*`), EF Core persistence, and background services for scanning/indexing and eventually enrichment and library building.
  - **`MusicHoarder.AppHost`**: Aspire AppHost executable that composes the application, references `MusicHoarder.Api`, and orchestrates infrastructure like PostgreSQL. This is the main entry point for multi-service/deployment scenarios.
  - **`MusicHoarder.ServiceDefaults`**: Shared library containing cross-cutting defaults: health checks, telemetry (OpenTelemetry), service discovery, resilient HTTP clients, and default endpoints (`MapDefaultEndpoints`).

- **Current implementation highlights**
  - **Minimal API setup** (`MusicHoarder.Api/Program.cs`):
    - Uses `WebApplication.CreateBuilder`, `builder.AddServiceDefaults()`, and `builder.AddNpgsqlDbContext<MusicHoarderDbContext>("musichoarderdb")`.
    - Registers a channel-based background scanner (`ScannerBackgroundService`) and services like `IFileScanner` and `IIndexService`.
    - Exposes a `/scan` POST endpoint that enqueues a `ScanRequest` into a channel and returns `202 Accepted`.
  - **Persistence** (`MusicHoarder.Api.Persistence`):
    - `SongMetadata` is the central entity, capturing file path, size, extension, last modified time, tag metadata (artist/album/title/year/track), fingerprint, duration, and soft-delete flags (`IsDeleted`, `DeletedAt`).
    - `MusicHoarderDbContext` manages `Songs` and has migrations establishing the initial schema and indexes.
  - **Scanner / indexer** (`MusicHoarder.Api.Scanner`):
    - `ScannerBackgroundService` reads `ScanRequest`s from a channel and orchestrates indexing using `IIndexService`.
    - `IndexService` walks the source directory (currently `/Volumes/music`), filters supported audio extensions, compares disk state with DB records, marks missing files as deleted, and uses `IFileScanner` to read metadata for new/changed files.
    - Progress is reported via `IndexProgress` and logged periodically; results are summarized in `IndexResult`.

- **Planned pipeline (from Linear issues)**
  - **ScannerService**: Discovers files on SMB shares, copies to a temp directory, reads tags with TagLibSharp, runs `fpcalc` (Chromaprint), and persists basic info + fingerprints.
  - **FpcalcService**: Wraps the `fpcalc` CLI (`libchromaprint-tools`) to produce duration + acoustic fingerprints.
  - **Enrichment pipeline**:
    1. AcoustID lookup service to map fingerprints to MusicBrainz recordings.
    2. Local MusicBrainz dataset importer + match service (SQLite or similar).
    3. Spotify `.jsonl.zst` dataset streaming match service as a fallback.
    4. Community tracker scrapers (e.g., Juice WRLD / Kanye West trackers) to enrich unreleased/leak content.
  - **Duplicate detection**: Fingerprint-based duplicate detection to keep the highest-quality copy.
  - **Library builder**: `LibraryBuilderService` and `DestinationPathResolver` that copy approved tracks to a clean destination tree and write enriched tags without modifying source files.
  - **Controllers & UX surface**:
    - `EnrichmentController` with SSE progress streaming for scan/enrich/build jobs.
    - `LibraryController` for browsing/querying the track index and stats.
    - `TrackController` for manual review / approval / rejection of low-confidence matches.
  - **Deployment**: Dockerfile and `docker-compose.yml` optimized for TrueNAS SCALE + Portainer, with NAS SMB shares mounted as volumes and `fpcalc` available in the container.

---

## Agent personas and responsibilities

### Architecture & Planning Agent

- **Purpose**: Guardrail the overall architecture and ensure new work fits the long-term design and Linear roadmap.
- **Primary responsibilities**:
  - Translate Linear milestones (e.g., *M1 — Project Foundation*, *M2 — Library Scanner*, *M3 — Enrichment Pipeline*, *M4 — Library Builder*, *M5 — API & Frontend Readiness*) into coherent technical tasks.
  - Define boundaries between services (scanner, enrichment, library builder, controllers) and their interfaces.
  - Decide when to introduce new background services, controllers, or entities, and how they fit in the solution layout.
  - Keep architecture diagrams and high-level documentation aligned with reality.
- **How this agent works**:
  - Reads relevant Linear issues and drafts a brief design (interfaces, key methods, data flow).
  - Ensures new APIs are compatible with planned SvelteKit (or similar) frontend and future integrations.
  - Hands off concrete implementation tasks to the Backend Implementation, Data & Enrichment, or DevOps agents.

### Backend Implementation Agent

- **Purpose**: Implement and refactor backend logic within `MusicHoarder.Api`, with a focus on correctness, performance, and maintainability.
- **Primary responsibilities**:
  - Implement new minimal API endpoints and controllers (e.g., `LibraryController`, `EnrichmentController`, `TrackController`).
  - Create and evolve EF Core entities and `MusicHoarderDbContext` (including migrations and indexes).
  - Write and maintain background services for scanning, enrichment orchestration, and library building.
  - Integrate configuration binding for paths, concurrency limits, and external API keys.
- **How this agent works**:
  - Follows patterns from `Program.cs`, `ScannerBackgroundService`, and `IndexService` to keep the codebase consistent.
  - Uses DI (constructor injection) for services and options.
  - Coordinates with the Data & Enrichment Agent when dealing with external datasets or API integration.

### Data & Enrichment Agent

- **Purpose**: Own the data-heavy enrichment pipeline and duplicate detection logic.
- **Primary responsibilities**:
  - Implement services for AcoustID lookup, MusicBrainz import/matching, Spotify dataset streaming, and tracker scraping.
  - Design data models and tables needed for enrichment metadata (e.g., local MusicBrainz tables, Spotify-derived entities, leak tracker tables).
  - Implement fingerprint-based duplicate grouping and selection rules.
  - Optimize streaming and batching logic to avoid exhausting memory while processing large datasets.
- **How this agent works**:
  - Aligns implementations with issues like `BRINK-31`, `BRINK-32`, `BRINK-33`, `BRINK-34`, `BRINK-35`, `BRINK-36`, and `BRINK-39`.
  - Provides clear, simple interfaces for the Backend Implementation Agent and EnrichmentService orchestrator to call.
  - Uses resilient HTTP and IO patterns (timeouts, retries, backoff) when talking to external services.

### DevOps & Deployment Agent

- **Purpose**: Make MusicHoarder easy to run locally and deploy on TrueNAS/Portainer or similar environments.
- **Primary responsibilities**:
  - Maintain Dockerfile(s) and `docker-compose.yml` to run the full stack (AppHost, API, PostgreSQL, sidecars if any).
  - Ensure `fpcalc` and other OS-level dependencies (e.g., `libchromaprint-tools`, `ZstdSharp` native bindings) are correctly installed and available.
  - Configure health checks, readiness/liveness probes, and metrics exports based on `ServiceDefaults`.
  - Document environment variables and configuration required for deployment.
- **How this agent works**:
  - Adheres to patterns in `MusicHoarder.AppHost` and `MusicHoarder.ServiceDefaults`.
  - Uses Linear issues like `BRINK-23` and `BRINK-43` as references for desired deployment behavior.
  - Avoids introducing non-portable dependencies that conflict with NAS/TrueNAS environments.

### Progress & UX/API Agent

- **Purpose**: Provide a great API surface and progress reporting UX for humans and the future frontend.
- **Primary responsibilities**:
  - Implement and refine endpoints for job triggering and monitoring (scan, enrich, build, cancel).
  - Implement SSE-based progress streaming endpoints for long-running jobs.
  - Implement library browsing, filtering, and stats endpoints (`/api/library/*`).
  - Implement manual review endpoints for low-confidence matches (`/api/tracks/*`).
- **How this agent works**:
  - Uses issues like `BRINK-10`, `BRINK-20`, `BRINK-21`, `BRINK-40`, `BRINK-41`, `BRINK-22`, and `BRINK-42` as functional specs.
  - Ensures endpoints are ergonomic for a SvelteKit-style frontend (e.g., well-structured JSON, pagination, query parameters).
  - Coordinates with Backend Implementation and Data & Enrichment agents to avoid duplicating logic.

### Documentation Agent

- **Purpose**: Keep docs understandable, up-to-date, and aligned with the code and Linear roadmap.
- **Primary responsibilities**:
  - Maintain `README.md`, `AGENTS.md`, and any future `docs/` content.
  - Document new background services, controllers, and configuration contracts when they are added.
  - Capture key design decisions and trade-offs to prevent re-litigating past choices.
- **How this agent works**:
  - Runs after significant architectural or behavioral changes to refresh docs.
  - Summarizes the relationship between code, configuration, and external services.
  - Keeps references to relevant Linear issues where appropriate.

---

## Collaboration & workflow

### Task sourcing and scoping

- **Start from Linear**:
  - Use the `MusicHoarder` project in Linear as the backlog and roadmap.
  - Filter by milestone (M1–M5) to understand sequence and priority of work.
  - For each task, read the entire issue description (including example code, API contracts, and acceptance criteria).

- **Scoping changes**:
  - Prefer small, well-scoped changes that fully implement one issue or a clearly defined subset.
  - When an issue is too large, the Architecture & Planning Agent should propose a breakdown (e.g., service skeleton, infrastructure wiring, then incremental feature layers).

### Handoffs between agents

- **Architecture → Backend / Data / DevOps**:
  - Architecture & Planning Agent drafts interfaces, DTOs, and configuration shapes.
  - Backend Implementation and Data & Enrichment agents implement the details under those contracts.
  - DevOps Agent wires required configuration and environment variables into runtime.

- **Backend ↔ Data & Enrichment**:
  - Backend Implementation Agent calls enrichment services through clearly defined interfaces.
  - Data & Enrichment Agent provides those interfaces and owns low-level enrichment logic and external data handling.

- **Backend/Enrichment → Progress & UX/API**:
  - Progress & UX/API Agent layers user-friendly endpoints over existing services and background jobs.
  - Progress Agent exposes job state and progress snapshots/SSE streams without reimplementing business logic.

- **All → Documentation**:
  - After major changes, the Documentation Agent updates `README.md`, `AGENTS.md`, or other docs to match new behavior.

### Branching, PRs, and commit conventions

- **Branch names**:
  - Prefer branches that mirror Linear `gitBranchName` patterns, for example:
    - `jeffreyvdbrink/brink-28-implement-scannerservice-background-service`
    - `jeffreyvdbrink/brink-36-implement-enrichmentservice-orchestrator-background-service`
  - If a different naming scheme is needed, still include the `BRINK-XX` id in the branch name wherever possible.

- **Commits and PRs**:
  - Reference relevant Linear issues in commit messages and PR descriptions (e.g., `BRINK-36: add enrichment orchestrator skeleton`).
  - Write concise PR bodies that:
    - Summarize the change.
    - Note any schema/config changes.
    - Link to the Linear issue(s).

---

## Coding & architecture conventions

### General C# / ASP.NET Core style

- **Minimal APIs and top-level statements**:
  - Keep `Program.cs` minimal and focused on composition: service registration, middleware, endpoint mapping.
  - Use extension methods for cross-cutting concerns (service defaults, health checks, OpenAPI/Scalar).

- **Dependency injection**:
  - Prefer constructor injection for services and DbContexts.
  - Use interfaces (`IIndexService`, `IFileScanner`, enrichment service interfaces) to decouple implementations.
  - Register background services via `AddHostedService<T>()`.

- **DTOs and records**:
  - Use records for small, immutable data carriers (`IndexProgress`, `IndexResult`, `ScanRequest`).
  - Use clear, descriptive names that map closely to domain concepts.

### Persistence and EF Core

- **Entities and DbContext**:
  - Place entities under `MusicHoarder.Api.Persistence`.
  - Keep `MusicHoarderDbContext` the single source of truth for EF configuration and migrations.
  - Prefer soft-delete via flags (e.g., `IsDeleted`, `DeletedAt`) for file-based entities like `SongMetadata`.

- **Migrations**:
  - For schema changes, add EF Core migrations and ensure they apply cleanly on startup in development (as is already done in `Program.cs`).
  - Avoid manual SQL migrations unless strictly necessary; if used, document them clearly.

- **Indexes and constraints**:
  - Add indexes on frequently queried fields (e.g., `FilePath`, `Fingerprint`, status columns) based on real query patterns.
  - Use uniqueness constraints where appropriate to protect against inconsistent duplicates (e.g., unique `(FilePath)`).

### Background processing

- **Patterns**:
  - Derive long-running workers from `BackgroundService`.
  - Use channels, queues, or other asynchronous primitives to decouple HTTP requests from heavy work.
  - Ensure all long-running operations take a `CancellationToken` and respect it.

- **Concurrency and throughput**:
  - For IO-heavy operations (SMB file access, dataset streaming, external APIs), use bounded concurrency via `SemaphoreSlim` or other mechanisms as outlined in Linear issues.
  - Batch DB writes to reduce transaction overhead (e.g., using batches of `SongMetadata`).

- **Logging and telemetry**:
  - Use structured logging with meaningful event messages and context properties (e.g., `ScanId`, file path, counts).
  - Hook into `ServiceDefaults` observability where possible so logs/metrics/traces flow through the same pipeline.

### Configuration and options

- **Configuration binding**:
  - Bind configuration to strongly-typed options classes (e.g., `MusicHoarderOptions`) sourced from `appsettings.json` and environment variables.
  - Include settings like:
    - Source/destination/temporary directories.
    - Concurrency limits for SMB and fingerprinting.
    - External API keys and endpoints (AcoustID, MusicBrainz, Spotify).

- **No hardcoded secrets or paths**:
  - Do not hardcode NAS paths, API keys, or secret tokens.
  - Use injected options and environment variables, and document expected configuration in `README.md`.

---

## Safety, environment, and data handling

### NAS and filesystem safety

- **Default to safe paths**:
  - In development, point scanners and builders at test directories or local subsets of the library.
  - Only operate on real NAS shares when explicitly configured to do so by the user.

- **Non-destructive behavior**:
  - The builder must never modify source files; it should only read from source and write new copies to the destination.
  - Use soft-delete semantics for DB records representing removed/missing files; avoid physical deletion unless explicitly requested.

### Secrets and external services

- **API keys and credentials**:
  - Store keys for AcoustID, MusicBrainz, Spotify, and trackers in configuration/environment variables.
  - Do not log secrets or full URLs containing query parameters with keys.

- **Rate limiting and politeness**:
  - When scraping trackers or calling APIs, respect rate limits and specify user agents as appropriate.
  - Implement retries with backoff for transient failures and stop when limits or error thresholds are reached.

### Long-running jobs

- **Separation from request pipeline**:
  - Trigger scan/enrich/build via short-lived HTTP endpoints that enqueue work or flip job state, not via long-running blocking requests.
  - Report progress via SSE endpoints or polling-friendly status endpoints.

- **Resilience**:
  - Handle partial failures gracefully: skip bad files, mark them as failed, and continue.
  - Ensure jobs can be cancelled and restarted without leaving the system in an inconsistent state.

---

## Using Linear with MusicHoarder

- **Reading vs writing**:
  - Agents may read Linear issues freely to understand requirements and constraints.
  - Creating or updating Linear issues should only happen when explicitly requested by the user.

- **Linking code to issues**:
  - Include the `BRINK-XX` id:
    - In branch names.
    - In commit messages (when asked to commit).
    - In PR titles or descriptions.
  - Optionally, reference issue IDs in code comments when it significantly aids understanding (e.g., marking a complex enrichment heuristic).

- **Milestones and prioritization**:
  - By default, prioritize work that advances earlier milestones (M1 and M2) before later ones (M3–M5), unless the user directs otherwise.
  - Use milestone context to choose appropriate abstractions (e.g., building enrichment services with future builder/controller needs in mind).

---

## Future frontend and integrations

- **Frontend expectations**:
  - A SvelteKit (or similar) frontend is planned to:
    - Show scan/enrichment/build progress.
    - Provide manual review UIs for low-confidence matches.
    - Browse and filter the library.
  - Ensure APIs are:
    - Consistent in URL shape and response structure.
    - Paginated and filterable where lists can be large.
    - Stable enough to support a long-lived frontend.

- **Extensibility**:
  - Design services and controllers so new enrichment sources, output formats, or UI features can be added without major breaking changes.
  - Prefer explicit enums and status fields over magic strings.

---

## End-to-end pipeline diagram

Below is the high-level pipeline that all agents should keep in mind when making changes:

```mermaid
flowchart LR
sourceNas[Source_NAS] --> scannerService[ScannerService]
scannerService --> fpcalcService[FpcalcService]
fpcalcService --> enrichmentService[EnrichmentService]
enrichmentService --> duplicateDetection[DuplicateDetection]
duplicateDetection --> manualReview[TrackController_ManualReview]
manualReview --> libraryBuilder[LibraryBuilderService]
libraryBuilder --> destinationNas[Destination_NAS]
```

Each service above may itself use multiple sub-services (AcoustID, MusicBrainz, Spotify dataset, tracker scrapers), but the overall flow remains the same. When introducing new functionality, decide where it belongs in this pipeline and ensure it integrates cleanly with upstream and downstream steps.

---

## Cursor Cloud specific instructions

### System dependencies

The VM snapshot pre-installs .NET 10 SDK, Docker (with fuse-overlayfs for DinD), Node.js 22, pnpm, and `fpcalc` (from `libchromaprint-tools`). The update script handles `dotnet restore` and `pnpm install` on startup.

### Running the full stack

The recommended way to run everything in development is via the Aspire AppHost:

```bash
mkdir -p /tmp/musichoarder-source /tmp/musichoarder-dest /tmp/musicenricher
dotnet user-secrets set "MusicEnricher:SourceDirectory" "/tmp/musichoarder-source" --project MusicHoarder.Api
dotnet user-secrets set "MusicEnricher:DestinationDirectory" "/tmp/musichoarder-dest" --project MusicHoarder.Api
dotnet run --project MusicHoarder.AppHost
```

This starts the Aspire dashboard (https://localhost:17072), provisions PostgreSQL as a Docker container, launches `MusicHoarder.Api`, and attempts to start the frontend via npm. EF Core migrations auto-apply in development.

**Gotcha — frontend via AppHost**: The AppHost uses `.WithNpm()` but the frontend only has a `pnpm-lock.yaml` (no `package-lock.json`). To run the frontend reliably, start it separately:

```bash
cd frontend && MUSICHOARDER_API_URL=http://localhost:<api-port> PORT=3000 pnpm dev
```

The API port is dynamically assigned by Aspire — find it in the Aspire dashboard or via `netstat -tlnp | grep MusicHoarder`.

### Required user-secrets

`MusicEnricher:SourceDirectory` and `MusicEnricher:DestinationDirectory` are validated on startup. Set them via `dotnet user-secrets` (see above) to point at local test directories.

### Pipeline dependencies (fpcalc + AcoustID)

`fpcalc` (Chromaprint) must be installed for the scan→enrich→library-build pipeline to work end to end. Without it, songs get indexed but with null `Fingerprint` and `DurationSeconds`, which means:
- **Enrichment** skips them (filters for non-null fingerprint + duration)
- **Library Builder** skips them (requires `EnrichmentStatus == Matched`)
- **Library page "Destination" view** shows nothing (requires `destinationPath` set by the builder)

The `MusicEnricher:AcoustIdApiKey` must be set for enrichment to match songs against MusicBrainz via AcoustID. It is injected as the environment variable `MusicEnricher__AcoustIdApiKey`. Without it, enrichment sets songs to `NeedsReview`.

The library page has two modes: **Source** (shows all scanned songs) and **Destination** (only songs that completed the full pipeline). Toggle between them on the Library page toolbar.

### Running tests

```bash
dotnet test MusicHoarder.Api.Tests/MusicHoarder.Api.Tests.csproj
```

All 19 xUnit tests use an in-memory EF Core provider and do not require PostgreSQL or Docker.

### Frontend lint

The `pnpm lint` script in `frontend/` references `eslint` and `eslint-config-next`, which are not listed as dependencies in `package.json`. This is a known gap — `pnpm lint` will fail until those are added. The frontend builds cleanly with `pnpm build`.

### Docker requirement

Docker must be running before starting the AppHost, since Aspire provisions PostgreSQL as a container. In the Cloud VM, Docker is started via `sudo dockerd` and the socket is made accessible via `sudo chmod 666 /var/run/docker.sock`.

