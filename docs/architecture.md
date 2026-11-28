# Architecture overview

MusicHoarder is a self-hosted application that scans a large, disorganized music collection,
identifies each track by its actual audio, enriches it with metadata from multiple providers, and
builds a clean, consistently-organized copy — without ever touching the originals. It is also a
full streaming player (web + iOS/Android) over the cleaned library.

This document is the entry point to the architecture documentation. Each area has its own document:

| Document | Covers |
|----------|--------|
| [backend.md](backend.md) | The ASP.NET Core API: endpoints, auth, background work, observability |
| [data-model.md](data-model.md) | EF Core entities, PostgreSQL, migrations, file storage layout |
| [integrations.md](integrations.md) | External providers (AcoustID, MusicBrainz, Spotify, Apple Music, Deezer, Soulseek, Navidrome) and the scan → match → build pipeline |
| [frontend.md](frontend.md) | The SvelteKit web app and the Capacitor mobile shell |
| [deployment.md](deployment.md) | Docker images, CI/CD, semantic-release, zero-downtime deploys |
| [runbook.md](runbook.md) | Operational procedures: run, upgrade, back up, troubleshoot |
| [adr/](adr/README.md) | Architecture decision records — the "why" behind the code |
| [SELF_HOSTING.md](SELF_HOSTING.md) | Self-hosting guide for operators (env reference, backups, optional integrations) |

## System context

A single deployment is one API process, one web frontend, one PostgreSQL database, and the
operator's music storage. Your originals never leave the machine: the source library is read-only
and stays on hardware the operator controls, and the cleaned destination library is written next to
it. The app does serve audio over HTTP — the streaming endpoint plays the destination library to
the operator's own browsers, and share links and invited members stream it remotely — so put a
deployment you care about behind TLS and share deliberately.

```
                       ┌────────────────────────────────────────────┐
                       │                Operator's NAS / disk       │
                       │  source library (read-only)  destination   │
                       └───────┬──────────────────────────┬─────────┘
                               │ scan                     │ build (copy + tag)
┌──────────────┐   HTTP   ┌────┴──────────────────────────┴─────────┐
│ SvelteKit    │◄────────►│            MusicHoarder.Api             │
│ frontend     │          │        (ASP.NET Core minimal API)       │
│ (web + mobile│          │  endpoints · auth · background pipeline │
│  shell)      │          └────┬────────────────────────┬───────────┘
└──────────────┘               │ EF Core                │ optional integrations
                        ┌──────┴──────┐    ┌────────────┴────────────────────┐
                        │ PostgreSQL  │    │ AcoustID · MusicBrainz · Spotify│
                        └─────────────┘    │ Apple Music · Deezer · Soulseek │
                                           │ Navidrome · LRCLIB · LLM graders│
                                           └─────────────────────────────────┘
```

## Components

| Project | What it is |
|---------|------------|
| `MusicHoarder.Api` | ASP.NET Core minimal API. Hosts the HTTP endpoints, EF Core/PostgreSQL persistence, and the background services that run the pipeline, enrichment providers, sync and downloads. See [backend.md](backend.md). |
| `MusicHoarder.AppHost` | .NET Aspire AppHost. Composes API + frontend + PostgreSQL for local development and generates the production docker-compose model. See [deployment.md](deployment.md). |
| `MusicHoarder.ServiceDefaults` | Shared cross-cutting defaults: health checks, OpenTelemetry, resilient HTTP. |
| `MusicHoarder.Api.Tests` | xUnit test suite running on the in-memory EF provider — no PostgreSQL or Docker required. |
| `frontend/` | SvelteKit (Svelte 5) + Bun web app: library browser, player with Now Playing and synced lyrics, pipeline dashboard, review Inbox, Discover, Stats, History, share pages. See [frontend.md](frontend.md). |
| `mobile/` | Capacitor shell for iOS and Android wrapping the same web pages, adding a native audio player so playback survives a locked phone. See [frontend.md](frontend.md). |
| `sidecars/spotiflac/` | Optional streaming-FLAC sidecar used by the download chain. |
| `perf/` | Load/seeding tooling for performance work. |

## The pipeline

The heart of the system is a state machine over each song, run by background workers that each
sweep for work in the status they handle (see [backend.md](backend.md) for the worker machinery and
[integrations.md](integrations.md) for each provider):

```
Source library
   → Scan          index files (incl. SMB/NAS) and read embedded tags
   → Fingerprint   compute an audio fingerprint (fpcalc/Chromaprint)
   → Match         identify via AcoustID, then match against MusicBrainz,
                   Spotify, Apple Music, Deezer + community trackers
   → Decide        a consensus evaluator derives the verdict from all providers
   → AI grade      (optional) an LLM scores the match/metadata quality
   → Dedupe        detect duplicate recordings, keep the best copy
   → Build         copy + tag + organize into the destination library
```

Two invariants shape everything around it:

1. **The source library is never modified.** Clean copies are written to a separate destination;
   matching never blindly overwrites good tags — disputed values are recorded as *proposed* changes.
2. **Uncertainty goes to a human.** Confident matches flow straight through; low-confidence matches
   and LLM-flagged items surface in the **Inbox** review queues.

## Cross-cutting decisions

The rationale for the choices below is recorded in the ADRs (see [adr/README.md](adr/README.md)):

- **.NET Aspire for orchestration and deployment artifacts** — one command boots API + frontend +
  PostgreSQL with health checks, dashboards and automatic migrations, and `aspire publish` emits the
  production/preview/self-host compose files from the same graph
  ([ADR-001](adr/ADR-001.md)).
- **PostgreSQL via EF Core; in-memory provider for tests** — the test suite needs no database or
  Docker, keeping `dotnet test` fast and hermetic; real-Postgres semantics get opt-in coverage
  ([ADR-002](adr/ADR-002.md)).
- **Background workers as status sweeps** — the pipeline is a per-song `SongMetadata` state machine;
  ~20 independent `BackgroundService`s each sweep for songs in the status they own, which makes
  stages independently restartable and observable ([ADR-003](adr/ADR-003.md)).
- **Observability derived at read time** — the History feed, background-work panel and provenance
  views are computed from rows the pipeline already writes; the only append-only log is the
  library-write (tag diff) event log ([ADR-004](adr/ADR-004.md)).
- **Consensus-based matching** — no single provider is trusted; a consensus evaluator combines up to
  six independent matchers and requires agreement (≥2 providers) before auto-matching.
- **One semantic-release version for the whole repo** — Conventional Commits drive a single `vX.Y.Z`
  across API, frontend and mobile; releases are cut weekly while `main` deploys continuously as
  `:edge` images ([ADR-005](adr/ADR-005.md)).
- **One client codebase** — the SvelteKit app served by adapter-node in Docker is the only client; a
  thin Capacitor shell adds a native audio player for locked-phone playback instead of maintaining a
  second app ([ADR-006](adr/ADR-006.md)).

## Key data flows

- **Listening**: frontend (or native shell) → API streaming endpoints → destination library files;
  formats the browser cannot play are transcoded as they stream. Likes and play counts are recorded
  server-side and drive the Overview shelves.
- **Multi-tenancy**: every request flows through a fixed middleware chain (RequestId → Authentication
  → RequireAuth → DemoReadOnly → MemberWriteGuard), and data isolation is enforced by EF Core global
  query filters on `OwnerUserId` rather than by SQL in call sites — see [backend.md](backend.md) and
  [data-model.md](data-model.md).
- **Frontend → API**: the browser never talks to the API directly; all calls go through a same-origin
  SvelteKit proxy route, so there is no CORS anywhere and cookies stay first-party — see
  [frontend.md](frontend.md).
- **Enrichment**: scan → fingerprint → parallel provider matching → consensus → (optional LLM
  grade) → human review if uncertain → build into destination. Every applied or proposed tag change
  is recorded in History. See [integrations.md](integrations.md).
- **Growing the library**: Spotify liked songs / playlists, Deezer or YouTube playlist links feed a
  wishlist; an ordered download chain (streaming-FLAC sidecar → Soulseek via slskd → yt-dlp fallback)
  turns wishlist entries into files that re-enter the pipeline. See [integrations.md](integrations.md).
- **Sharing**: revocable public links scoped to a track or album, and invited member accounts whose
  access is grant-based. See [backend.md](backend.md).

## Configuration model

All application options live under the `MusicEnricher` section (`appsettings.json` or
`MusicEnricher__`-prefixed environment variables); optional integrations have their own sections
(`QualityGrading__`, `LyricsTranscription__`, sync/Navidrome/slskd settings). Every optional
integration degrades gracefully when unconfigured — the feature is off, the rest of the app works.
The quickstart and the full option table are in the [README](../README.md#configuration); the
operator-facing reference is in [SELF_HOSTING.md](SELF_HOSTING.md).
