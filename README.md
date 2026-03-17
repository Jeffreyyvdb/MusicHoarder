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

## CI/CD — GitHub Actions + Watchtower

Every push to `main` automatically builds and pushes a fresh Docker image to GHCR. Watchtower running on the homelab polls GHCR every 5 minutes and restarts the container when a new image is detected.

```
Push to main
     ↓
GitHub Actions — run tests → build + push image to GHCR
     ↓
ghcr.io/jeffreyyvdb/musichoarder:latest
     ↓
Watchtower (homelab) polls every 5 min → pulls + restarts automatically
```

No SSH access, no open ports, no VPN required.

### GitHub Actions workflow

`.github/workflows/deploy.yml` runs on every push to `main`:

1. Runs the xUnit test suite.
2. Builds the Docker image using a multi-stage `Dockerfile`.
3. Pushes two tags to GHCR:
   - `ghcr.io/jeffreyyvdb/musichoarder:latest` — used by Watchtower for auto-deploy.
   - `ghcr.io/jeffreyyvdb/musichoarder:sha-<commit>` — for rollback.
4. Uses GitHub Actions cache (`type=gha`) so subsequent builds that only change .NET code complete in ~1–2 minutes.

No additional secrets are required — `GITHUB_TOKEN` is used automatically for GHCR authentication.

### Homelab deployment

#### One-time setup

1. **Copy `docker-compose.yml`** to the homelab (e.g. `~/musichoarder/docker-compose.yml`).

2. **Create a `.env` file** next to `docker-compose.yml`:

   ```env
   POSTGRES_PASSWORD=a-strong-random-password
   ACOUSTID_API_KEY=your-acoustid-key
   ```

3. **Authenticate Docker with GHCR** (only needed if the package is private):

   Create a GitHub Personal Access Token (PAT) with `read:packages` scope, then:

   ```bash
   echo "<YOUR_PAT>" | docker login ghcr.io -u jeffreyyvdb --password-stdin
   ```

   This writes credentials to `/root/.docker/config.json`, which Watchtower mounts.

   If you make the package public (GitHub → Packages → musichoarder → Package settings → Change visibility → Public), remove the `config.json` volume mount from the `watchtower` service.

4. **Start the stack**:

   ```bash
   docker compose pull
   docker compose up -d
   ```

   The API is reachable at `http://<homelab-ip>:5050`.

#### Volume mounts

Adjust the paths in `docker-compose.yml` to match your NAS mount points:

```yaml
volumes:
  - /mnt/nas/music-source:/music/source:ro      # source library (read-only)
  - /mnt/nas/music-clean:/music/destination      # destination for cleaned library
  - /tmp/musicenricher:/tmp/musicenricher        # scratch space
```

#### Rollback

Pull a specific SHA-tagged image and recreate the container:

```bash
docker pull ghcr.io/jeffreyyvdb/musichoarder:sha-abc1234
docker compose stop musichoarder
docker compose run --rm -d musichoarder
# or edit docker-compose.yml image tag and docker compose up -d
```

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
