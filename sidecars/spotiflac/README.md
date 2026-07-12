# spotiflac sidecar

A thin FastAPI wrapper around the [`SpotiFLAC`](https://pypi.org/project/SpotiFLAC/) pip module that
MusicHoarder's `spotiflac` download provider calls to fetch true-lossless FLAC. MusicHoarder owns all
metadata, so this wrapper runs with enrichment + lyrics **off** and just writes one untagged FLAC.

> **Opt-in and legally grey.** This sidecar is **not** part of the default MusicHoarder stack and is
> not baked into the published MusicHoarder images. It relays through third-party servers to acquire
> lossless audio, which is against those streaming services' terms of service — run it only where
> that's acceptable to you. MusicHoarder talks to it over HTTP as an opaque endpoint; nothing about
> it is compiled into `MusicHoarder.Api`. Off unless you explicitly enable it (see below).

## Credits

Wraps **SpotiFLAC** by [@ShuShuzinhuu](https://github.com/ShuShuzinhuu/SpotiFLAC-Module-Version)
(the Python module) — itself derived from [@afkarxyz/SpotiFLAC](https://github.com/afkarxyz/SpotiFLAC).
Both are MIT-licensed. This wrapper only imports the published `SpotiFLAC` PyPI package; it does not
vendor their source. All acquisition logic lives in that module — this folder is just the HTTP glue.

## HTTP contract (matches `StreamingFlacSidecarClient.cs`)

| Endpoint | Response |
|---|---|
| `GET /health` | `{"status":"ok","providers":["qobuz","tidal"]}` |
| `POST /acquire` | `{"status":"ok","file":"<abs path>","provider":"qobuz"}` · `{"status":"not_found","error":...}` · `{"status":"error","error":...}` |

`POST /acquire` body: `{spotify_url, quality, services, output_dir, filename_stem, timeout_s}`. The file
is written to `{output_dir}/{filename_stem}.flac` — `output_dir` is the shared staging volume, mounted
into **both** containers at the same absolute path so the returned path resolves on the API side too.

## Enable it (Compose)

The `spotiflac` service already ships in `docker-compose.yml` **behind a Compose profile**, pulling its
image from GHCR. Turning it on is pure `.env` — no compose editing (so it works even with a read-only
Git-synced compose):

```dotenv
COMPOSE_PROFILES=spotiflac          # starts the sidecar container
SPOTIFLAC_SIDECAR_URL=http://spotiflac:8000
DOWNLOAD_PROVIDER_1=spotiflac       # prefer lossless; falls through to the rest
DOWNLOAD_PROVIDER_2=slskd
DOWNLOAD_PROVIDER_3=yt-dlp
# optional: pin the image / use your own backends
# SPOTIFLAC_VERSION=latest
# SPOTIFLAC_TIDAL_CUSTOM_API=...
# SPOTIFLAC_QOBUZ_LOCAL_API_URL=...
```

Then `docker compose up -d`. Leave `COMPOSE_PROFILES` unset and the sidecar never starts.

**Build from source** instead of pulling the image: layer the build override, which adds `build:` to
this folder — `docker compose -f docker-compose.yml -f docker-compose.build.yml up -d --build`
(with `COMPOSE_PROFILES=spotiflac`).

## Run standalone (dev / testing)

```bash
docker build -t spotiflac-sidecar ./sidecars/spotiflac

# Mount the SAME host path MusicHoarder uses for its download staging dir at the SAME container path
# the API sees (MusicEnricher__DownloadDirectory, e.g. /data/downloads).
docker run --rm -p 8000:8000 \
  -v /srv/musichoarder/downloads:/data/downloads \
  spotiflac-sidecar
```

## Optional: self-hosted backends

Instead of the built-in community relay, point at your own instances:

- `SPOTIFLAC_TIDAL_CUSTOM_API` — a self-hosted hifi-api URL
- `SPOTIFLAC_QOBUZ_LOCAL_API_URL` — a local Qobuz-DL stream API URL

## Notes

- **Write path is confined.** All writes are restricted to `SPOTIFLAC_STAGING_DIR` (default
  `/data/downloads`, which matches the API's `MusicEnricher__DownloadDirectory` in the self-host
  compose) and the filename is limited to a safe charset. If you run the API with a non-default
  download dir, set `SPOTIFLAC_STAGING_DIR` on the sidecar to match.
- **Pin the module.** It ships frequently (v1.3.1 as of Jul 2026); bump `requirements.txt` deliberately.
- The `SpotiFLAC()` return value is undocumented, so the wrapper decides success by checking the output
  file exists and is non-empty. If the module API changes, adjust `_run_spotiflac` / `_looks_like_no_source`.
- Amazon is out of scope (needs CENC decryption) — stick to Tidal/Qobuz (and Deezer if you want a third).
