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
| `frontend` | Next.js frontend — library browser, scan/enrich progress, manual review UI |

---

## Local Development

### Prerequisites

- .NET 10 SDK
- Docker (for PostgreSQL via Aspire)
- Node.js 22 + pnpm
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

The AppHost uses `.WithNpm()` but the frontend only has `pnpm-lock.yaml`. Start it separately for reliability:

```bash
cd frontend && MUSICHOARDER_API_URL=http://localhost:<api-port> PORT=3000 pnpm dev
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
| Frontend | Docker | `frontend/` | `frontend/Dockerfile` | Exposes port `3000`. Set build arg `NEXT_PUBLIC_DEMO_MODE=true` for the demo deployment |

Enable Dokploy's GitHub webhook on each app so a push to `main` triggers a rebuild automatically. Runtime env vars (`ConnectionStrings__musichoarderdb`, `MusicEnricher__*`, `MUSICHOARDER_API_URL`, etc.) are configured per-app in Dokploy.

Because the frontend bakes `NEXT_PUBLIC_*` values at build time, `NEXT_PUBLIC_DEMO_MODE` must be set as a **build arg** in Dokploy, not a runtime env var.

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
