---
name: mh-debug
description: Debug a running MusicHoarder instance (prod or dev) over its existing HTTP API — no MCP server needed. Use when asked to investigate why a song didn't enrich/match, why lyrics show blank or wrong, why a track never reached the destination library, pipeline/job state, on-disk vs DB tag mismatches, or to inspect any single song's full state. Triggers on "debug production", "why didn't this song match", "lyrics aren't showing", "inspect song state", "check the pipeline".
---

# Debugging MusicHoarder over the HTTP API

MusicHoarder already exposes a rich **read-only** debugging surface. You do **not** need an
MCP server — it would only wrap these same endpoints. Use `curl` (or `WebFetch` in dev)
against the running instance.

## 1. Where to point and how to authenticate

All backend calls go through the frontend same-origin proxy `/api/mh/<api-path>` (see
`frontend/src/routes/api/mh/[...path]/+server.ts`), which forwards every header — including
`Cookie` — to the API. So target the **public site origin** and prefix API paths with
`/api/mh/`.

Auth is the `mh_session` httpOnly cookie (magic-link / passkey — **no API key or bearer
token**; `AuthOptions.CookieName`, `AuthEndpoints.cs`). The debug + owner-only routes are
gated by `RequireOwner()` (`DebugEndpoints.cs:33`), so you need the **owner's** session.

Recipe (never commit the cookie value — keep it in an env var / shell only):

```bash
export MH_BASE_URL="https://<your-instance>"          # public site origin
export MH_COOKIE="mh_session=<paste from an owner's logged-in browser>"
mh() { curl -fsS --cookie "$MH_COOKIE" "$MH_BASE_URL/api/mh/$1"; }

mh "api/auth/me" | jq          # sanity check: confirms the session + that you're owner
```

To grab the cookie: log in to the site in a browser as the owner → DevTools → Application →
Cookies → copy the `mh_session` value. It's httpOnly, so it must come from the browser, not JS.

## 2. The debugging catalog (read-only)

| What you want | Call (via `mh "<path>"`) |
| --- | --- |
| Find problem songs | `songs?enrichmentStatus=NeedsReview` or `=Failed` |
| **Full state of one song** | `songs/{id}/enrichment-detail` — source vs current metadata, **every** `SongProviderAttempt` (provider, status, query, matched candidate + confidence), errors, change history |
| Aggregate pipeline state | `api/debug/pipeline-summary` — counts per stage/status, provider outcomes, recent errors |
| On-disk vs DB tags | `api/debug/songs/{id}/tags` — live TagLib dump next to persisted metadata |
| **Exact lyric strings** | `api/tracks/{id}/lyrics` — `synced` / `plain` / `isInstrumental` (see lyrics playbook below) |
| Library stats | `stats` |
| Live job state | `api/enrichment/status`; SSE stream `api/enrichment/progress` |
| Duplicates | `api/library/duplicates` |
| Source/dest dir trees | `api/debug/source-tree`, `api/debug/destination-tree` |
| Full annotated catalog (dev only) | `openapi/v1.json`, or the `/scalar` UI in a browser |

Pipe through `jq` for readability, e.g. `mh "songs/123/enrichment-detail" | jq '.providerAttempts'`.

## 3. Playbook: "lyrics tab is blank / says synced but shows nothing"

1. `mh "api/tracks/{id}/lyrics" | jq` — inspect `synced` and `plain`.
   - `synced` non-empty but the panel was blank → a parser/render bug, **not** missing data.
     The frontend parser lives at `frontend/src/lib/lyrics/parse-lrc.ts` (tolerant of CRLF,
     `mm:ss:cc`, multiple inline timestamps); `LyricsPanel.svelte` falls back to raw/plain
     text when no timestamps parse. Reproduce with a `parse-lrc` unit test.
   - both `null`/empty but `lyricsStatus: "Fetched"` → DB inconsistency; check
     `ApplyLyricsResult` (`SongMetadata.cs`) and the LRCLIB fetch.
   - `isInstrumental: true` → expected: no lyrics.

## 4. Playbook: "this song never matched / never reached the library"

1. `mh "songs/{id}/enrichment-detail" | jq` — read each provider attempt's status + error.
2. No fingerprint? `mh "api/debug/songs/{id}/tags"` and check `DurationSeconds`/`Fingerprint`
   — missing `fpcalc` leaves both null, so AcoustID skips and the builder never promotes it.
3. `mh "api/debug/pipeline-summary" | jq '.recentErrors'` for stage-level failures.

## 5. Known gaps (don't rediscover these)

The HTTP API covers ~85% of debugging. It currently **cannot**: bulk-query provider attempts
across songs, stream per-song events (only a global progress snapshot), or audit config
changes. If any of these becomes painful, the cheap fix is a couple of owner-gated read-only
`GET` endpoints alongside `DebugEndpoints.cs` — **not** an MCP server.
