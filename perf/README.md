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
```

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

## The generated library

`seed/` is a small .NET program that uses the API's own EF model, so it follows schema changes. It
writes rows shaped like an enriched, built library — fingerprints, synced lyrics, provider ids,
likes, play counts, playlists — and hard-links every song to one of four generated FLAC tracks with
embedded covers, so streaming, covers and thumbnails are real requests against real files. The
same seed always produces the same library.

The stack runs with every outside call switched off (enrichment providers, cover and lyrics
lookups, update checks). Its Postgres listens on loopback only, and the data lives in
`~/.cache/musichoarder-perf` (override with `PERF_WORK_DIR`) and a Docker volume per size.

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
