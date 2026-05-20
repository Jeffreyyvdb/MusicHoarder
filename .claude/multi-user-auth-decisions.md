# Multi-user auth — implementation decisions

Stacks on top of PR #6 (`feat/settings-page-from-design`). Full plan lives at
`~/.claude/plans/effervescent-snuggling-candy.md`.

## Big picture

- **Identity model:** `User` (Owner | Demo roles), `Session` (cookie-backed,
  revocable), `MagicLinkToken` (single-use, 15-min TTL, SHA-256 stored).
- **Magic-link delivery:** Resend HTTPS API when configured; otherwise falls
  back to logging the link via `ConsoleMagicLinkSender`. In Development the
  request endpoint also returns the link in the JSON response so devs can
  click without email.
- **Session cookie:** signed via `IDataProtectionProvider`. Keys persisted to
  `/data/dpkeys` (mounted Docker volume) so cookies survive container restarts.
- **Auth gate:** two middlewares — `AuthenticationMiddleware` resolves the
  cookie → user, `RequireAuthMiddleware` rejects unauthenticated requests to
  anything under `/api/*` except `/api/auth/*` and health endpoints.
- **Per-user data isolation:** every `SongMetadata`, `SpotifySettings`,
  `SpotifyTrackLibraryMatch` row has `OwnerUserId`. EF global query filters
  scope reads to the current user; background services use
  `IgnoreQueryFilters()` explicitly with the owner GUID.
- **Mutating endpoints** carry a `.RequireOwner()` filter so the demo user
  can browse but not change anything.

## Decisions that warrant review

### 1. Disabled EF DbContext pooling (`Program.cs`)

The previous registration was `builder.AddNpgsqlDbContext<MusicHoarderDbContext>(...)`,
which uses pooled contexts. Pooled contexts require a single
`(DbContextOptions)` constructor. To inject `ICurrentUserAccessor` for query
filters we need a second constructor.

**Switched to** `services.AddDbContext<MusicHoarderDbContext>(...)` (non-pooled)
plus `builder.EnrichNpgsqlDbContext<MusicHoarderDbContext>()` to apply
Aspire's OpenTelemetry + health-check wiring on top.

Trade-off: small per-request DbContext allocation cost. Acceptable for the
2-user scale and not measurable on a homelab box.

### 2. `UserAwareModelCacheKeyFactory`

EF caches the compiled model once per DbContext type. With per-user query
filters baked into `OnModelCreating`, two contexts with different users
would otherwise share one compiled model with the wrong filter value.

The factory varies the cache key by the captured user id (or `"anon"` for
design-time / background-service contexts). At our scale this caches at most
three compiled models (Owner / Demo / anon). Documented at
`MusicHoarder.Api/Persistence/UserAwareModelCacheKeyFactory.cs`.

### 3. Background services bypass the query filter explicitly

Hosted services have no `HttpContext`, so `ICurrentUserAccessor.UserId` is
`Guid.Empty` and the query filter would return nothing. Rather than route
background services through a "system user" abstraction, every
background-service query uses `.IgnoreQueryFilters()` and an explicit
`.Where(s => s.OwnerUserId == ownerLookup.OwnerUserId)` for tenanted entities.
Grep-friendly and makes the tenancy boundary visible at each call site.

Touched: `IndexService`, `FingerprintBackgroundService`,
`EnrichmentBackgroundService`, `EnrichmentOrchestrator`,
`LibraryBuilderBackgroundService`, `LibraryBuilderService`,
`DuplicateDetectionService`, `SpotifyOAuthService`, `SpotifyApiService`,
`SpotifyLibraryComparisonService`, `SpotifyApiEnrichmentProvider`.

### 4. `SpotifySettings` is per-user in schema but **owner-only at runtime**

Adding `OwnerUserId` to `SpotifySettings` was straightforward. Going fully
per-user (so the demo could have its own Spotify connection) would require
refactoring every Spotify service to take a user context — large surface,
not justified for the 2-user case where the demo never needs its own
Spotify.

Compromise: `SpotifySettings` has the FK in place, but `SpotifyOAuthService`
/ `SpotifyApiService` / `SpotifyLibraryComparisonService` pin to
`ownerLookup.OwnerUserId`. The `/api/spotify/connect`, `/credentials`, and
`/disconnect` endpoints are `.RequireOwner()`. Demo users get
`spotify_not_connected` from read endpoints (which is correct — they have no
Spotify state).

### 5. Demo seeder via hosted service, not `HasData`

The seeder is a runtime `IHostedService` (`DemoSeederHostedService`) reading
20 hand-curated tracks from an embedded JSON resource
(`Auth/Resources/demo-songs.json`). `HasData` for ~20 rows with realistic
`Artist`/`Album`/`Title`/`Lyrics` values would require baking literal
`DateTime` values into a migration that future devs can't edit; the runtime
seeder is one file you own.

The seeder is **idempotent** — checks for existing demo songs before
inserting. Also updates owner/demo emails on every boot from
`Auth:OwnerEmail` / `Auth:DemoUserEmail` so you can change your email
without a migration.

### 6. `IsSynthetic` flag on `SongMetadata`

Demo rows have `IsSynthetic = true`. Scanner, FingerprintBackgroundService,
EnrichmentBackgroundService, and LibraryBuilder all filter them out — they
have no real file on disk, so the pipeline shouldn't try to operate on them.
Demo data is pre-seeded with `EnrichmentStatus = Matched` and
`LibraryBuildStatus = Done` so even without `IsSynthetic` checks it'd be
mostly-inert; the flag is a defense in depth.

### 7. Magic-link callback is a SvelteKit `+server.ts`, not an API redirect

The proxy at `frontend/src/routes/api/mh/[...path]/+server.ts` uses
`redirect: 'follow'`, which would swallow `Set-Cookie` from an API-side
redirect. So `/auth/callback` is its own SvelteKit endpoint that POSTs the
token to the API and forwards the API's `Set-Cookie` to the browser, then
303s into `/app/overview`. Cookie ends up on the browser correctly.

### 8. `(app)` group auth gate is `+layout.server.ts`

The `(app)/+layout.ts` sets `ssr = false`, but server `load` functions run
regardless — that flag only disables component-side SSR. The new
`+layout.server.ts` calls `/api/auth/me` on each app navigation and throws
`redirect(303, '/login')` on 401. `PUBLIC_DEMO_MODE=true` short-circuits to
a mock demo user so the offline demo path still works.

### 9. DataProtection keys volume

`AddDataProtection().PersistKeysToFileSystem("/data/dpkeys")` falls back to
`AppContext.BaseDirectory/dpkeys` when the configured path isn't writable
(first-boot dev experience). In production the docker-compose mounts a
named volume `musichoarder-dpkeys` on `/data/dpkeys`. **Without this volume
mount, every container restart invalidates every cookie.**

### 10. `Frontend__PublicBaseUrl` carries the auth callback target

The API's magic-link email contains `<frontend-base>/auth/callback?token=…`.
`AppHost.cs` already wires `Frontend__PublicBaseUrl = frontend.GetEndpoint("https")`,
so the dev experience just works. In production, `docker-compose.yml`
requires `PUBLIC_BASE_URL` — bad config there means the email links go
nowhere.

### 11. Invite-only — no signup endpoint

The two users are created at migration time via `HasData` with stable GUIDs
(`WellKnownUsers.OwnerId` / `DemoId`). There is **no signup flow** — anyone
who types an unknown email gets a `200 OK` (to avoid enumeration) but no
email is sent. The `Resend` sender additionally skips sending for demo
users (`Role == Demo`) so a stranger typing the demo email can't burn
through your Resend quota.

### 12. The user-secrets / .env.example are updated, but you'll need to set them

```bash
dotnet user-secrets set "Parameters:owner-email"         "you@example.com"  --project MusicHoarder.AppHost
dotnet user-secrets set "Parameters:demo-user-email"     "demo@example.com" --project MusicHoarder.AppHost
dotnet user-secrets set "Parameters:resend-api-key"      "re_..."            --project MusicHoarder.AppHost
dotnet user-secrets set "Parameters:resend-from-address" "auth@example.com"  --project MusicHoarder.AppHost
```

For docker-compose deployment, set `OWNER_EMAIL` and `PUBLIC_BASE_URL` in
`.env` (they're required); the rest have defaults.

## Out of scope (deliberately punted)

- Per-user `RuntimeSettings` (pipeline tuning is sysadmin work; gated on
  Owner already).
- Per-user lyrics-cache dedup (already per-song → per-user transitively).
- Audit log of who-did-what.
- Password fallback / OAuth providers.
- Email template HTML / branding.
- Account self-service for changing display name or email (do by hand for two
  users).
- True per-user paths (Scanner / LibraryBuilder still use the global
  `MusicEnricher` paths as "the owner's paths"; demo has no paths because its
  data is seeded).
