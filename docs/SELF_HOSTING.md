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

The stack starts three containers: PostgreSQL, the API (`api`), and the frontend.
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
| `LYRICS_TRANSCRIPTION_API_KEY` | — | Experimental AI lyrics transcription + compare. **Blank → the feature is hidden in the UI.** Groq recommended; see below. |
| `LYRICS_TRANSCRIPTION_BASE_URL` / `_MODEL` / `_LLM_MODEL` | — | Transcription endpoint, Whisper model, and (optional) cleanup LLM for the above. |
| `PUBLIC_UMAMI_*` | — | Optional self-hosted Umami analytics. |

## First login

MusicHoarder uses passwordless **magic-link** sign-in.

1. Open the frontend and enter your `OWNER_EMAIL`.
2. If you configured Resend, the link arrives by email. **If you didn't** (`RESEND_API_KEY`
   blank), the link is written to the API logs instead:

   ```bash
   docker compose logs api | grep -i magic
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
- **AI lyrics transcription (experimental)** — set `LYRICS_TRANSCRIPTION_API_KEY` to enable, in a
  track's **Lyrics** tab, transcribing the audio into synced lyrics (for songs LRCLIB has none for)
  and comparing them side-by-side with LRCLIB, then choosing which version the player shows **and
  embeds into the file**. The whole feature is **hidden until the key is set**.
  [**Groq**](https://console.groq.com) is recommended (fast, cheap, has a free tier): set
  `LYRICS_TRANSCRIPTION_BASE_URL=https://api.groq.com/openai/v1` and
  `LYRICS_TRANSCRIPTION_MODEL=whisper-large-v3` (already the compose defaults). Songs that *do* have
  LRCLIB lyrics are timed by deterministic forced alignment (no LLM); songs with *no* lyrics at all
  optionally use a fast cleanup LLM (`LYRICS_TRANSCRIPTION_LLM_MODEL`) via the `QUALITY_GRADING_*` creds.
- **Umami analytics** — set `PUBLIC_UMAMI_SRC` (full `…/script.js` URL) and
  `PUBLIC_UMAMI_WEBSITE_ID` to load a self-hosted Umami tracker.
- **Soulseek via slskd** — MusicHoarder can use a [slskd](https://github.com/slskd/slskd) instance
  **you run and manage yourself** as a wishlist download source (tried before yt-dlp) and for
  manual per-track/album quality upgrades. MusicHoarder never joins the Soulseek network itself —
  it only calls slskd's REST API. Set `SLSKD_URL` (e.g. `http://slskd:5030`), `SLSKD_API_KEY`
  (an entry under slskd's `web.authentication.api_keys`), and bind slskd's **completed-downloads
  directory** into the api container read-write via `SLSKD_DOWNLOADS_HOST_PATH` (it's mounted at
  `/data/slskd-downloads`; finished files are moved out of it into the normal download staging
  dir, so it stays a transient staging area). All three unset → the integration is off and the
  provider chain quietly falls through to yt-dlp. Etiquette: share a folder in your slskd config —
  zero-share accounts get queued or banned by many peers — and leave MusicHoarder's built-in
  search rate limit alone unless you know why you're raising it.
- **Streaming-FLAC acquisition (`spotiflac`)** — *legally grey, off by default.* MusicHoarder can
  fetch true-lossless FLAC for wishlist downloads via an **acquisition sidecar** (a small FastAPI
  wrapper around the [`SpotiFLAC`](https://pypi.org/project/SpotiFLAC/) module, in
  [`sidecars/spotiflac/`](../sidecars/spotiflac/)). It relays through third-party servers to pull
  lossless audio without a streaming account, which is against those services' terms — enable it only
  where that's acceptable to you. MusicHoarder never talks to a streaming service directly and nothing
  about the sidecar is compiled into its image; it calls the sidecar's HTTP API as an opaque endpoint,
  exactly like slskd. The sidecar service already ships in `docker-compose.yml` **behind a Compose
  profile**, so enabling it is pure `.env` — no compose editing (works even for a read-only
  Git-synced compose). Set: `COMPOSE_PROFILES=spotiflac` (starts the sidecar container, pulled from
  GHCR), `SPOTIFLAC_SIDECAR_URL=http://spotiflac:8000`, and put `spotiflac` first in the chain —
  `DOWNLOAD_PROVIDER_1=spotiflac`, `DOWNLOAD_PROVIDER_2=slskd`, `DOWNLOAD_PROVIDER_3=yt-dlp` — then
  `docker compose up -d`. Leave `COMPOSE_PROFILES` unset and the sidecar never starts and the provider
  reports "not found", so it's inert for everyone who doesn't opt in. The sidecar shares the API's
  download staging volume at the same path, so the FLAC it writes is visible to the API. A track with
  no lossless source upstream falls through to slskd/yt-dlp; a downed sidecar fails the item and
  retries next sweep. You can point it at self-hosted Tidal/Qobuz backends
  (`SPOTIFLAC_TIDAL_CUSTOM_API` / `SPOTIFLAC_QOBUZ_LOCAL_API_URL`) instead of its built-in community
  relay — see [`sidecars/spotiflac/README.md`](../sidecars/spotiflac/README.md).
- **Instance sync** — one MusicHoarder (e.g. your homelab) can push every finished track to
  another (e.g. a public VPS) over plain HTTPS: after a track's library build completes, the
  pusher asks the receiver "do you have this track, at what quality?" (by audio fingerprint /
  AcoustID / MusicBrainz id — never by database id) and uploads only when the receiver is missing
  it or holds a worse copy; better copies replace the receiver's file **in place**, preserving its
  track id so stream URLs keep working. Generate one shared key with `openssl rand -base64 48`,
  then on the receiver set `SYNC_MODE=Receive` + `SYNC_API_KEY=<key>`; on the pusher set
  `SYNC_MODE=Push`, `SYNC_API_KEY=<key>`, and `SYNC_REMOTE_URL=https://<receiver-api-origin>`.
  The receive endpoints answer 404 unless `SYNC_MODE=Receive`, so the surface is invisible
  everywhere else; failed pushes retry with backoff automatically.

## Updating

```bash
docker compose pull
docker compose up -d
```

Pulls the newest images and recreates the containers. Database migrations apply automatically on
startup. To stay on a known version, pin `MUSICHOARDER_VERSION` in `.env`.

## Backups

Two named volumes hold state worth backing up:

- **`postgres-data`** — your catalog: scan results, matches, review decisions, lyrics. Back this
  up (e.g. `docker compose exec postgres pg_dump -U musichoarder musichoarderdb > backup.sql`).
- **`musichoarder-dpkeys`** — the DataProtection keys that sign session cookies. Losing it just
  logs everyone out (they sign in again); it's not catastrophic, but persisting it keeps sessions
  valid across restarts.

Your **source** library is read-only and never touched. The **destination** library is fully
regenerable from the source + catalog, so it doesn't strictly need backing up.

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
- **Nothing happens after a scan** — check `docker compose logs api`; `fpcalc`
  (Chromaprint) is baked into the image, so fingerprinting works out of the box, but very large
  libraries take time. The processing pipeline runs in the background.
- **Spotify login fails** — the redirect URI in the Spotify dashboard must exactly match
  `<PUBLIC_BASE_URL>/api/spotify/callback`, and `PUBLIC_BASE_URL` must be the URL you actually
  visit.

## See also

- Maintainer release/CI pipeline and zero-downtime (Docker Swarm) notes: the
  [Deployment (CI/CD)](../README.md#deployment-cicd) section of the README.
