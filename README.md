# MusicHoarder

**Fix your messy music library — automatically — then listen to it in an app that feels at home on
your iPhone, your Android phone and your desktop.**

MusicHoarder is a self-hosted, open-source app that scans a large, disorganized music collection
(including NAS/SMB shares), identifies each track by its actual audio, enriches it with proper
metadata, and builds a clean, consistently-organized copy — without ever touching your originals.
Once it's clean, it's a full music player in the style of Apple Music: browse, play with synced
lyrics, like, get stats, discover new playlists, and grow your library from Spotify or Soulseek.

[![CI](https://github.com/Jeffreyyvdb/MusicHoarder/actions/workflows/ci.yml/badge.svg)](https://github.com/Jeffreyyvdb/MusicHoarder/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

If you've ever ended up with thousands of files like `track_047.mp3`, duplicate rips at different
bitrates, missing artwork, and inconsistent folder names, this is for you. Point it at your source
library, let it run, and review anything it isn't sure about.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/screenshots/hero-dark.webp">
  <img alt="MusicHoarder on the desktop and on an iPhone — the Albums grid of a real library next to Now Playing with time-synced lyrics over the album's artwork" src="docs/screenshots/hero.webp">
</picture>

<sub>Every screenshot in this README is of a real, self-hosted library of about 20,000 files — not
a mock-up. The image above follows your GitHub light or dark theme.</sub>

## Features

### Listen — an Apple Music-style player for the library you own

![On an iPhone: the Listen tab's overview with its library shortcuts and favourite tracks, an album page with Play and Shuffle and its canonical-tracklist status, and Now Playing showing synced lyrics](docs/screenshots/phone-listen.webp)

- **Built like a native iOS app** — on a phone, a floating glass tab bar (Listen · Inbox · Add ·
  Manage) with its own search button, large titles that collapse as you scroll, grouped lists,
  bottom sheets, long-press menus and an edge swipe for Back. Each tab keeps its own navigation
  stack, so switching tabs returns you to where you were, and re-tapping a tab pops it to its root.
  On the desktop the same app has a sidebar, a toolbar and a ⌘K command palette.
- **Apple's design language, accessibility included** — the system palette in light and dark,
  iOS text styles that follow the iPhone's Text Size (Dynamic Type), touch targets sized to
  Apple's guidelines, and support for Reduce Motion, Reduce Transparency and Increase Contrast.
  Every text colour is contrast-checked.
- **Now Playing** — a full-screen, always-dark player over a dimmed, blurred copy of the cover, with
  karaoke-style **synced lyrics** that follow the song (tap a line to jump to it), **music videos**
  that play in sync with the audio, and an Info view with the track's tags, match confidence,
  fingerprint and every provider's answer. Drag it down to dismiss; the mini player keeps playing
  as you move around the app.
- **Keeps playing** — when a queue runs out, a server-side radio continues with similar songs from
  your library (artist, genre, era and your own likes and plays), so a one-track album doesn't end
  in silence. The installed iPhone app keeps playing in the background, and formats Safari can't
  play (such as Ogg Opus) are decoded as they stream.
- **Browse the way you listen** — a personal Overview with shelves built from your real play and
  like history, Albums, Artists with an A–Z index, and one Tracks list sliced by filters (Spotify
  liked, favourites, local files, has video, with lyrics, unreleased). On a phone, tapping a row
  plays the list you're looking at.
- **Likes & play history** — heart any track; plays are tracked (count + last played) to power
  most-played, recently-added and "never played" shelves.
- **Install it** — add it to your iPhone or iPad home screen, or install it from Chrome/Edge; it
  launches full-screen with its own icon and shows an offline page instead of a browser error. The
  native [Android app](android/README.md) mirrors the same design and behaviour.

![Now Playing on the desktop: the album cover and transport controls on the left, and the song's time-synced lyrics on the right with the current line highlighted](docs/screenshots/now-playing.webp)

### Identify, enrich & organize

- **Audio fingerprinting** — identifies each track by its actual sound (AcoustID/Chromaprint), not
  by unreliable filenames or existing tags, so `track_047.mp3` gets recognized for what it is.
- **Multi-provider enrichment with consensus** — every song is matched independently by up to six
  sources — **AcoustID**, **MusicBrainz**, **Spotify**, **Apple Music**, **Deezer**, and community
  trackers — and a consensus evaluator derives the verdict by agreement: when two or more providers
  land on the same recording it auto-matches, a lone fingerprint hit only becomes a candidate, and
  conflicting confident matches go to review. Every provider is individually toggleable, and the app
  degrades gracefully when a credential is missing.
- **Non-destructive, quality-aware tagging** — source files are always read-only; clean copies are
  written to a separate destination library. Matching never blindly overwrites good tags — empty
  fields are filled, a curated value that disagrees is kept and logged as a *proposed* change unless
  a strong consensus justifies the upgrade, and every applied or proposed change is recorded.
- **Whole-album reconciliation & healing** — because tracks are enriched independently, one real
  album can split across release IDs, titles, years, or artist spellings. A build-time reconciler
  elects one canonical identity per folder and tags every track with it, keeping albums whole in
  players like Navidrome. It's build-time and reversible — your per-track enrichment is untouched.
- **Canonical album tracklists** — cross-checks each album against MusicBrainz, Spotify, Apple
  Music, and Deezer to build the *full* tracklist, so the album page shows every real track and
  marks the ones you're missing, with one tap to fetch them.
- **Cover art** — resolves album artwork (folder image → embedded picture, then Cover Art Archive →
  Deezer → iTunes when a file has none), validates it, and writes a single cover into each
  destination folder so players show the real sleeve.
- **Duplicate detection** — groups songs by identical fingerprint and elects the best copy (codec
  tier and bitrate, then metadata trustworthiness, then file size) so only the best version is built.
- **Synced lyrics + AI transcription** — fetches time-synced (karaoke-style) or plain lyrics from
  LRCLIB and embeds them into the built file. *Optional, experimental:* when no lyrics exist
  anywhere, an OpenAI-compatible **Whisper** pass (default `whisper-1`; repoint it at Groq or a
  self-hosted model) transcribes the audio into a fresh synced `.lrc` — stored **separately** from
  any curated lyrics and clearly marked, so it never overwrites the real thing. No key → the
  feature is just off.
- **Community trackers** *(optional)* — artist-scoped catalogs cover leaks, alternate versions, and
  unreleased albums that mainstream services don't, gated to a per-artist allowlist.

![An album page on the desktop: Mac Miller's Circles, linked to Spotify and Deezer with 10 of 12 tracks present — the missing opener is listed with "Get this track" and "Find this track", and every owned track shows its format, size and match confidence](docs/screenshots/album.webp)

### Review & curate

- **The Inbox** — everything the pipeline couldn't decide on its own, one queue per kind of
  decision, each with a live count: **Tag review** (low-confidence matches to approve, correct, or
  bulk-approve above a confidence threshold, with every provider's candidate and a field-by-field
  diff before anything is written), **Duplicate tracks** (compare fingerprint twins and keep the
  best), **Artist names** and **Album names** (spellings of one artist or album that could be
  merged), and **AI flagged** (matches the grader marked Wrong or Questionable). On a phone each
  queue opens as a list with pushed details and a bottom Accept / Skip / Reject toolbar.
- **Optional AI quality grading** — an LLM grades each match (and whole-album matches) with a 0–100
  score, verdict, summary, and issue codes, powering rollups and flagged/verified buckets so you can
  triage what actually needs attention. OpenAI-compatible; defaults to OpenRouter, works with a local
  Ollama. Re-gradeable when the prompt or model changes.

![Tag review on the desktop: a file named "63 (Dagger) (feat. MadeInTYO).mp3" identified by its fingerprint as "Lean Wit Me" at 100%, next to the other providers' candidates and a From → Will-write-to diff of the file's path](docs/screenshots/inbox-review.webp)

![On an iPhone: the Inbox hub with a count per queue, a Tag review decision with provider candidates and a bottom Accept toolbar, and the Pipeline summary with its stage-by-stage counts](docs/screenshots/phone-manage.webp)

### Grow your library

- **Discover playlists** *(optional)* — browse Deezer-backed editorial and chart playlists by genre
  or search, or paste a Spotify/Deezer playlist link, then subscribe so new tracks are wishlisted
  and (with downloads enabled) fetched automatically.
- **Spotify sync** *(optional)* — connect a Spotify account (read-only) and browse your Liked Songs
  and playlists with every track's local-library match shown inline. Add any playlist (or your Liked
  Songs) as an **auto-syncing wishlist source** so new additions flow in on their own, see a
  track-by-track *in-library vs missing* comparison, and a fast poll picks up songs you just liked
  within seconds.
- **Wishlist with auto-download** *(optional)* — everything wishlisted (from Spotify sync or
  Discover) is turned into an actual file by an ordered fetch chain: an optional streaming-FLAC
  sidecar, a self-run [slskd](https://github.com/slskd/slskd) (Soulseek), then a yt-dlp fallback
  that keeps native Opus and stamps the authoritative identity so the download enriches correctly
  and lands in the right album. Already-owned tracks are skipped; failures retry individually or in
  bulk. Off by default; see the [self-hosting guide](docs/SELF_HOSTING.md#optional-integrations).
- **Add from link** — paste a Spotify track or YouTube link to download it straight into the
  library.
- **Playlist sync** *(optional)* — mirror your Spotify Liked Songs or any playlist as a static
  `.m3u8` file in the destination library, in order, so Navidrome/Plex/Jellyfin auto-import it.

![The Discover page — Deezer-backed editorial and chart playlists with genre filters, a search box, a paste-a-link button, and one-click subscribe](docs/screenshots/discover.webp)

### Share

- **Public share links** — mint a revocable, no-account link scoped to a single track or a whole
  album. The public page is a chrome-free player with ambient artwork, an album queue that plays
  straight through, and a full-screen synced-lyrics view. Nothing else in your library is exposed.
  With the Android app installed, share and invite links open natively in the app.
- **Member accounts** — invite people by email (Settings → People mints a one-time link) to create
  their own listen-only account, then share albums, artists, or your entire library with each of
  them. Members get the same listening app — Overview, Albums, Artists and Tracks, Now Playing with
  lyrics and music videos, and their own likes and play history — over exactly what you granted.
  Every grant is revocable, and removing someone disables their account and signs them out
  everywhere.
- **Several accounts on one device** — sign in to more than one account (say, your own and the
  demo) and switch between them from the account menu, on the web and on Android.

### Watch it work

- **Pipeline** — a live dashboard of the whole flow, Scan → Fingerprint → Match → Decide → AI grade
  → Dedupe → Library, with what's in flight, what's awaiting you, and what just landed. New files
  copied onto the source share are picked up by a periodic re-scan.
- **History** — one feed that answers "what has this been doing?": what was acquired, built,
  identified, renamed, healed, given lyrics, videos or artwork, and what the app itself changed,
  filterable by kind and period. Tag diffs written to the destination are kept as a permanent log,
  so you can see exactly what Navidrome sees differently.
- **Stats** — a "hoard at a glance" page: hero counts, the pipeline and Spotify-wishlist funnels,
  top artists, biggest albums, format breakdown and match status; plus per-folder, AI-quality,
  album-match and performance-over-time views under Manage.

![The Pipeline dashboard: 20,192 source files, 18,530 in the library, 3,784 awaiting a decision and an average AI quality of 82.4, above the stage-by-stage conveyor and the "Needs you" queue](docs/screenshots/pipeline.webp)

<p>
  <img width="49%" alt="The Stats page: library, cover, lyrics and liked-to-library counts above the pipeline funnel, the Spotify wishlist journey, top artists and a format breakdown" src="docs/screenshots/stats.webp">
  <img width="49%" alt="The History feed: scans, cover art added, artists renamed and album splits healed, each with when it happened, filterable by kind and period" src="docs/screenshots/history.webp">
</p>

### Optional integrations

- **Navidrome two-way like sync** — keeps song likes in sync in both directions with a Navidrome
  server via its Subsonic *starred* API (immediate push on toggle + periodic reconcile).
- **Soulseek quality upgrades** — manually queue a search for a strictly-better copy of a track or
  album on Soulseek (via slskd); the better file is swapped **in place**, keeping the track's id,
  enrichment, and lyrics.
- **Instance sync (NAS → VPS)** — push finished tracks from a private instance to a public one over
  plain HTTPS. A portable fingerprint-based existence check means only missing or better-quality
  files transfer, in-place replacement keeps the remote's track ids stable, and an at-least-once
  outbox with retries survives crashes.

### Platform

- **Passwordless auth + read-only demo** — sign in by emailed magic link or WebAuthn passkey
  (Face ID / Touch ID / Windows Hello / security key), with Admin (full control), Member (invited,
  listen-only, sees only what was shared with them), and Demo (read-only) roles. A one-click
  **[Try the demo](https://musichoarder.app/login)** account browses and plays a seeded library
  while every mutating action is denied.
- **Self-hosted** — runs on your own hardware via .NET Aspire (dev) or Docker Compose (prod),
  pulling prebuilt GHCR images. Your music never leaves your machine.
- **Installable web app** — on iPhone, Safari → Share → *Add to Home Screen*. Sign in once inside
  the installed app: it keeps its own session, separate from Safari's, and an emailed magic link
  opens in Safari rather than in the installed app — so the smooth way in is a passkey (enrol one
  under Settings → Account from Safari first) or the demo account.
- **Native Android app** — Kotlin + Compose + Media3, paired by QR code, email sign-in or passkey.
  See [android/README.md](android/README.md).

## How it works

The pipeline is a state machine over each song, run by background workers that each sweep for work
in the status they handle:

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

Confident matches flow straight through to a clean destination library; uncertain ones — and
anything the AI grader flags — surface in the **Inbox** for a human decision. The source is never
modified, and removed/missing files are soft-deleted rather than purged. The whole flow is visible
live on the **Manage → Pipeline** dashboard.

## Tech stack

| Project | Description |
|---------|-------------|
| `MusicHoarder.Api` | ASP.NET Core minimal API — endpoints, EF Core/PostgreSQL persistence, and the background services that run the pipeline, enrichment providers, sync, and downloads |
| `MusicHoarder.AppHost` | .NET Aspire AppHost — composes the API, frontend, and PostgreSQL for local dev |
| `MusicHoarder.ServiceDefaults` | Shared cross-cutting defaults (health checks, OpenTelemetry, resilient HTTP) |
| `frontend` | SvelteKit 2 + Svelte 5 + Bun — the full web app, installable on iPhone and desktop: library browser, player with Now Playing, live pipeline, review Inbox, Discover, Stats, History, and share pages |
| `android` | Native Android client (Kotlin, Jetpack Compose, Media3) — a line-by-line port of the web app's library and player, talking to the same API |

---

## Quickstart (local development)

### Prerequisites

- .NET 10 SDK
- Docker (for PostgreSQL via Aspire)
- Bun (frontend toolchain); Node.js 22 only for the semantic-release step
- `fpcalc` (`libchromaprint-tools`) for fingerprinting
- [Aspire CLI](https://aspire.dev) (optional — enables `aspire run`; otherwise use `dotnet run`)

### Run with Aspire (recommended)

```bash
aspire run
```

That's the whole thing. On first run the Aspire dashboard (at `https://localhost:17072`) prompts for
the two required values — your **source** and **destination** library paths — then provisions
PostgreSQL in Docker, launches the API, and starts the frontend. EF Core migrations are applied
automatically.

> No Aspire CLI? `dotnet run --project MusicHoarder.AppHost` does exactly the same thing. Install
> the CLI with `curl -sSL https://aspire.dev/install.sh | bash`.

**Optional — skip the prompts.** Pre-seed the paths (and any provider keys) as AppHost user-secrets
so boots are unattended and repeatable:

```bash
mkdir -p /tmp/musichoarder-source /tmp/musichoarder-dest
dotnet user-secrets set "Parameters:source-directory" "/tmp/musichoarder-source" --project MusicHoarder.AppHost
dotnet user-secrets set "Parameters:destination-directory" "/tmp/musichoarder-dest" --project MusicHoarder.AppHost
```

Drop a few audio files into your source directory, then trigger a scan from the UI (or let the
pipeline auto-run) to watch them flow through. Prefer to click around first? Open `/login` and hit
**Try the demo** for a read-only, seeded library.

### Frontend (standalone)

The AppHost runs the frontend via `.WithBun()`. To start it separately:

```bash
cd frontend && MUSICHOARDER_API_URL=http://localhost:<api-port> PORT=3000 bun run dev
```

Find the API port in the Aspire dashboard.

### Run tests

```bash
dotnet test MusicHoarder.Api.Tests/MusicHoarder.Api.Tests.csproj
```

The xUnit suite uses an in-memory EF Core provider — no PostgreSQL or Docker required.

---

## Configuration

All options live under the `MusicEnricher` section in `appsettings.json` or as environment variables
using the `MusicEnricher__` prefix.

| Key | Description | Required |
|-----|-------------|----------|
| `MusicEnricher__SourceDirectory` | Path to the source music library | Yes |
| `MusicEnricher__DestinationDirectory` | Path for the cleaned destination library | Yes |
| `MusicEnricher__AutoStartPipeline` | Auto-run the *processing* cascade (scan→fingerprint→enrich→build, enrichment backfill/retry sweep). Discovery (file indexing) always runs so the library still populates. Set `false` to require manual triggering of the heavy steps — useful in resource-constrained environments. | No (default: `true`) |
| `MusicEnricher__TempDirectory` | Scratch space for in-progress work | No (default: `/tmp/musicenricher`) |
| `MusicEnricher__AcoustIdApiKey` | AcoustID API key for fingerprint-to-MusicBrainz lookup | No (enrichment falls back to `NeedsReview` without it) |
| `MusicEnricher__AcoustIdScoreThreshold` | Minimum confidence score to accept a match (0–1) | No (default: `0.85`) |
| `MusicEnricher__SmbConcurrency` | Parallel file reads from SMB | No (default: `8`) |
| `MusicEnricher__EnrichmentWorkerConcurrency` | Parallel AcoustID lookups | No (default: `2`) |
| `ConnectionStrings__musichoarderdb` | PostgreSQL connection string | Yes (injected by Aspire in dev) |

Everything in the **Optional integrations** and several **Browse & grow** features above are off
until configured: Spotify (metadata + import) needs a registered Spotify app and the OAuth relay;
Discover/wishlist auto-download needs slskd and/or yt-dlp; Navidrome sync, instance sync, AI quality
grading, and the experimental AI lyrics transcription each have their own config sections
(`QualityGrading__`, `LyricsTranscription__`, sync/Navidrome/slskd settings). Lyrics transcription is
**hidden in the UI unless `LyricsTranscription__ApiKey` is set**. See the
[self-hosting guide](docs/SELF_HOSTING.md#optional-integrations) for the full reference.

---

## Self-host

Run MusicHoarder on your own box or NAS with Docker. The shipped `docker-compose.yml` **pulls
prebuilt images** from GHCR (`ghcr.io/jeffreyyvdb/musichoarder/{api,frontend}`) — no repo checkout
or build toolchain required. A localhost trial needs just the compose file:

```bash
mkdir musichoarder && cd musichoarder
curl -fsSLO https://raw.githubusercontent.com/Jeffreyyvdb/MusicHoarder/main/docker-compose.yml
docker compose up -d
```

The web UI is then at `http://localhost:3000` (API at `:5050`); migrations apply automatically.
Sign in as `owner@musichoarder.local` — the login link is printed to the API logs
(`docker compose logs api | grep -i magic`), no email service needed.

An `.env` is optional for the trial — every value has a working localhost default. For a real
deployment, grab [`.env.example`](.env.example) and set `POSTGRES_PASSWORD`, `MUSIC_SOURCE_PATH`,
`MUSIC_DESTINATION_PATH`, `OWNER_EMAIL`, and `PUBLIC_BASE_URL`.

The app serves plain HTTP — put it behind your own reverse proxy for TLS and point
`PUBLIC_BASE_URL` at the external URL.

**→ Full guide:** [docs/SELF_HOSTING.md](docs/SELF_HOSTING.md) — env reference, first login,
reverse proxy, Portainer/TrueNAS, optional integrations (AcoustID, Spotify, AI grading, AI lyrics
transcription, Navidrome sync, instance sync, Umami), updating, backups, build-from-source, and
troubleshooting.

---

## Deployment (CI/CD)

The whole repo (API **and** frontend together) is versioned as a single line by
[semantic-release](https://github.com/semantic-release/semantic-release). Deployment is
release-driven — only a semantic-release version publishes images and redeploys; routine
`chore`/`docs`/`refactor` pushes do not.

```
Push to main
     ↓
ci.yml          — dotnet build+test, frontend lint/check/test (also on PRs)
     ↓
release.yml     — semantic-release analyzes Conventional Commits; if warranted,
                  cuts a `vX.Y.Z` tag + GitHub Release, then dispatches ↓
     ↓
aspire-deploy.yml — builds the API + frontend images, pushes them to GHCR, semver-tags
                  them, then calls the Dokploy API to redeploy the Compose stack
```

The version bump follows the commit prefix — `fix:` → patch, `feat:` → minor,
`feat!:`/`BREAKING CHANGE:` → major; `chore`/`docs`/`refactor`/`test`/`style` cut no release. The
[Releases page](https://github.com/Jeffreyyvdb/MusicHoarder/releases) is the canonical changelog.

### Zero-downtime deploys

The prod compose (`MusicHoarder.AppHost/aspire-output/docker-compose.yaml`) declares a Swarm
`deploy.update_config` (`order: start-first`) plus Docker `healthcheck`s on the `api` and `frontend`
services. To get zero-downtime rollouts you must run the stack as a **Docker Stack (Swarm)** — in
Dokploy set the Compose service's **Compose Type to "Docker Stack"**. Swarm then starts the new task,
waits for its healthcheck to pass, and only then removes the old one, so there is no 502 window.
(Because Swarm ignores `pull_policy`, the stack deploy must run with `--resolve-image always` so the
unchanged `:latest` reference still re-pulls the newest digest.)

These keys are inert under plain `docker compose up` (Compose ignores `deploy.update_config`), so the
[Self-host](#self-host) path above is unaffected and keeps its current stop-then-start behavior —
self-hosters who want zero-downtime can likewise run their stack via `docker stack deploy`.

Operational detail — PR preview environments, the Spotify OAuth relay, and the Dokploy setup — lives
in the heavily-commented workflow files under [`.github/workflows/`](.github/workflows).

---

## Pipeline notes

### fpcalc + AcoustID

`fpcalc` (Chromaprint) is included in the Docker image via `libchromaprint-tools`. Without
`MusicEnricher__AcoustIdApiKey`, enrichment sets songs to `NeedsReview` rather than `Matched`, and
the Library Builder skips them.

### Library views

The **Listen** views (Overview / Albums / Artists / Tracks) show what's in your destination
library; the **Manage → Pipeline** dashboard shows the full source-to-destination flow, and the
**Inbox** holds anything awaiting a human decision.

### EF Core migrations

Migrations are applied automatically on container startup in all environments. No manual migration
steps are required after a new image is deployed.

---

## Contributing & license

- **Contributing:** see [CONTRIBUTING.md](CONTRIBUTING.md). Commit messages follow
  [Conventional Commits](https://www.conventionalcommits.org/) — they drive the shared
  semantic-release version.
- **Security:** report vulnerabilities per [SECURITY.md](SECURITY.md).
- **License:** [MIT](LICENSE).
