## MusicHoarder Frontend

SvelteKit + Bun app. Backend traffic is routed through the same-origin proxy at `/api/mh/[...path]` to the .NET API so the browser never needs CORS.

## Run with Aspire (recommended)

From the repository root:

```bash
dotnet run --project MusicHoarder.AppHost
```

Aspire starts:
- `MusicHoarder.Api` (backend API)
- `frontend` (SvelteKit via Bun — `AddViteApp(...).WithBun()`)
- PostgreSQL

AppHost also sets `Frontend__PublicBaseUrl` on the API so that after Spotify OAuth (callback hits the API), users are redirected back to the SvelteKit app instead of seeing raw JSON.

## Frontend API routing

The frontend proxies backend requests through:
- `/api/mh/[...path]` → `.NET API`

The proxy (`src/routes/api/mh/[...path]/+server.ts`) resolves the backend base URL in this order:
1. `MUSICHOARDER_API_URL`
2. Aspire-discovery variables like `services__musichoarder-api__http__0`
3. Fallback: `http://localhost:5107`

## Demo access

There is no frontend-only demo. To showcase the product, use the **API-backed demo
account**: the `/login` page's "Try the demo" button calls `POST /api/auth/demo-login`,
which starts a real read-only session backed by a seeded `Demo` user in the database.
All data comes from the backend API — `MUSICHOARDER_API_URL` (or Aspire discovery) is
always required. API failures are surfaced as UI errors.

## Analytics (optional)

Umami analytics are loaded when `PUBLIC_UMAMI_SRC` and `PUBLIC_UMAMI_WEBSITE_ID` are set. Session replay is optional via `PUBLIC_UMAMI_RECORDER_SRC`. All `PUBLIC_UMAMI_*` vars are read at runtime (`$env/dynamic/public`), so they can be set in the container env (e.g. docker-compose / Dokploy) without a rebuild.

Load the tracker straight from the Umami host — `PUBLIC_UMAMI_SRC` is the full tracker URL ending in `/script.js` (not the dashboard root), and the tracker posts events to that same origin:

```bash
PUBLIC_UMAMI_SRC="https://your-umami-host/script.js"
PUBLIC_UMAMI_WEBSITE_ID="<your-website-id>"
PUBLIC_UMAMI_RECORDER_SRC="https://your-umami-host/recorder.js"   # only if using the recorder
```

The tracker tag sets `data-performance="true"`, so Core Web Vitals are collected on Umami server **v3.1.0+** (silently ignored by older servers).

## Local frontend-only run (optional)

If you run the frontend without AppHost:

```bash
bun install
bun run dev
```

Make sure the API is reachable at `MUSICHOARDER_API_URL` or at the localhost fallback above.

## Scripts

- `bun run dev` — Vite dev server
- `bun run build` — production build (adapter-node → `build/index.js`)
- `bun run start:prod` — serve the production build with Bun
- `bun run check` — svelte-check + TypeScript
- `bun run lint` — ESLint
- `bun run format` — Prettier
- `bun run release:dry` — dry-run semantic-release

## Spotify OAuth (non-Aspire)

Spotify's redirect URI must point at the **API** (e.g. `http://localhost:5107/api/spotify/callback`). For the API to send users back to this app after login, configure **`Frontend:PublicBaseUrl`** on the API (env var `Frontend__PublicBaseUrl`) to this app's public origin, e.g. `http://localhost:3000`. `appsettings.Development.json` in the API project includes a localhost default for that.

## Migration note (Next.js → SvelteKit)

This app was ported from Next.js 16 + React 19 to SvelteKit 2 + Svelte 5 (runes) + Bun. The (app) route group is rendered client-only (`ssr = false`) because the audio player and demo-mode logic read browser-only state. The landing route (`/`) keeps SSR. The port is complete: all (app) pages (overview SSE+triggers, file browser, review queue, settings, spotify, artist detail) are fully implemented alongside the shared shell, sidebar, header, mini-player, theme toggle, demo banner, analytics, API proxy, health endpoint, player store, and `api-client.ts`.
