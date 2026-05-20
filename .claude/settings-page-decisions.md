# Settings page — implementation decisions

This doc captures the non-obvious calls I made while turning the
[claude.ai/design MusicHoarder bundle](https://api.anthropic.com/v1/design/h/oDvxvuPZOdkgiqpFXxetDw)
into a real settings page. Review these before merging the PR.

## Source of truth

- Design bundle: `oDvxvuPZOdkgiqpFXxetDw` (downloaded and extracted in
  `/tmp/design-extracted/musichoarder/`).
- Primary file from the bundle: `project/src/SettingsPage.jsx` — a React
  prototype with 6 sections (Account, Paths & storage, Spotify, Enrichment
  sources, Pipeline, Data & resets) and a left-rail nav.
- The chat transcripts (`chats/chat2.md`) explicitly say the user "doesn't
  want auth/landing" if we go local-tool, and "wants Spotify creds + reset
  buttons + purge".

## Scope cuts vs. the design

| Design section | What I built | Why |
| --- | --- | --- |
| Account | **Dropped** | MusicHoarder is single-instance self-hosted; there is no auth model. The chat transcript explicitly flagged this as a fork in the road and the existing codebase commits to "local tool". Rebuilding multi-tenant auth is far out of scope for this PR. |
| Paths & storage | **Read-only** | `MusicEnricherOptions.SourceDirectory` / `DestinationDirectory` come from AppHost parameters (`Parameters:source-directory` / `Parameters:destination-directory`) and are validated on startup with `ValidateOnStart()`. Allowing them to be mutated at runtime would bypass that gate and require restarting the pipeline. Surfaced read-only with a note pointing back to user-secrets. |
| Spotify | **Kept + enhanced** | Reused the existing credentials form (already shipped), and added the design's redirect-URI display (with Copy button) + scopes chips. |
| Enrichment sources | **Built (4 providers)** | The design lists generic "sources"; I bound them to the four real providers (`AcoustID`, `SpotifyAPI`, `MusicBrainzWeb`, `Tracker`) the API actually has via `IEnrichmentProvider`. |
| Pipeline | **Built** | Confidence thresholds + worker concurrency sliders. The "skip duplicates" / "prefer higher bitrate" / "move source files" toggles in the design are aspirational — they don't map to existing pipeline behaviour, so I left them out rather than fake them. |
| Data & resets | **Kept** | The existing page already had post-fingerprint purge and full purge with `PurgeStatusBanner`. The design adds "Reset fingerprint cache", "Reset decisions", "Re-fetch artwork" buttons — these don't map to currently-implemented endpoints, so I left them as future work to avoid wiring buttons to nothing. |

## Backend architecture

### New persisted-overlay pattern: `RuntimeSettings`

The user-tunable values in `MusicEnricherOptions` are bound from
configuration at startup via `BindConfiguration` + `ValidateOnStart` →
`IOptions<MusicEnricherOptions>` is effectively immutable for the process
lifetime. To let the UI mutate a subset of those at runtime I added:

- `Persistence/RuntimeSettings.cs` — singleton DB row with **nullable**
  columns for each overridable field. Null means "fall back to config".
- `Settings/IRuntimeSettingsService.cs` + `RuntimeSettingsService.cs` —
  reads/writes the row, merges with `IOptionsMonitor<MusicEnricherOptions>.CurrentValue`,
  caches the result in-process with a `SemaphoreSlim`, invalidates on write.
- `Endpoints/SettingsEndpoints.cs` — `GET /api/settings` returns the
  effective view (paths from `IOptions`, providers/pipeline from runtime
  service, Spotify metadata from `IOptions<SpotifyOptions>`). `PUT /api/settings`
  partially updates the overlay.
- EF migration: `20260519212100_AddRuntimeSettings`.

### Live vs. restart-required

`EnrichmentOrchestrator` was refactored to consult `IRuntimeSettingsService`
on every call, so:

- **Live (next enrichment cycle):** provider on/off toggles, confidence
  thresholds — `EnrichmentOrchestrator.IsProviderEnabled` and
  `GetEnabledProviderEnumsAsync` now read from the runtime service.
- **Restart-required:** worker concurrency (`EnrichmentWorkerConcurrency`,
  `LibraryBuilderWorkerConcurrency`). The `SemaphoreSlim` instances are
  constructed in the background services' constructors from
  `IOptions<MusicEnricherOptions>`; resizing them at runtime is risky
  (in-flight workers, fairness) and not worth doing for this PR. The PUT
  endpoint persists the new value and the UI warns the user.

### Interface breaking change

`IEnrichmentOrchestrator.GetEnabledProviderEnums()` (sync) became
`GetEnabledProviderEnumsAsync(CancellationToken)`. The only call site was
`EnrichmentBackgroundService.RefreshStaleStatusesAsync` (already async) and
the test stub in `EnrichmentRetrySweepTests`. Both updated.

The test orchestrator factories in `EnrichmentOrchestratorTests` now pass a
new `TestRuntimeSettingsService` that returns the `MusicEnricherOptions`
verbatim — preserving every existing test's behaviour.

## Frontend architecture

- `frontend/src/routes/(app)/settings/+page.svelte` was rewritten from a
  single-column "Spotify + Danger zone" page into a 5-tab layout using the
  existing shadcn-svelte `Tabs` primitive (which already includes the
  `min-h-0` flex fix called out in `CLAUDE.md`).
- API helpers added to `frontend/src/lib/api-client.ts`: `fetchSettings`,
  `updateSettings`, plus TypeScript interfaces mirroring the C# DTOs. Demo
  mode (`PUBLIC_DEMO_MODE=true`) returns canned data so the page works
  offline.
- The toggle widget is a hand-rolled `peer-checked` Tailwind switch — the
  shadcn-svelte preset doesn't ship a switch primitive and I didn't want to
  pull in another bits-ui component for one use site.
- Tabs default to **Paths** (not "Account" like the design) because Paths is
  the most useful first impression and Account doesn't exist here.

## What I left for follow-ups

- Live concurrency resize (would need to change the background-service
  constructors to read the semaphore size from the runtime service on each
  cycle and reallocate when the limit changes).
- Per-provider settings drill-in (the design has a small cog button per
  provider — not wired here).
- "Reset fingerprint cache", "Reset decisions", "Re-fetch artwork" buttons
  in Data & resets — would need three new endpoints + UI surface; out of
  scope for the first cut.
- Storage stats card in Data & resets — `DashboardEndpoints.GetStats`
  already exposes track/storage/duration data and could be folded in.
