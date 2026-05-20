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

## Demo vs production data mode

Use `PUBLIC_DEMO_MODE` (SvelteKit exposes any `PUBLIC_*` env to the client) to control whether API-backed UI data comes from fake data or the backend API:

- `PUBLIC_DEMO_MODE=true` → **Demo mode** (mock data)
- `PUBLIC_DEMO_MODE=false` (or unset) → **Production mode** (real API only)

In production mode, API failures are surfaced as UI errors and do not fall back to mock data.

The **`/spotify`** page participates in demo mode: with `PUBLIC_DEMO_MODE=true` you do **not** need `MUSICHOARDER_API_URL` for that route. The UI shows a connected state with sample playlists and liked songs (no real Spotify OAuth). For a demo-only deployment that just showcases the product, set `PUBLIC_DEMO_MODE=true` and omit the API URL unless other pages require it.

```bash
# Demo deployment
PUBLIC_DEMO_MODE=true

# Production deployment
PUBLIC_DEMO_MODE=false
MUSICHOARDER_API_URL=https://your-api-host
```

## Analytics (optional)

Umami analytics are loaded when `PUBLIC_UMAMI_SRC` and `PUBLIC_UMAMI_WEBSITE_ID` are set. Session replay is optional via `PUBLIC_UMAMI_RECORDER_SRC`.

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
