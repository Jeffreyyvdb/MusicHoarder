# Performance suite

Measures how fast MusicHoarder is for the person using it, and says why a number is what it is.
It starts a local copy of the stack with a generated library, times the API and the web app, and
writes a report you can compare against the next run.

```bash
cd perf
bun install
bun perf all --label baseline          # start, measure, report, stop  (about 10 minutes)

# …change something…

bun perf all --label my-change
bun perf compare baseline my-change    # what got faster, what got slower
```

Reports land in `perf/results/<time>_<label>/`: `report.md` (also printed), `report.html` (open a
row for its detail) and `result.json` (what `compare` reads).

## What you need

Docker, the .NET SDK, Bun, Node 22.18 or newer and `ffmpeg` (all already needed to work on the
project; the browser scenarios run under Node because Playwright is not reliable under Bun), plus a
Chromium for the browser part: `bunx playwright install chromium`, or point `PERF_CHROMIUM` at any
Chrome or Chromium binary.

## Checking a deployed instance

`src/web/preview-checks.ts` is not a measurement: it checks a running instance (a PR preview, or
production) for the web-platform hardening — security headers and the report-only CSP with its
report endpoint, the landing hero's loading, passkey autofill on the login field, focus outlines
under forced colors, and, signed in as the demo account, one `<main>` and native progress bars on
the app pages, with no CSP violations or script errors anywhere. It prints one PASS/FAIL line per
check and exits 1 when any fails. The PR preview workflow runs it after every preview deploy.

```bash
cd perf && bun install
node src/web/preview-checks.ts https://pr-123.preview.example.com
```

## What it measures

**API** (`src/api/endpoints.ts`): each request in the order a listener meets them — opening the
app, browsing, playing a track, then the admin pages. For every one:

| Column | Meaning |
|---|---|
| Median, p95 | Time to the last byte, one caller, called directly |
| Size, Gzipped | The body as sent, and what a compressing proxy would put on the wire |
| SQL | Statements per request and time in Postgres (from `pg_stat_statements`) — a high count is an N+1 |
| CPU | CPU time the API process spent per request |
| ×8 p95 | The same request with eight callers at once |

Large list responses also get a **bytes by field** breakdown, and a few requests are repeated
through the frontend's `/api/mh` proxy to show what that hop adds.

The two **unchanged** rows are the library lists asked for again with the `ETag` they came with,
the way the web app revalidates on a page visit (`revalidate: true`): the row is the 304, and it
fails if anything else comes back. They are also the only place that statement runs outside
production, since the API's tests use an in-memory database.

**Web app** (`src/web/bench.ts`), in headless Chromium, once per profile:

- **Cold starts**: Overview, Albums, Tracks, Artists with an empty cache, timed until the page shows
  its real content ("usable"), with first paint, main-thread blocking, bytes transferred and the
  slowest API calls behind it.
- **Interactions**: moving between pages inside the app, opening an album, typing in the track
  search (input to next frame), scrolling the track list (frame times), and pressing play until
  sound starts.

Profiles: `desktop` (this machine, no limits), `phone` (390×844, CPU 4× slower, 20 Mbit/s) and
`phone-4g` (9 Mbit/s, 85 ms). `desktop,phone` run by default; pick with `--profiles`.

Every row has a **budget**, set next to its definition. A run lists what is over budget first, and
`--strict` makes that an exit code.

## Commands

```
bun perf all                 start a stack, measure API and web, report, stop
bun perf up                  start the stack and leave it running (API :5599, web :5598)
bun perf measure             measure API and web against the running stack
bun perf api | web           one of the two
bun perf down [--purge]      stop it (--purge also deletes the seeded database and files)
bun perf compare <a> <b>     compare two runs, by label or folder
bun perf list                runs so far
bun perf help                every option
bun run test                 the suite's own unit tests (lock, memory check, names)
```

`up` and `all` take the machine's stack lock and check free memory first (see **One stack per
machine** below): `--no-wait` fails instead of waiting for the lock, `--force` starts despite low
memory, and `--scope` runs the stack in systemd scopes.

While working on one thing, keep the stack up and re-measure only that:

```bash
bun perf up
bun perf api --only "song list" --label try-1
bun perf web --only "open tracks" --profiles phone --runs 5 --label try-1
```

When a browser number is high and the API calls behind it are not, the time is going into
JavaScript. `--cpu-profile` saves one profile per interaction next to the report (`phone-search.cpuprofile`,
`desktop-play.cpuprofile`, …); load one in Chrome DevTools (Performance → Load profile) to see which functions it went to. Profiling slows
the page, so leave it off for runs you intend to compare.

`up` rebuilds the API and the frontend, so the stack always runs the code in your checkout;
`--no-build` skips that.

`--size` picks the library: `tiny` 300 songs, `small` 2,000, `medium` 10,000 (default), `large`
30,000. Each size has its own database, seeded once and reused.

## One stack per machine

A stack is a Postgres, a Release API, the Node frontend and, while the browser scenarios run, a
headless Chromium: about 1.5 GB for `tiny`, close to 3 GB for `medium` (see **Memory** below). Several of them
at once can run a machine out of memory, and since every process lives in the cgroup of whatever
started it, the out-of-memory killer (systemd-oomd kills whole cgroups) then takes the terminal,
editor or agent session that started the run along with it. So:

- **`up` and `all` take a machine-wide lock** before they start anything:
  `~/.cache/musichoarder-perf/stack.lock` (under `$XDG_CACHE_HOME` when set), in the shared cache
  folder whatever `PERF_WORK_DIR` says. When another stack holds it they wait, printing who holds it
  (its command or pids, library size, work dir and start time); `--no-wait` fails at once instead.
  `all` holds the lock until it has stopped the stack, `up` until **`down`**: the stack stays running
  after `up` returns, and so does the lock, so always `down` a stack you brought `up`. A lock whose
  processes have all gone (a crash, a reboot) is stale, and the next run removes it. Ctrl-C during
  `all` stops the stack and releases the lock. `measure`, `api` and `web` use a running stack and
  take no lock. `up` in the work dir whose stack is already up restarts that stack, as before.
- **The free-memory check**: `up` and `all` read the memory available (`MemAvailable` from
  `/proc/meminfo` on Linux, free memory elsewhere) and refuse to start below what the chosen size
  needs. `--force` starts anyway.
- **Every work dir has its own database**: the Docker container and volume are named after the work
  dir. The default one keeps the plain names (`mh-perf-postgres-medium`, `mh-perf-pgdata-medium`),
  so the volume it already seeded is reused; any other `PERF_WORK_DIR` adds a short hash of its path
  (`mh-perf-postgres-medium-1a2b3c4d`), or `PERF_INSTANCE=<name>` names it outright. Ports are not
  derived: `PERF_PG_PORT`, `PERF_API_PORT` and `PERF_WEB_PORT` pick them (defaults 55432, 5599,
  5598), and `up` fails early naming the port when something else holds one. Changing
  `PERF_PG_PORT` for an existing database re-creates its container on the same volume.

Several sessions sharing one machine (separate worktrees, say) should each set their own work dir
and ports, for every perf command they run, and let the lock queue them:

```bash
export PERF_WORK_DIR=~/.cache/musichoarder-perf-b PERF_PG_PORT=55433 PERF_API_PORT=5601 PERF_WEB_PORT=5602
bun perf all --size small --label try-1     # waits while another session's stack runs
```

### Containing a run (Linux with systemd)

With `--scope` (or `PERF_SYSTEMD_SCOPE=1`), the API, the frontend and the browser run (Node and its
Chromium) each start in a transient systemd user scope (`systemd-run --user --scope`) in
`app-mhperf.slice` (override with `PERF_SYSTEMD_SLICE`), with `MemoryHigh` and `MemoryMax` set from
the size's measured peak. If memory runs out, the limit or systemd-oomd then kills the perf stack
rather than the session that started it: the kernel kills the part over its limit and systemd
stops that part's scope (`journalctl --user` says `Failed with result 'oom-kill'`). The rest of
the stack keeps running and holding the lock, and measuring it says the stack is down;
`bun perf down` clears it up. `systemd-cgls --user-unit app-mhperf.slice` shows the
scopes while a stack runs. Where `systemd-run --user` is not available (macOS, most CI) the flag
prints one line and changes nothing. Pass it to `web` and `measure` as well, or export the variable,
since the browser starts with those. The builds and the seeder are short-lived and run unscoped.
`PERF_SYSTEMD_MEMORY_MAX_MB` puts one low limit on every scope, for checking that the containment
works.

Postgres runs under the Docker daemon, outside any scope, so it always gets a `docker run --memory`
limit of its own, sized the same way (and `docker update`d onto containers made before the limit).

### Memory

Peak memory per part of the stack (cgroup `memory.peak`, page cache included) on a real run, which
`FOOTPRINT_MB` in `src/memory.ts` holds rounded up; the limits are 1.5× these:

| Size | Postgres | API | Frontend | Node + Chromium | Needs free |
|---|---|---|---|---|---|
| tiny (measured) | 166 MB | 564 MB | 85 MB | 407 MB | 1.5 GB |
| small (measured) | 205 MB | 504 MB | 87 MB | 519 MB | 1.7 GB |
| medium | 328 MB (seed) | estimated | estimated | estimated | 2.7 GB |
| large | estimated | estimated | estimated | estimated | 4.8 GB |

`memory.peak` counts page cache the kernel can reclaim, so these overstate what a part strictly
needs; the API idles near 170 MB with `tiny`. The Postgres limit does not slow the seed: 10,000
songs seeded in 24 s with the `medium` limit and in 32 s without.

The build (`dotnet build` and the frontend's Vite build, about 1.7 GB together at their peak) runs
before the stack starts, so the check asks for whichever is larger. Re-measure after a change that
moves these much, and update `FOOTPRINT_MB`.

## The generated library

`seed/` is a small .NET program that uses the API's own EF model, so it follows schema changes. It
writes rows shaped like an enriched, built library — fingerprints, synced lyrics, provider ids,
likes, play counts, playlists — and hard-links every song to one of four generated FLAC tracks with
embedded covers, so streaming, covers and thumbnails are real requests against real files. The
same seed always produces the same library.

The stack runs with every outside call switched off (enrichment providers, cover and lyrics
lookups, update checks). Its Postgres listens on loopback only, and the data lives in
`~/.cache/musichoarder-perf` (override with `PERF_WORK_DIR`) and a Docker volume per size and work
dir.

## A live instance

The same measurements, read-only (GET requests, and the browser only opens pages and plays a
track), against any instance:

```bash
PERF_COOKIE='mh_session=…' bun perf all --url https://music.example.com --label prod
```

The SQL and CPU columns are empty there, since they read the local database and process. Playing
a track on a real account moves that account's playback session to the test browser.

## Reading the numbers

- **Compare runs from the same machine only.** Absolute times depend on the hardware and on what
  else it is doing; `compare` ignores differences smaller than the spread within a run.
- **The local stack has no compressing proxy in front.** If production sits behind one, JSON goes
  over the wire at the *Gzipped* size, not *Size*. The time the server takes to produce the body,
  and the time the browser takes to parse it, are the same either way.
- **The library is synthetic.** Sizes and shapes are realistic; album pages have no canonical
  tracklists, and nothing is mid-pipeline.

## Adding a measurement

- An endpoint: one line in `ENDPOINTS` in `src/api/endpoints.ts` (name, group, path, budget).
- A page's cold start: one line in `LOADS` in `src/web/bench.ts` — the path and a condition that
  becomes true when the page shows its content.
- An interaction: a step in `measureInteractions` and its budget in `INTERACTION_BUDGETS`.
