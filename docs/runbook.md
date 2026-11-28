# Runbook

Day-to-day operations for a MusicHoarder instance: running it, upgrading it,
backing it up, and fixing it when it misbehaves — both a local development
stack (Aspire) and a self-hosted deployment (Docker Compose). For build/CI/CD
and release engineering, see [Deployment](deployment.md); for the full
self-host configuration reference (`.env` variables, integrations), see
[SELF_HOSTING.md](SELF_HOSTING.md).

## When to use this

- You want to **start or stop** MusicHoarder locally or on your server.
- A release came out and you want to **upgrade** (or pin/roll back).
- You need a **backup** or a **restore**.
- Something is broken: sign-in loops, scan stalls, permissions, database
  password mismatch, a failed deploy.

## Prerequisites

| Environment | Needs |
|---|---|
| Local dev | .NET SDK 11 (`global.json`), Bun, **Docker running** (Aspire provisions Postgres as a container) |
| Self-host | Docker + Docker Compose, the published compose file, an `.env` (optional for a localhost trial) |
| Self-host, real install | Reverse proxy for TLS (the app serves plain HTTP only), the five **Core** `.env` values: `POSTGRES_PASSWORD`, `MUSIC_SOURCE_PATH`, `MUSIC_DESTINATION_PATH`, `OWNER_EMAIL`, `PUBLIC_BASE_URL` |

## Procedures

### Run locally (development)

```bash
dotnet run --project MusicHoarder.AppHost
```

- The **Aspire dashboard** (`https://localhost:17072`) shows resources, logs,
  traces, and prompts for missing required parameters on first run.
- Pre-seed parameters instead of re-typing them:
  `dotnet user-secrets set "Parameters:source-directory" "…" --project MusicHoarder.AppHost`
  (same for `destination-directory`, `acoustid-api-key`,
  `spotify-client-id`, `spotify-client-secret`).
- Postgres runs as a **persistent container** with a named data volume
  (`MusicHoarder.AppHost/AppHost.cs`), so data survives AppHost restarts; EF
  migrations apply automatically at API startup.
- To run only the frontend against a running API: `bun run dev` in `frontend/`
  with `MUSICHOARDER_API_URL` pointed at the API port from the dashboard.

**Stop**: Ctrl-C the AppHost. The Postgres container persists; remove it and
its volume via the dashboard or `docker rm` if you want a clean slate.

### Run self-hosted

Zero-config trial (all values default to localhost):

```bash
mkdir musichoarder && cd musichoarder
curl -fsSLO https://raw.githubusercontent.com/Jeffreyyvdb/MusicHoarder/main/docker-compose.yml
docker compose up -d
```

Real deployment: start from `.env.example`, set the five Core values, then
`docker compose up -d`. Details, ports (frontend `:3000`, API `:5050`), reverse
proxying, and Portainer/TrueNAS/Synology variants are in
[SELF_HOSTING.md](SELF_HOSTING.md).

**First sign-in** is passwordless (magic link). Without `RESEND_API_KEY`, the
link is printed to the API logs:

```bash
docker compose logs api | grep -i magic
```

### Upgrade

```bash
docker compose pull
docker compose up -d
```

Migrations apply automatically on startup. Read the release notes first — the
app changes quickly — and back up before upgrading (below). Control what you
pull with `MUSICHOARDER_VERSION` in `.env`: `latest` (weekly), `2` / `2.5`
(major/minor lines), `2.5.1` (exact), or `edge` (every merge to `main`; the
maintainer's own instance runs this — not recommended). Releases are cut at
most weekly, so `latest` moves at most weekly.

For Dokploy-hosted instances: redeploy from the Dokploy UI (the compose service
pulls the tag configured in `API_IMAGE` / `FRONTEND_IMAGE`), or the same
webhook CI uses.

### Back up

Two named volumes hold state worth backing up:

| Volume | Holds | Notes |
|---|---|---|
| `postgres-data` | The catalog: scan results, matches, review decisions, lyrics | **Back this up.** |
| `musichoarder-dpkeys` | DataProtection keys (session cookies, encrypted Spotify credentials) | Back it up too, but keep it separate from the DB dump: a dump alone holds no usable Spotify token. Losing it logs everyone out and disconnects Spotify (recoverable by signing in again). |

```bash
docker compose exec postgres pg_dump -U postgres musichoarderdb > backup.sql
```

Your **source** library is mounted read-only and never modified. The **destination** library is
regenerable from source + catalog only for tracks that came from the source — tracks the download
chain acquired (wishlist, YouTube, Soulseek) never had a source file, and once staged-source
release is enabled their staged copy is deleted after the library copy is verified
(`Download/StagedSourceReleaseService.cs`), leaving the destination as the only copy. **Back the
destination library up alongside the database** (and the download staging dir if you want
crash-recovery for in-flight downloads).

### Restore

Restore the Postgres dump into a fresh `postgres-data` volume and restore the
`musichoarder-dpkeys` volume, then bring the stack up. Start Postgres alone first: after
`docker compose down` the `postgres` container is gone, so `exec` would fail, and booting the
whole stack would start the API against an empty database:

```bash
docker compose down                 # keep volumes
docker volume rm <project>_postgres-data
docker volume create <project>_postgres-data

# Postgres only — never start the API against an empty volume.
docker compose up -d postgres
until docker compose exec -T postgres pg_isready -U postgres -q; do sleep 2; done

# A fresh volume has no database yet; create it, then restore. Both connect with password auth
# (scram even on the local socket), so pass POSTGRES_PASSWORD from your .env.
set -e
docker compose exec -e PGPASSWORD="${POSTGRES_PASSWORD:?source .env first}" postgres \
  createdb -U postgres musichoarderdb
docker compose exec -e PGPASSWORD="${POSTGRES_PASSWORD:?source .env first}" -T postgres \
  psql -U postgres -d musichoarderdb -v ON_ERROR_STOP=1 < backup.sql
```

If `psql` fails, **stop here** — fix the dump (or pick an earlier one) and re-run the steps above
before letting the API start. Only once the restore exits clean, restore the `musichoarder-dpkeys`
volume contents and start the rest of the stack:

```bash
docker compose up -d
```

## Rollback

- **Self-host, image-level**: pin `MUSICHOARDER_VERSION` in `.env` to the
  previous release (e.g. `2.5.0`, no leading `v`) and `docker compose pull &&
  docker compose up -d`. Database migrations are forward-only as applied at
  startup — for a rollback across a migration, restore the pre-upgrade
  `backup.sql` (see above) along with the pinned image.
- **Dokploy / maintainer instance**: every edge build is also tagged
  `:sha-<commit>` on GHCR, so point `API_IMAGE` / `FRONTEND_IMAGE` at a known
  good `:sha-…` tag (or a release tag) and redeploy. Same migration caveat.
- **Compose/config changes**: the compose file and `.env` are plain files;
  keep the previous version (or let Git track a checkout) and `docker compose
  up -d` again to reapply.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `denied` / `unauthorized` pulling images | The official images are public. For a private fork: `docker login ghcr.io` with a `read:packages` token, or build from source (`docker-compose.build.yml`). |
| Tracks stuck in "Needs review" | No `ACOUSTID_API_KEY`. Without fingerprinting the pipeline can't confidently match most tracks. |
| Logged out after every restart | The `musichoarder-dpkeys` volume isn't persisting — make sure it's a named volume and not recreated. |
| Postgres auth failures after setting `POSTGRES_PASSWORD` | Postgres reads the password only at **first boot** (baked into `postgres-data`). Either `ALTER USER postgres PASSWORD '…'` inside the container, or wipe the volume (`docker compose down -v` — destroys the catalog). |
| Permission errors on the music mounts | The container must read the source (`:ro` by design) and write the destination. On a NAS, check dataset/share ownership and ACLs for the container's user. `PUID`/`PGID` run the API as that user, but nothing changes owners for you: every writable mount must already belong to that user (`docker/api-entrypoint.sh`). |
| Nothing happens after a scan | Check `docker compose logs api`. Large libraries take time; the pipeline runs in the background. `fpcalc` is baked into the image, so fingerprinting works out of the box. |
| Spotify login fails | The redirect URI in the Spotify dashboard must exactly match `<PUBLIC_BASE_URL>/api/spotify/callback`, and `PUBLIC_BASE_URL` must be the URL you actually visit. |
| Can't stay signed in over both HTTPS and LAN HTTP | When `PUBLIC_BASE_URL` is `https://…`, cookies are Secure and HTTP sessions won't stick. For LAN-only use set `PUBLIC_BASE_URL` to the `http://` address. |
| Signed-in app shows an update banner it shouldn't | Only relevant on `:edge` builds — the edge version string makes the update check ask GitHub whether the commit already holds the latest release; a stale cached answer resolves itself. |

## Escalation

- **Self-hosted**: the [troubleshooting section of
  SELF_HOSTING.md](SELF_HOSTING.md#troubleshooting) covers the same list with
  more context; beyond that, check the [issue
  tracker](https://github.com/Jeffreyyvdb/MusicHoarder/issues) and include
  `docker compose logs api` (redact tokens/emails) in any report.
- **Maintainer-hosted instance**: deploys are made by
  `.github/workflows/aspire-deploy.yml`, which verifies the new build is
  actually served (`/api/mh/api/version` + `/_app/version.json`) and retries
  the Dokploy webhook once — a failed run there means the instance never
  picked up the images; inspect the compose service's Deployments log and
  Containers tab in Dokploy.
- **Local dev**: the Aspire dashboard's structured logs and traces are the
  first stop; API health is at `/alive`, the frontend's at `/api/health` (the
  same endpoints the Docker healthchecks use).

## See also

- [SELF_HOSTING.md](SELF_HOSTING.md) — configuration reference and integrations.
- [Deployment](deployment.md) — how images are built and releases are cut.
