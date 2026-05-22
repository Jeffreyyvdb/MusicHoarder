# MusicHoarder

A .NET Aspire-style backend that scans a large music library (including NAS SMB shares), fingerprints and enriches tracks using external datasets (AcoustID, MusicBrainz, Spotify, community trackers), and builds a clean, organized destination library.

## Architecture

```
Source NAS → ScannerService → FpcalcService → EnrichmentService
          → DuplicateDetection → ManualReview → LibraryBuilderService → Destination NAS
```

## Projects

| Project | Description |
|---------|-------------|
| `MusicHoarder.Api` | ASP.NET Core minimal API — endpoints, EF Core persistence, background services |
| `MusicHoarder.AppHost` | Aspire AppHost — composes the API, frontend, and PostgreSQL |
| `MusicHoarder.ServiceDefaults` | Shared cross-cutting defaults (health checks, OpenTelemetry, resilient HTTP) |
| `frontend` | SvelteKit frontend — library browser, scan/enrich progress, manual review UI |

---

## Local Development

### Prerequisites

- .NET 10 SDK
- Docker (for PostgreSQL via Aspire)
- Bun (frontend toolchain); Node.js 22 only for the semantic-release step
- `fpcalc` (`libchromaprint-tools`) for fingerprinting

### Run with Aspire (recommended)

```bash
mkdir -p /tmp/musichoarder-source /tmp/musichoarder-dest /tmp/musicenricher

dotnet user-secrets set "MusicEnricher:SourceDirectory" "/tmp/musichoarder-source" --project MusicHoarder.Api
dotnet user-secrets set "MusicEnricher:DestinationDirectory" "/tmp/musichoarder-dest" --project MusicHoarder.Api

dotnet run --project MusicHoarder.AppHost
```

This starts the Aspire dashboard at `https://localhost:17072`, provisions PostgreSQL as a Docker container, launches the API, and attempts to start the frontend. EF Core migrations are applied automatically.

### Frontend (standalone)

The AppHost runs the frontend via `.WithBun()`. To start it separately:

```bash
cd frontend && MUSICHOARDER_API_URL=http://localhost:<api-port> PORT=3000 bun run dev
```

Find the API port in the Aspire dashboard or via `netstat -tlnp | grep dotnet`.

### Run tests

```bash
dotnet test MusicHoarder.Api.Tests/MusicHoarder.Api.Tests.csproj
```

All 19 xUnit tests use an in-memory EF Core provider — no PostgreSQL or Docker required.

---

## Configuration

All options live under the `MusicEnricher` section in `appsettings.json` or as environment variables using the `MusicEnricher__` prefix.

| Key | Description | Required |
|-----|-------------|----------|
| `MusicEnricher__SourceDirectory` | Path to the source music library | Yes |
| `MusicEnricher__DestinationDirectory` | Path for the cleaned destination library | Yes |
| `MusicEnricher__AutoStartPipeline` | Auto-run the *processing* cascade (scan→fingerprint→enrich→build, enrichment backfill/retry sweep). Discovery (file indexing) always runs so the library still populates. Set `false` to require manual triggering of the heavy steps — useful in resource-constrained preview environments. | No (default: `true`) |
| `MusicEnricher__TempDirectory` | Scratch space for in-progress work | No (default: `/tmp/musicenricher`) |
| `MusicEnricher__AcoustIdApiKey` | AcoustID API key for fingerprint-to-MusicBrainz lookup | No (enrichment falls back to `NeedsReview` without it) |
| `MusicEnricher__AcoustIdScoreThreshold` | Minimum confidence score to accept a match (0–1) | No (default: `0.85`) |
| `MusicEnricher__SmbConcurrency` | Parallel file reads from SMB | No (default: `8`) |
| `MusicEnricher__EnrichmentWorkerConcurrency` | Parallel AcoustID lookups | No (default: `2`) |
| `ConnectionStrings__musichoarderdb` | PostgreSQL connection string | Yes (injected by Aspire in dev) |

---

## CI/CD — Dokploy

Both the API and the frontend are built and deployed by Dokploy directly from this repository using Dokploy's **Docker** build type (reads the `Dockerfile` at the configured build context). GitHub Actions only runs tests — it no longer builds or publishes container images.

```
Push to main
     ↓
GitHub Actions — run xUnit tests (ci.yml)
     ↓
Dokploy (Hetzner VPS) — git pull → docker build → redeploy
```

### Dokploy applications

Create two applications in Dokploy, both pointing at this repo and the `main` branch:

| App | Build type | Build context | Dockerfile | Notes |
|-----|------------|---------------|------------|-------|
| API | Docker | `/` (repo root) | `Dockerfile` | Exposes port `8080` |
| Frontend | Docker | `frontend/` | `frontend/Dockerfile` | Exposes port `3000` |

Enable Dokploy's GitHub webhook on each app so a push to `main` triggers a rebuild automatically. Runtime env vars (`ConnectionStrings__musichoarderdb`, `MusicEnricher__*`, `MUSICHOARDER_API_URL`, etc.) are configured per-app in Dokploy.

Because the frontend bakes `PUBLIC_*` values at build time, any `PUBLIC_*` value (e.g. `PUBLIC_SITE_URL`) must be set as a **build arg** in Dokploy, not a runtime env var.

### PR preview environments

Every pull request from a branch **in this repo** (fork PRs are skipped — see below) gets its own
isolated, full-stack preview on Dokploy at `https://pr-<n>.<PREVIEW_BASE_DOMAIN>`. Dokploy's native
Preview Deployments only support single Application services, not Compose apps
([dokploy#2028](https://github.com/Dokploy/dokploy/issues/2028)), so `.github/workflows/pr-preview.yml`
rolls its own: it builds `:pr-<n>` images, then `scripts/dokploy-preview.sh` creates an **isolated**
compose stack (own Postgres + API + frontend, namespaced by `appName=mh-pr-<n>`) from
`docker-compose.preview.yaml`. The stack is torn down when the PR closes; a daily
`pr-preview-cleanup.yml` reaps any that slip through.

When a preview is live the workflow attaches a green **`preview`** label to the PR (so the PR list
shows at a glance which PRs have a running environment) and posts a sticky comment with the URL, the
**deployed commit SHA**, and the update time. Every push re-deploys and refreshes that comment — the
short SHA tells you whether the live env matches the latest commit. The label is removed and the
comment updated on teardown (close-event teardown, or the daily reaper as a fallback).

Previews set `MusicEnricher__AutoStartPipeline=false` so the resource-intensive processing never
auto-runs. Discovery still runs, so the library populates on boot; drive the rest manually from the
UI (Fingerprint → Build, or the per-song / per-folder "Enrich" actions) to test exactly what a PR
touches.

Sign in to a preview with the magic link printed in the API logs (Dokploy log viewer) — previews
run with Resend disabled so no email provider is needed. The owner can also sign in with a
**passkey**: previews pin the WebAuthn relying-party id to the shared `<PREVIEW_BASE_DOMAIN>` parent
(not the per-PR host), so one passkey registered against it works on every `pr-<n>` subdomain, and
the `PREVIEW_OWNER_SEED_CREDENTIAL` secret (below) seeds that passkey into each empty preview DB.

**Security:** previews never build or run fork code on the server (this is a public repo). The
workflow guards every job with `github.event.pull_request.head.repo.full_name == github.repository`.

**One-time setup:**

1. **DNS** — add a wildcard `A` record `*.<PREVIEW_BASE_DOMAIN>` pointing at the Dokploy server IP
   (e.g. `*.preview.musichoarder.app`). TLS is issued per-host by Dokploy/Traefik via
   Let's Encrypt.
2. **Dokploy** — create a dedicated **environment** (or project) for previews and note its
   `environmentId` (never use the production environment). Seed a small read-only sample library on
   the server at the path you set as `PREVIEW_SOURCE_DIR` (below) so previews have files to scan.
3. **GitHub Actions secrets:**

   | Secret | Purpose |
   |--------|---------|
   | `DOKPLOY_URL`, `DOKPLOY_API_KEY` | Dokploy REST API (shared with the prod deploy) |
   | `DOKPLOY_PREVIEW_ENVIRONMENT_ID` | environment previews are created in |
   | `PREVIEW_BASE_DOMAIN` | e.g. `preview.musichoarder.app` (also the WebAuthn relying-party id) |
   | `PREVIEW_POSTGRES_PASSWORD` | throwaway Postgres password for previews |
   | `PREVIEW_OWNER_EMAIL` | owner account for magic-link sign-in |
   | `PREVIEW_OWNER_SEED_CREDENTIAL` *(optional)* | minified JSON of the owner's pre-registered passkey, seeded into each preview DB (see below) |
   | `PREVIEW_SPOTIFY_CLIENT_ID`, `PREVIEW_SPOTIFY_CLIENT_SECRET` *(optional)* | Spotify app creds so a preview can complete the OAuth token exchange (see **Spotify OAuth** below). Omit → Spotify connect disabled in previews. |
   | `PREVIEW_SPOTIFY_OAUTH_STATE_KEY` *(optional)* | shared HMAC key signing the OAuth `state`; **must equal** the prod relay's `spotify-oauth-state-key`. |
   | `GHCR_CLEANUP_TOKEN` *(optional)* | PAT with `delete:packages` to prune `:pr-<n>` images |

   Repo variable: `PREVIEW_SPOTIFY_OAUTH_RELAY_URL` *(optional)* — the single registered relay URL, `https://<prod-frontend>/api/spotify/relay`.

   **Capturing `PREVIEW_OWNER_SEED_CREDENTIAL` (one-time):** spin up any preview (the relying-party
   id is already pinned to `<PREVIEW_BASE_DOMAIN>`), sign in via magic link as the owner, and
   register a passkey in **Settings**. Then read that one row from the preview's Postgres and store
   it as the secret — a single minified JSON line (public-key material only; the private key never
   leaves your authenticator), e.g.:

   ```json
   {"credentialId":"<base64>","publicKey":"<base64>","aaGuid":"00000000-0000-0000-0000-000000000000","signCount":0,"transports":"internal,hybrid","displayName":"My passkey"}
   ```

   `credentialId` and `publicKey` are the `WebAuthnCredentials` row's `bytea` columns base64-encoded.
   Once the secret is set, every future preview seeds it on boot, so the owner gets passkey login on
   a fresh DB with no re-registration.

**Validating it:** once the secrets are set, push a commit to any same-repo PR — the `pull_request`
trigger builds the images and provisions the stack, then comments the URL. (The `workflow_dispatch`
"Run workflow" button only appears **after** this workflow is on the default branch — GitHub doesn't
expose manual dispatch for a workflow that only exists on a feature branch — so for pre-merge testing
use the `pull_request` trigger; `workflow_dispatch` is for manual re-previews afterwards.) Closing the
PR runs the teardown job.

Tunables: `PREVIEW_MAX_STACKS` (default 5, env in the provision step); `PREVIEW_SOURCE_DIR` is a
**repo variable** (Settings → Variables) for the host path of the shared read-only sample library
bind-mounted into each preview (defaults to `/srv/mh-preview/sample-source` if unset). The per-PR
built library lives in a managed `musichoarder-dest` named volume (isolated per stack), so it is
reaped automatically by the teardown's `compose.delete … deleteVolumes:true` — nothing accumulates
on the host disk.

### Spotify OAuth (relay)

Spotify requires every `redirect_uri` to be pre-registered exactly — no wildcards, no `localhost`,
HTTPS-only except `http://127.0.0.1` loopback. Since local dev (dynamic port) and each PR preview
(dynamic subdomain) have different origins, none can be registered individually. Instead **one**
relay URI is registered and shared by all environments:

```
https://<prod-frontend>/api/spotify/relay
```

How it works: `/api/spotify/connect` always asks Spotify to redirect to that relay, and encodes the
originating environment's own origin into a **signed** OAuth `state`. The relay
(`frontend/src/routes/api/spotify/relay/+server.ts`) verifies the HMAC signature, checks the origin
against an allowlist, then 303-bounces the browser to `<origin>/api/spotify/callback`. The browser
arrives there top-level on its own frontend origin, so the `mh_session` cookie rides along and the
originating environment completes the token exchange itself (`redirect_uri` = the relay URL) and
stores tokens in its own DB. The signature is what keeps the bounce from being an open redirect of
the auth `code`.

**One-time setup:**

1. **Spotify dashboard** → register exactly `https://<prod-frontend>/api/spotify/relay` as the only
   redirect URI (remove any old `…/api/spotify/callback` or `127.0.0.1` entries).
2. **Generate one signing key** (e.g. `openssl rand -base64 32`) and set it identically in three
   places — they must match or the relay rejects the state:
   - local dev: `dotnet user-secrets set "Parameters:spotify-oauth-state-key" "<key>" --project MusicHoarder.AppHost`
     (also set `Parameters:spotify-oauth-relay-url` to the relay URL, and `Spotify:ClientId`/`Spotify:ClientSecret`);
   - prod (Dokploy runtime env): `Spotify__OAuthStateSigningKey`;
   - previews (GitHub secret): `PREVIEW_SPOTIFY_OAUTH_STATE_KEY`.
3. **Prod relay allowlist** (`spotify-return-origin-allowlist`, the prod frontend's
   `SPOTIFY_RETURN_ORIGIN_ALLOWLIST` env in Dokploy) must list every origin the relay may bounce to:
   the prod origin, `https://*.<PREVIEW_BASE_DOMAIN>`, and — for local dev — `https://localhost:*`
   and `http://127.0.0.1:*`. A single `*` matches one host/port segment.

Local Spotify testing therefore depends on the prod relay being deployed and its allowlist
permitting loopback. For fully offline dev, leave `spotify-oauth-relay-url` empty and the redirect
falls back to the request origin (`Spotify:OAuthRedirectBaseUrl` is also still honored).

### Self-hosted / homelab deployment (docker-compose)

`docker-compose.yml` at the repo root is an alternative self-hosted path that builds the images locally instead of pulling prebuilt ones (images are no longer published to GHCR).

1. **Copy `docker-compose.yml`** and `.env.example` to the host.

2. **Create a `.env` file** next to `docker-compose.yml`:

   ```env
   POSTGRES_PASSWORD=a-strong-random-password
   ACOUSTID_API_KEY=your-acoustid-key
   MUSIC_SOURCE_PATH=/mnt/nas/music-source
   MUSIC_DESTINATION_PATH=/mnt/nas/music-clean
   ```

3. **Build and start the stack** (from a checkout of this repo):

   ```bash
   docker compose build
   docker compose up -d
   ```

   The API is reachable at `http://<host-ip>:5050` and the frontend at `http://<host-ip>:3000`.

#### Volume mounts

Adjust the paths in `docker-compose.yml` to match your NAS mount points:

```yaml
volumes:
  - /mnt/nas/music-source:/music/source:ro      # source library (read-only)
  - /mnt/nas/music-clean:/music/destination      # destination for cleaned library
  - /tmp/musicenricher:/tmp/musicenricher        # scratch space
```

#### Rollback

Check out a known-good commit and rebuild:

```bash
git checkout <commit-sha>
docker compose build
docker compose up -d
```

On Dokploy, use the application's deployment history to redeploy a previous commit.

---

## Pipeline notes

### fpcalc + AcoustID

`fpcalc` (Chromaprint) is included in the Docker image via `libchromaprint-tools`. Without `MusicEnricher__AcoustIdApiKey`, enrichment sets songs to `NeedsReview` rather than `Matched`, and the Library Builder skips them.

### Library page modes

The frontend Library page has two modes:

- **Source** — all scanned songs.
- **Destination** — only songs that completed the full pipeline (Matched → Copied/Tagged/Done).

### EF Core migrations

Migrations are applied automatically on container startup in all environments. No manual migration steps are required after a new image is deployed.
