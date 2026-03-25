## MusicHoarder Frontend

This Next.js app is wired to the .NET API through an internal Next.js proxy route so browser requests stay same-origin and do not require CORS.

## Run with Aspire (recommended)

From the repository root:

```bash
dotnet run --project MusicHoarder.AppHost
```

Aspire starts:
- `MusicHoarder.Api` (backend API)
- `frontend` (Next.js app via pnpm)
- PostgreSQL

AppHost also sets `Frontend__PublicBaseUrl` on the API to the frontend HTTP endpoint so that after Spotify OAuth (callback hits the API), users are redirected back to the Next.js app instead of seeing raw JSON.

## Frontend API routing

The frontend proxies backend requests through:
- `/api/mh/[...path]` -> `.NET API`

The proxy resolves backend base URL in this order:
1. `MUSICHOARDER_API_URL`
2. Aspire-discovery variables like `services__musichoarder-api__http__0`
3. Fallback: `http://localhost:5107`

## Demo vs Production data mode

Use `NEXT_PUBLIC_DEMO_MODE` to control whether API-backed frontend data is served from fake data or the backend API:

- `NEXT_PUBLIC_DEMO_MODE=true` -> **Demo mode** (mock data)
- `NEXT_PUBLIC_DEMO_MODE=false` (or unset) -> **Production mode** (real API only)

In production mode, API failures are surfaced as UI errors and do not fall back to mock data.

Examples:

```bash
# Demo deployment
NEXT_PUBLIC_DEMO_MODE=true
```

```bash
# Production deployment
NEXT_PUBLIC_DEMO_MODE=false
MUSICHOARDER_API_URL=https://your-api-host
```

## Local frontend-only run (optional)

If you run frontend without AppHost:

```bash
pnpm dev
```

Make sure the API is reachable at `MUSICHOARDER_API_URL` or at the localhost fallback above.

## Spotify OAuth (non-Aspire)

Spotify’s redirect URI must point at the **API** (e.g. `http://localhost:5107/api/spotify/callback`). For the API to send users back to this app after login, configure **`Frontend:PublicBaseUrl`** on the API (or environment variable `Frontend__PublicBaseUrl`) to this app’s public origin, e.g. `http://localhost:3000`. `appsettings.Development.json` in the API project includes a localhost default for that.
