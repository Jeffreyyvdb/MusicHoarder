# Build, CI/CD & release engineering

How MusicHoarder is built, tested, versioned, published and deployed. For the
operator-side picture (running and upgrading an instance), see
[Runbook](runbook.md) and [Self-hosting](SELF_HOSTING.md).

## The mental model

Two facts explain most of this repo's pipeline design:

1. **One AppHost generates every deploy shape.** `MusicHoarder.AppHost/AppHost.cs`
   is the single source of truth for the stack's topology (Postgres, API,
   frontend, optional sidecar). Locally it *runs* the stack with the Aspire
   dashboard; at publish time (`aspire publish`, gated by the `DEPLOY_TARGET`
   env var) it *emits* a Docker Compose file — a Swarm/prod shape for Dokploy
   (`MusicHoarder.AppHost/aspire-output/`), a per-PR preview shape
   (`MusicHoarder.AppHost/aspire-output-preview/`), and the published self-host
   template (root `docker-compose.yml`). All three composes are **committed,
   generated artifacts**.
2. **Deploying and releasing are separate.** Every push to `main` builds
   `:edge` images and redeploys the maintainer's Dokploy instance
   ([`aspire-deploy.yml`](../.github/workflows/aspire-deploy.yml)). Releases —
   the `vX.Y.Z` tag, GitHub Release, and the `:X.Y.Z`/`:X.Y`/`:X`/`:latest`
   image tags self-hosters pull — are cut **weekly** (Mondays 07:00 UTC) by
   [`release.yml`](../.github/workflows/release.yml) running
   [semantic-release](https://semantic-release.org) over Conventional Commits.

```
Push to main
     │
ci.yml             dotnet build+test, frontend lint/check/test (also on PRs)
aspire-deploy.yml  builds images → :edge and :sha-<commit>, redeploys Dokploy

Mondays 07:00 UTC (or Actions → Release → Run workflow)
     │
release.yml        tests, then semantic-release cuts one vX.Y.Z tag + GitHub
                   Release and dispatches ↓
aspire-deploy.yml  rebuilds that tag's images → :X.Y.Z, :X.Y, :X, :latest
                   (no redeploy — the instance already runs that commit)
release.yml also   ships the Android APK/AAB → Play and dispatches iOS TestFlight
```

Version bumps follow commit prefixes: `fix:` → patch, `feat:` → minor,
`feat!:` / `BREAKING CHANGE:` → major; `chore`/`docs`/`refactor`/`test`/`style`
cut nothing. Commit messages must follow
[Conventional Commits](https://www.conventionalcommits.org/) — see
`CONTRIBUTING.md`. The [Releases page](https://github.com/Jeffreyyvdb/MusicHoarder/releases)
is the canonical changelog.

## Local development with Aspire

Prerequisites: .NET SDK (pinned in `global.json` to 11.0.100), Bun (frontend),
and **Docker running** — Aspire provisions PostgreSQL as a container.

```bash
dotnet run --project MusicHoarder.AppHost
```

This starts:

- **Postgres 17.6** — pinned via `.WithImageTag("17.6")` in
  `MusicHoarder.AppHost/AppHost.cs`, with `ContainerLifetime.Persistent` and a
  named data volume, so the container survives AppHost restarts. The pin is
  deliberate: the default tag has floated to 18.x (which also moved the data
  directory), and an unpinned regen would silently propose a major-version
  upgrade of a live database.
- **The API** (`MusicHoarder.Api`) — `WaitFor(postgresdb)`, and EF migrations
  apply automatically on boot.
- **The frontend** (`AddViteApp("frontend", "../frontend").WithBun()`), served
  over HTTPS with the Aspire developer certificate. The internal Node→ASP.NET
  proxy hop (`MUSICHOARDER_API_URL`) stays HTTP by design, sidestepping
  cross-runtime dev-cert trust.
- **The Aspire dashboard** at `https://localhost:17072` — logs, traces, and the
  first-run prompts for missing parameters. (The dashboard is a dev-only
  feature: `AddDockerComposeEnvironment` sets `DashboardEnabled = false`, so no
  deploy shape ships it or the OTLP wiring that comes with it.)

### Configuration as AppHost parameters

Every secret and path is an `AddParameter(...)` in `MusicHoarder.AppHost/AppHost.cs`,
injected into the API as environment variables (`MusicEnricher__*`, `Spotify__*`,
`Auth__*`, …). Required values (`source-directory`, `destination-directory`,
`frontend-public-base-url`) are prompted in the dashboard on first run; optional
provider credentials are `.AsOptional()` and degrade the corresponding feature
gracefully when blank (no AcoustID key → most tracks land in review; no
transcription key → the AI lyrics feature is hidden).

To pre-seed and avoid re-entering values, use the AppHost's user-secrets store
(note the `Parameters:` prefix):

```bash
dotnet user-secrets set "Parameters:source-directory" "/tmp/mh-source" --project MusicHoarder.AppHost
dotnet user-secrets set "Parameters:destination-directory" "/tmp/mh-dest" --project MusicHoarder.AppHost
dotnet user-secrets set "Parameters:acoustid-api-key" "..." --project MusicHoarder.AppHost
dotnet user-secrets set "Parameters:spotify-client-id" "..." --project MusicHoarder.AppHost
dotnet user-secrets set "Parameters:spotify-client-secret" "..." --project MusicHoarder.AppHost
```

Secrets live only in user-secrets or environment — the committed
`appsettings*.json` keep credential fields empty (a repo rule; see `CLAUDE.md`).
The frontend runs standalone with `bun run dev` in `frontend/`, pointing at the
API port shown in the dashboard. A pre-push git hook (`.githooks/pre-push`,
installed by `bun install` in `frontend/`) formats/lints/typechecks changed
frontend files before CI does.

## Docker setup

Four images, published to GHCR under `ghcr.io/jeffreyyvdb/musichoarder/…`:

| Image | Dockerfile | Notes |
|---|---|---|
| `api` | `Dockerfile` | Multi-stage .NET build + `aspnet:11.0` runtime. Bakes in `fpcalc` (`libchromaprint-tools`), `ffmpeg`, `yt-dlp` and `deno` (yt-dlp's JS runtime for YouTube's player challenge). Entrypoint is `docker/api-entrypoint.sh`, health-checked at `/alive`. |
| `api-base` | `Dockerfile.api-base` | The runtime-deps layer only. Aspire builds the API image through the .NET SDK container target (`dotnet publish -t:PublishContainer`), which cannot `apt-get`, so the deps are baked into this base and referenced via `<ContainerBaseImage>`. CI rebuilds and pushes it (`:latest`) before `aspire do push`. |
| `frontend` | `frontend/Dockerfile` | SvelteKit + Bun + adapter-node; runs as a non-root `svelte` user, health-checked at `/api/health`. |
| `spotiflac` | `sidecars/spotiflac/Dockerfile` | Opt-in lossless-FLAC sidecar (see [Sidecars](#sidecars-spotiflac)); versioned on its own scheme. |

The pinned tool versions (`ARG YTDLP_VERSION` / `DENO_VERSION` + SHA-256
checksums, verified at build) are **kept identical in both API Dockerfiles** by
convention; `tool-pins.yml` opens PRs when newer releases exist.

### Compose files

- **`docker-compose.yml`** — the published self-host template, generated by the
  AppHost (`DEPLOY_TARGET=selfhost aspire publish`). Pulls prebuilt GHCR images
  (`${MUSICHOARDER_VERSION:-latest}`), runs Postgres 17.6, api, frontend, and a
  `spotiflac` service behind the Compose profile `spotiflac`. All configuration
  is `${…}`-placeholder env vars documented in `.env.example` and
  [SELF_HOSTING.md](SELF_HOSTING.md).
- **`docker-compose.build.yml`** — optional layer to build the images from a
  checkout instead of pulling:
  `docker compose -f docker-compose.yml -f docker-compose.build.yml up -d --build`.
- **`MusicHoarder.AppHost/aspire-output/docker-compose.yaml`** and
  `aspire-output-preview/…` — the committed prod (Swarm, with zero-downtime
  `deploy.update_config` and Traefik `lbswarm` labels) and preview shapes.

`ComposeFileExtensions.cs` shapes each target: the prod shape sends OTLP to the
host's collector, the self-host shape drops the OAuth-relay keys and adds
`PUID`/`PGID`, and the preview shape adds demo-seed and WebAuthn settings.

### Compose drift guard

Because all three composes are committed rather than regenerated at deploy
time, an AppHost change that adds an env mapping can silently miss a target —
the failure mode that shipped AI lyrics transcription invisible in prod.
[`scripts/check-aspire-compose-drift.sh`](../scripts/check-aspire-compose-drift.sh),
run as the first `ci.yml` job, does a presence check: every env key declared in
`AppHost.cs` / `ComposeFileExtensions.cs` must appear in each generated compose
that should carry it (with the per-target shapers' intentional exceptions
listed in the script). On failure, regenerate and commit:

```bash
aspire publish -o MusicHoarder.AppHost/aspire-output --non-interactive          # swarm/prod
DEPLOY_TARGET=compose  aspire publish -o MusicHoarder.AppHost/aspire-output-preview --non-interactive
DEPLOY_TARGET=selfhost aspire publish -o /tmp/sh && cp /tmp/sh/docker-compose.yaml docker-compose.yml
```

## CI/CD workflows (`.github/workflows/`)

| Workflow | Trigger | What it does |
|---|---|---|
| [`ci.yml`](../.github/workflows/ci.yml) | push/PR to `main` | Three jobs: **compose-drift** (the bash guard above), **dotnet** (restore/build/test against a Postgres 17.6 service container via `MUSICHOARDER_TEST_POSTGRES`, plus a compile of `perf/seed` so perf tooling never rots), **frontend** (`scripts/ci-checks.sh` — lint, typecheck, test, build). Only `dotnet` and `frontend` are required status checks on `main`, which is what makes path-filtered workflows safe to skip. Every job is wrapped by the composite action [`.github/actions/budget`](../.github/actions/budget), which fails a job that exceeds its declared wall-clock budget so CI slowdowns are fixed in the change that caused them. |
| [`aspire-deploy.yml`](../.github/workflows/aspire-deploy.yml) | push to `main` (path-ignored: docs, mobile, perf, `*.md`) or dispatch | Builds the API + frontend images on native amd64 runners via `aspire do push`, tags them `:edge` + `:sha-<commit>` (edge) or `:X.Y.Z`/`:X.Y`/`:X`/`:latest` (release), rebuilds `api-base`, builds the spotiflac sidecar keyed by its content (`:tree-<hash>` — no rebuild when the folder didn't change), and for edge builds fires the Dokploy webhook and **waits until the instance actually serves the new version** (`/api/mh/api/version` + the frontend's `/_app/version.json`), retrying once. |
| [`release.yml`](../.github/workflows/release.yml) | weekly cron (Mon 07:00 UTC) or manual | Gates on the frontend checks + `dotnet test`, then runs semantic-release (under Node 24 — semantic-release v25+ doesn't run under Bun's Node-compat layer), dispatches `aspire-deploy.yml` with the version via `workflow_dispatch` (the one event `GITHUB_TOKEN` may trigger — no PAT), then ships Android (best-effort, never blocks the release) and dispatches iOS TestFlight. A manual run with `android_version: X.Y.Z` re-publishes only the Android app for an existing release. |
| [`mobile.yml`](../.github/workflows/mobile.yml) | push/PR touching `mobile/**` | iOS simulator build + Android debug APK; no signing, no secrets. Path-filtered, and therefore deliberately **not** a required status check (a skipped run must not leave a PR waiting). Also asserts the Home-Screen widget extension is embedded with the right bundle id / App Group. |
| [`ios-testflight.yml`](../.github/workflows/ios-testflight.yml) | dispatch (by `release.yml` or manual) | Signs and uploads the iOS app to TestFlight using App Store Connect cloud-managed signing (archive unsigned; only the export step signs, so runners never mint new dev certificates). Reports-and-stops green without its secrets. |
| [`pr-preview.yml`](../.github/workflows/pr-preview.yml) + [`pr-preview-cleanup.yml`](../.github/workflows/pr-preview-cleanup.yml) + [`scripts/dokploy-preview.sh`](../scripts/dokploy-preview.sh) | PR open/sync/close | Per-PR full stacks on Dokploy at `https://pr-<n>.<PREVIEW_BASE_DOMAIN>` (Dokploy's native preview feature doesn't support Compose apps). Runs only for same-repo branches — fork PRs are skipped, since this is a public repo and untrusted code is never built/run on the server. Runs `perf/src/web/preview-checks.ts` against each deployed preview (security headers/CSP, passkey autofill, forced-colors focus, app pages as the demo account). |
| [`tool-pins.yml`](../.github/workflows/tool-pins.yml) | daily | Bumps the yt-dlp/deno `ARG` pins in both API Dockerfiles (Dependabot can't — these are URL-downloaded binaries, not `FROM` images), respecting a 3-day release cooldown. yt-dlp must stay fresh because YouTube changes break old versions. |
| [`dependabot.yml`](../.github/dependabot.yml) + [`dependabot-auto-merge.yml`](../.github/workflows/dependabot-auto-merge.yml) | weekly | Bun, github-actions and nuget ecosystems, all with supply-chain **cooldowns** (a new version must be out for a few days before it's proposed). Patch+minor bumps are grouped per dependency-type; majors stay individual. Only bun patch/minor PRs auto-merge. Notable pinned exceptions with comments: `@sveltejs/adapter-node@5.5.5` (broken release), `conventional-changelog-conventionalcommits` v10 (empties release bodies), TypeScript 7 (breaks svelte-check). |
| [`claude.yml`](../.github/workflows/claude.yml), [`looping.yml`](../.github/workflows/looping.yml) | various | AI-assistant/issue-triage plumbing; out of scope here. |

### Semantic-release specifics

- One version line for the whole repo (API **and** frontend); `frontend/package.json`'s
  `version` field is intentionally stale — the canonical version is the latest
  `v*` git tag.
- `@semantic-release/git` and `@semantic-release/changelog` are not used (per
  the maintainers' recommendation); no release commit is pushed back to `main`,
  so branch-protection required checks need no bypass actor. Tags/Releases are
  created through the GitHub Releases API.
- Local dry run from `frontend/`: `bun run release:dry` (needs Node ≥ 22.14 on
  PATH).

### The edge version string

Edge builds bake `<last release>-edge.g<sha>` into the API assembly
(`MH_VERSION` → `<Version>` in `MusicHoarder.Api/MusicHoarder.Api.csproj`). The
`g` prefix keeps an all-digit short SHA with a leading zero from being an
invalid SemVer identifier (which would fail NuGet restore). Because a release
is often cut on a commit already running as `<prev>-edge`, the in-app update
check asks GitHub's compare API whether the running commit already holds the
latest release instead of trusting the version number.

### Zero-downtime deploys

The prod compose declares Swarm `deploy.update_config` (`order: start-first`)
plus Docker healthchecks, and `traefik.swarm.lbswarm=true` on both app
services (without it Traefik re-reads task IPs only every 15 s, causing brief
502s per deploy). Getting zero-downtime requires running the stack as a Docker
Stack (in Dokploy: Compose Type = "Docker Stack"), and the stack deploy must
use `--resolve-image always` since Swarm ignores `pull_policy`. Under plain
`docker compose up` these keys are inert — self-host keeps stop-then-start.
Because GHCR sends no push webhooks, the deploy webhook is fired by the Action
*after* the images are pushed (not by Dokploy's own auto-deploy, which fires
before the build).

## Self-hosting

Self-hosting is documented in [SELF_HOSTING.md](SELF_HOSTING.md): the one-file
zero-config trial, the `.env` core settings, reverse-proxy placement, optional
integrations (AcoustID, Spotify, slskd, spotiflac, Umami, OTel, instance sync),
updating and pinning, and backups. Nothing here repeats it.

## Performance tooling (`perf/`)

A Bun + Playwright suite that measures rather than gates:

- **`perf/seed`** — a .NET console app that fills a throwaway Postgres through
  the API's own EF model. Not in the solution file, but `ci.yml` compiles it so
  a breaking change fails the PR.
- **`bun perf all`** (`perf/src/cli.ts`) — starts a local stack with a
  generated library, times every endpoint in `perf/src/api/endpoints.ts`
  (median/p95 latency, payload size, SQL statements per request via
  `pg_stat_statements`, CPU, ×8-concurrency p95) and the web app's cold starts
  and interactions in headless Chromium (browser scenarios run under Node —
  Playwright is unreliable under Bun), then writes a report to
  `perf/results/<time>_<label>/` that `bun perf compare` diffs against another
  run.
- **Machine safety** — a stack takes a few GB, so one runs per machine: `up`
  and `all` take a machine-wide lock (`perf/src/lock.ts`), refuse to start when
  free memory is below the measured footprint, and keep the lock until `down`.
  Parallel sessions on one machine set their own `PERF_WORK_DIR` and `PERF_*_PORT`s;
  `--scope` runs the stack in systemd scopes with memory limits so an OOM kill
  takes the stack, not the session.
- **`perf/src/web/preview-checks.ts`** — not a measurement but a web-platform
  conformance check run against every PR preview (and usable against prod).

See `perf/README.md` for the full column reference.

## Scripts and tools

| Path | Purpose |
|---|---|
| `scripts/check-aspire-compose-drift.sh` | CI guard that the three committed generated composes carry every AppHost-declared env key (see above). |
| `scripts/dokploy-preview.sh` | Provisions/destroys/reaps per-PR preview stacks on Dokploy via its REST API, cleaning up the isolated networks Dokploy leaves behind. Used by `pr-preview.yml`. |
| `tools/yetracker-import/yetracker_import.py` | Offline one-off normalizer for the scraped "yetracker" community-tracker Google-Sheets CSV export into the committed `MusicHoarder.Api/Data/yetracker.json` catalog. Drops download links and free-text notes; exits non-zero if a sheet present on disk yields no songs (headers drift, and a silent empty sheet would truncate the committed catalog). |

## Sidecars: `sidecars/spotiflac`

A thin FastAPI wrapper around the `SpotiFLAC` PyPI module that the API's
`spotiflac` download provider calls over HTTP for true-lossless FLAC.

- **Opt-in and legally grey**: it relays through third-party servers, which is
  against streaming services' terms; it is a separate container, never baked
  into the published images, and off unless enabled (`COMPOSE_PROFILES=spotiflac`
  + `SPOTIFLAC_SIDECAR_URL=http://spotiflac:8000` in the self-host compose; see
  `sidecars/spotiflac/README.md`).
- **Contract** (matches `MusicHoarder.Api`'s `StreamingFlacSidecarClient.cs`):
  `GET /health` and `POST /acquire` (`{spotify_url, quality, services,
  output_dir, filename_stem, timeout_s, isrc}` → `{"status":"ok","file":…}`).
  The staging directory is bind-mounted into both containers at the **same
  absolute path**, so returned file paths resolve on both sides.
- **Build/CI**: built by the `sidecar` job in `aspire-deploy.yml`, keyed by the
  git tree hash of `sidecars/spotiflac` (`:tree-<hash>`): merges that don't
  touch the folder push nothing, and because the key is content-based, a queued
  edge run that got cancelled cannot lose a change. Tags: `:latest` follows
  main; a release adds `:X.Y.Z` so `SPOTIFLAC_VERSION` can pin one.

## Related reading

- [Runbook](runbook.md) — operating an instance day to day.
- [SELF_HOSTING.md](SELF_HOSTING.md) — the self-host configuration reference.
- `CLAUDE.md` (root) — the full repo conventions doc (also readable as `AGENTS.md`).
- `.github/workflows/` — every workflow is heavily comment-headered; treat the
  headers as the authoritative rationale.
