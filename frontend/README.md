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

## Frontend API routing

The frontend proxies backend requests through:
- `/api/mh/[...path]` -> `.NET API`

The proxy resolves backend base URL in this order:
1. `MUSICHOARDER_API_URL`
2. Aspire-discovery variables like `services__musichoarder-api__http__0`
3. Fallback: `http://localhost:5107`

## Local frontend-only run (optional)

If you run frontend without AppHost:

```bash
pnpm dev
```

Make sure the API is reachable at `MUSICHOARDER_API_URL` or at the localhost fallback above.
