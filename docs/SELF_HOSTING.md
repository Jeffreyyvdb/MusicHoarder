# Self-hosting MusicHoarder

This guide covers running MusicHoarder on your own hardware — a homelab box, a NAS (TrueNAS,
Synology, Unraid), or any host with Docker. It uses the **prebuilt images** published to GHCR,
so you don't need a repo checkout or any build toolchain.

> TLS / reverse proxy is intentionally **out of scope** for this app. MusicHoarder serves plain
> HTTP; put it behind your own reverse proxy (Traefik, Caddy, Nginx Proxy Manager, your NAS's
> built-in proxy, …) and point `PUBLIC_BASE_URL` at the external URL.

## Quick start (prebuilt images)

You only need two files: `docker-compose.yml` and an `.env`.

```bash
mkdir musichoarder && cd musichoarder

# Grab the compose file and the env template
curl -fsSLO https://raw.githubusercontent.com/Jeffreyyvdb/MusicHoarder/main/docker-compose.yml
curl -fsSL  https://raw.githubusercontent.com/Jeffreyyvdb/MusicHoarder/main/.env.example -o .env

# Edit .env and fill in the required values (see the table below)
nano .env

docker compose up -d
```

The stack starts three containers: PostgreSQL, the API (`musichoarder`), and the frontend.
Migrations apply automatically on first boot.

- Frontend (the web UI): `http://<host-ip>:3000`
- API: `http://<host-ip>:5050`

Point your reverse proxy at the frontend (`:3000`).

> The official images are **public**, so no GitHub login or token is needed to pull them. (Only
> relevant if you publish your own *private* fork: then `docker login ghcr.io` first.)

## Configuration

All values are set in `.env`. The compose file maps them onto the app's environment variables
for you. See [`.env.example`](../.env.example) for the annotated source of truth.

| Variable | Required | Purpose |
|----------|:--------:|---------|
| `POSTGRES_PASSWORD` | ✅ | Password for the bundled Postgres. Use a long random string. |
| `MUSIC_SOURCE_PATH` | ✅ | Host path to your existing library. Mounted **read-only**; never modified. |
| `MUSIC_DESTINATION_PATH` | ✅ | Host path where the cleaned, organized copy is written. |
| `OWNER_EMAIL` | ✅ | Admin account; sign in with this address. |
| `PUBLIC_BASE_URL` | ✅ | The external URL you reach the app at (used for magic links + Spotify redirects). |
| `MUSICHOARDER_VERSION` | — | Pin a release tag instead of `latest`. |
| `ACOUSTID_API_KEY` | — | Audio-fingerprint identification. Strongly recommended (see below). |
| `SPOTIFY_CLIENT_ID` / `SPOTIFY_CLIENT_SECRET` | — | Spotify metadata enrichment + OAuth. |
| `DEMO_USER_EMAIL` | — | Read-only demo account (defaults to `demo@musichoarder.local`). |
| `RESEND_API_KEY` / `RESEND_FROM_ADDRESS` | — | Send magic-link emails. Blank → link printed to logs. |
| `QUALITY_GRADING_*` | — | Optional AI quality grading (OpenAI-compatible). |
| `PUBLIC_UMAMI_*` | — | Optional self-hosted Umami analytics. |

## First login

MusicHoarder uses passwordless **magic-link** sign-in.

1. Open the frontend and enter your `OWNER_EMAIL`.
2. If you configured Resend, the link arrives by email. **If you didn't** (`RESEND_API_KEY`
   blank), the link is written to the API logs instead:

   ```bash
   docker compose logs musichoarder | grep -i magic
   ```

   Copy the URL into your browser to finish signing in.

There's also a read-only **demo** account (`DEMO_USER_EMAIL`) reachable via the "Try the demo"
button — handy for showing the UI without exposing write access.

## Behind your reverse proxy

The app speaks plain HTTP only — TLS is your proxy's job. Typical setup:

- Route your chosen hostname → `http://<host-ip>:3000` (the frontend).
- Set `PUBLIC_BASE_URL` to that external `https://…` URL.

The frontend proxies all API calls to the backend over the internal Docker network, so you do
**not** need to expose or proxy `:5050` publicly. (It's published on the host for convenience and
debugging; you can remove that port mapping if you don't want it reachable.)

For **LAN-only** use without a proxy, set `PUBLIC_BASE_URL=http://<host-ip>:3000` and browse to
that address directly. Some features that depend on HTTPS (e.g. passkeys, certain OAuth flows)
are limited over plain HTTP.

## Portainer / TrueNAS / Synology

Rather than an `.env` file, these UIs let you paste a Compose stack and set the variables in the
web form:

1. Create a new **Stack** (Portainer) / **Custom App** (TrueNAS SCALE) / **Project** (Synology
   Container Manager).
2. Paste the contents of `docker-compose.yml`.
3. Add the same environment variables from the table above in the UI's env section.
4. Make sure `MUSIC_SOURCE_PATH` / `MUSIC_DESTINATION_PATH` point at real dataset/share paths the
   container can read/write (see Troubleshooting).
5. Deploy.

## Optional integrations

- **AcoustID** — identifies tracks by their actual audio. Get a free key at
  <https://acoustid.org/new-application> and set `ACOUSTID_API_KEY`. Without it, most tracks land
  in the review queue rather than matching automatically.
- **Spotify** — register an app at <https://developer.spotify.com/dashboard>, set
  `SPOTIFY_CLIENT_ID` / `SPOTIFY_CLIENT_SECRET`, and add the redirect URI
  `<PUBLIC_BASE_URL>/api/spotify/callback` in the Spotify dashboard.
- **AI quality grading** — point `QUALITY_GRADING_*` at any OpenAI-compatible endpoint
  (OpenRouter by default) to let an LLM grade match/metadata quality for triage.
- **Umami analytics** — set `PUBLIC_UMAMI_SRC` (full `…/script.js` URL) and
  `PUBLIC_UMAMI_WEBSITE_ID` to load a self-hosted Umami tracker.

## Updating

```bash
docker compose pull
docker compose up -d
```

Pulls the newest images and recreates the containers. Database migrations apply automatically on
startup. To stay on a known version, pin `MUSICHOARDER_VERSION` in `.env`.

## Backups

Everything worth keeping — scan results, enrichment provider matches, review decisions, AI quality
grades, lyrics, manual approvals, Spotify tokens + liked-songs match cache, canonical albums — lives
in the **`musichoarderdb`** Postgres database (the `postgres-data` volume). Back it up with:

```bash
./scripts/backup.sh
# or, by hand:
docker compose exec -T postgres pg_dump -U postgres -d musichoarderdb --clean --if-exists > backup.sql
```

The other named volume, **`musichoarder-dpkeys`**, holds the DataProtection keys that sign session
cookies. It's *not* in the database. Losing it just logs everyone out (they sign in again); it's not
catastrophic, but persisting it keeps sessions valid across restarts.

Your **source** library is read-only and never touched. The **destination** library is fully
regenerable from the source + catalog, so it doesn't strictly need backing up.

### Migrating from an existing instance (e.g. Dokploy → homelab)

All the derived work is in `musichoarderdb`, so moving to a new host is a database dump + restore —
you don't re-run any enrichment provider or AI analysis. There's **one catch that will bite you if
you skip it**: the catalog stores *absolute* file paths, so you must remap them whenever the new
host mounts your library at different paths than the old one did (it almost always does — see the
box below). Do the restore + remap with **only Postgres running**, before the API ever starts.

> **Why the remap is mandatory.** Each row in the `Songs` table stores an absolute `SourcePath`
> (and `DestinationPath`). The API matches files to rows by *exact* `SourcePath`, and a scan fires
> automatically on first boot. The Dokploy/Aspire stack bind-mounts your library at its host path
> (so paths look like `/root/music/…`), while the self-host compose mounts it at a fixed
> `/music/source`. If the stored paths don't match what the new stack scans, that first scan treats
> every file as new and **soft-deletes your entire imported catalog**, then re-enriches from
> scratch. Remapping the paths first makes the scan a no-op (`0 new … Marked 0 files as deleted`).

1. **On the source**, dump the database.

   - If it's a plain `docker compose` stack: `./scripts/backup.sh` (writes
     `musichoarder-backup-<timestamp>.sql`).
   - On **Dokploy** (Docker Swarm — `docker compose exec` doesn't apply), run `pg_dump` straight
     from the container. Note that Dokploy hardens Postgres with `scram-sha-256`, so you must pass
     the password:

     ```bash
     CTR=$(docker ps --filter name=postgres --format '{{.Names}}' | head -n1)
     docker exec "$CTR" sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" \
       pg_dump -U postgres -d musichoarderdb --clean --if-exists' > backup.sql
     ```

2. **Copy** the `.sql` file to the new host, next to its `docker-compose.yml`.

3. **On the target**, restore into a fresh database with only Postgres up:

   ```bash
   docker compose up -d postgres                 # database only; wait until healthy
   ./scripts/restore.sh backup.sql               # restore.sh prints the stored paths + next steps
   ```

   `restore.sh` finishes by printing the `SourcePath`/`DestinationPath` prefixes the dump brought
   in. Compare them with what *this* stack scans:

   ```bash
   docker compose config | grep -iE 'SourceDirectory|DestinationDirectory'
   ```

4. **Remap the paths** if they differ (they will, coming from Dokploy). `--source`/`--dest` take
   path *prefixes*; the rewrite is prefix-anchored and leaves the demo account's rows alone:

   ```bash
   ./scripts/remap-paths.sh \
     --source /root/music             /music/source \
     --dest   /root/musichoarder-dest /music/destination
   ```

   Use the real prefixes you saw in step 3. Also make sure `.env`'s `MUSIC_SOURCE_PATH` /
   `MUSIC_DESTINATION_PATH` point the `/music/source` and `/music/destination` mounts at the actual
   library folders on the new host.

5. **(Recommended) match the enrichment env** to the source — `ACOUSTID_API_KEY`, `SPOTIFY_*`,
   `QUALITY_GRADING_*` in `.env`. If the enabled-provider set differs, the API runs a "provider-set
   change" sweep that needlessly re-enriches (and may re-grade) songs that were already done.

6. **Start the rest of the stack and confirm the scan is a no-op:**

   ```bash
   docker compose up -d
   docker compose logs -f musichoarder
   ```

   You want `Discovery complete: N total, 0 new, 0 changed, N unchanged` and `Marked 0 files as
   deleted`. If you see that, your full catalog (matches, grades, lyrics, review queue) is preserved.

Notes:

- **Spotify** tokens come across in the dump, so no re-auth — but if the new host has a different
  `PUBLIC_BASE_URL`, register `<new PUBLIC_BASE_URL>/api/spotify/callback` in the Spotify dashboard.
- The **`musichoarder-dpkeys`** volume isn't in the dump; skipping it just means everyone logs in
  again on the new host.
- Both stacks use the `postgres` role by default, so ownership transfers cleanly. If your *old*
  volume was initialized under a different role (e.g. an early self-host install that used
  `musichoarder`), pass `PG_USER=musichoarder` to `backup.sh`, and set `POSTGRES_USER` in the
  target's `.env` if you're reusing such a volume — `POSTGRES_USER` only applies to a fresh data dir.
- If you already booted the API before remapping and it soft-deleted everything: don't panic, no
  data is lost from the dump. `docker compose down -v`, then redo from step 3 (restore → remap →
  start) on a fresh volume.

## Build from source

If you'd rather build the images locally instead of pulling them, grab a checkout of the repo and
layer the build override:

```bash
git clone https://github.com/Jeffreyyvdb/MusicHoarder.git
cd MusicHoarder
cp .env.example .env   # fill it in
docker compose -f docker-compose.yml -f docker-compose.build.yml up -d --build
```

## Troubleshooting

- **`denied` / `unauthorized` when pulling** — the official images are public, so this shouldn't
  happen for them. If you're pulling a *private* fork of the images, `docker login ghcr.io` with a
  `read:packages` token first, or build from source.
- **Tracks stuck in "Needs review"** — set `ACOUSTID_API_KEY`. Without fingerprint
  identification, the pipeline can't confidently match most tracks.
- **Logged out after every restart** — the `musichoarder-dpkeys` volume isn't persisting. Make
  sure it's a named volume (it is in the shipped compose) and not being recreated.
- **Permission errors on the music mounts** — the container must be able to *read* the source and
  *write* the destination. On a NAS, check the dataset/share ownership and ACLs for the user the
  container runs as. The source mount is read-only by design (`:ro`).
- **Nothing happens after a scan** — check `docker compose logs musichoarder`; `fpcalc`
  (Chromaprint) is baked into the image, so fingerprinting works out of the box, but very large
  libraries take time. The processing pipeline runs in the background.
- **Spotify login fails** — the redirect URI in the Spotify dashboard must exactly match
  `<PUBLIC_BASE_URL>/api/spotify/callback`, and `PUBLIC_BASE_URL` must be the URL you actually
  visit.

## See also

- Maintainer release/CI pipeline and zero-downtime (Docker Swarm) notes: the
  [Deployment (CI/CD)](../README.md#deployment-cicd) section of the README.
