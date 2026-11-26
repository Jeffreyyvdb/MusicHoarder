// What asking "anything new?" costs once the background jobs have written. The two "unchanged"
// rows of the endpoint catalogue revalidate a library nothing has touched, which a production
// library never is: the wishlist sync stamps its sources every two minutes, the lyrics timing check
// stamps the songs it probed. This scenario makes those writes itself, straight in the perf
// database, and then asks again:
//
//   bookkeeping writes  → the lists should still answer 304, and the name counts from memory
//   a visible write     → the lists must answer 200 with the change in them (the staleness check)
//
// It also times a single-row update of a song, which is where a row-version trigger would cost.

import type { StackConfig, Target } from '../config.ts'
import { psql } from '../stack.ts'
import { percentile } from '../stats.ts'
import { mainRows, timed } from './bench.ts'

const LISTS = [
  { name: 'Song list', path: '/songs?includeDeleted=false' },
  { name: 'Album cards', path: '/api/albums' },
] as const
const NAME_COUNTS = { name: 'Inbox name counts', path: '/api/inbox/name-counts' } as const

const STEPS = ['Nothing written', 'After bookkeeping writes', 'After a title change', 'After a video became ready'] as const
type Step = (typeof STEPS)[number]

interface Sample {
  status: number
  ms: number
  bytes: number
  /** Rows Postgres returned for the request: one for a stamp, the library for a recompute. */
  dbRows: number
}

export interface WritesRow {
  step: Step
  request: string
  /** Every status seen across the runs; more than one means the answer was not stable. */
  statuses: number[]
  medianMs: number
  minMs: number
  maxMs: number
  medianBytes: number
  medianDbRows: number
  runs: number
}

export interface WritesReport {
  target: string
  startedAt: string
  runs: number
  rows: WritesRow[]
  /** Microseconds per single-row `UPDATE "Songs"`, median of the repeats. */
  songUpdateMicros: { bookkeepingColumn: number; visibleColumn: number; rowsPerRepeat: number; repeats: number }
  /** A list that answered 304 after a visible write, or 200 without the change in it. */
  stale: string[]
  /** Bookkeeping writes that cost a download or a recompute. Not a failure of correctness. */
  missed: string[]
}

const median = (values: number[]) => percentile(values, 50)

function dbRowsSince(cfg: StackConfig): number {
  const [[rows] = []] = psql(
    cfg,
    `SELECT coalesce(sum(rows), 0) FROM pg_stat_statements
      WHERE query NOT ILIKE '%pg_stat_statements%' AND query NOT ILIKE 'SET %' AND query NOT ILIKE 'DISCARD%'`,
  )
  return Number(rows ?? 0)
}

async function ask(target: Target, path: string, etag: string | null): Promise<Sample & { etag: string | null; body: Uint8Array | null }> {
  const cfg = target.local!
  psql(cfg, 'SELECT pg_stat_statements_reset()')
  const headers = { ...target.headers, 'accept-encoding': 'identity', ...(etag ? { 'if-none-match': etag } : {}) }
  const answer = await timed(`${target.apiBase}${path}`, headers, true)
  return { status: answer.status, ms: answer.totalMs, bytes: answer.bytes, dbRows: dbRowsSince(cfg), etag: answer.headers.get('etag'), body: answer.body }
}

function songIn(body: Uint8Array | null, id: number): Record<string, unknown> | null {
  if (!body?.byteLength) return null
  const rows = (mainRows(JSON.parse(new TextDecoder().decode(body))) ?? []) as Record<string, unknown>[]
  return rows.find((row) => row.id === id) ?? null
}

/** The statements production's writers were seen making that change nothing a list shows. */
function bookkeepingWrites(cfg: StackConfig, songIds: number[]) {
  const ids = songIds.join(', ')
  psql(cfg, `UPDATE "WishlistSources" SET "LastSyncedAtUtc" = now()`)
  psql(
    cfg,
    `UPDATE "Songs" SET "LyricsSyncCheckedAtUtc" = now(), "LyricsSyncConfidence" = random(),
            "LyricsSyncProbeAttempts" = "LyricsSyncProbeAttempts" + 1
      WHERE "Id" IN (${ids})`,
  )
  psql(cfg, `UPDATE "Songs" SET "LibraryBuildLastAttemptedAtUtc" = now(), "LastWrittenAtUtc" = now() WHERE "Id" IN (${ids})`)
  psql(cfg, `UPDATE "Songs" SET "EnrichedAtUtc" = now(), "EnrichmentLastAttemptedAtUtc" = now() WHERE "Id" IN (${ids})`)
}

function timeSongUpdates(cfg: StackConfig, set: string, rowsPerRepeat: number): number {
  // psql prints a line for the CREATE as well; the timing is the last one.
  const micros = psql(
    cfg,
    `CREATE FUNCTION pg_temp.mh_time_updates(ids int[]) RETURNS double precision LANGUAGE plpgsql AS $$
     DECLARE started timestamptz := clock_timestamp(); id int;
     BEGIN
       FOREACH id IN ARRAY ids LOOP
         UPDATE "Songs" SET ${set} WHERE "Id" = id;
       END LOOP;
       RETURN extract(epoch FROM clock_timestamp() - started) * 1000000 / array_length(ids, 1);
     END $$;
     SELECT pg_temp.mh_time_updates(ARRAY(SELECT "Id" FROM "Songs" ORDER BY "Id" LIMIT ${rowsPerRepeat}))`,
  ).at(-1)?.[0]
  return Number(micros)
}

export async function runWritesScenario(target: Target, runs: number): Promise<WritesReport> {
  const cfg = target.local
  if (!cfg) throw new Error('The writes scenario writes to the database, so it only runs against the local stack.')
  const startedAt = new Date().toISOString()

  const songIds = psql(cfg, `SELECT "Id" FROM "Songs" WHERE "DeletedAtUtc" IS NULL AND "DestinationPath" IS NOT NULL ORDER BY "Id" LIMIT 5`).map(([id]) => Number(id))
  const [[videoId, videoSongId] = []] = psql(cfg, `SELECT "Id", "SongId" FROM "SongMusicVideos" ORDER BY "Id" LIMIT 1`)
  const [[sources] = []] = psql(cfg, `SELECT count(*) FROM "WishlistSources"`)
  if (songIds.length < 5 || !videoId || Number(sources) === 0)
    throw new Error('This database has no playlist sources or music videos to write to. Start the stack again ("bun perf up") so the seed adds them.')
  const renamedId = songIds[0]!

  const samples = new Map<string, Sample[]>()
  const record = (step: Step, request: string, sample: Sample) => {
    const key = `${step}\u0000${request}`
    samples.set(key, [...(samples.get(key) ?? []), sample])
  }
  const stale = new Set<string>()
  const missed = new Set<string>()

  for (let run = 0; run < runs; run++) {
    process.stdout.write(`[writes] run ${run + 1} of ${runs} … `)
    const tags = new Map<string, string>()
    const learn = async () => {
      for (const list of LISTS) {
        const full = await ask(target, list.path, null)
        if (!full.etag) throw new Error(`${list.path} answered ${full.status} with no ETag to send back.`)
        tags.set(list.path, full.etag)
      }
      await ask(target, NAME_COUNTS.path, null)
    }
    const revalidate = async (step: Step) => {
      const answers = new Map<string, Awaited<ReturnType<typeof ask>>>()
      for (const list of LISTS) {
        const answer = await ask(target, list.path, tags.get(list.path)!)
        record(step, list.name, answer)
        answers.set(list.name, answer)
      }
      const counts = await ask(target, NAME_COUNTS.path, null)
      record(step, NAME_COUNTS.name, counts)
      answers.set(NAME_COUNTS.name, counts)
      return answers
    }

    await learn()
    const untouched = await revalidate('Nothing written')
    for (const list of LISTS)
      if (untouched.get(list.name)!.status !== 304) throw new Error(`${list.path} answered ${untouched.get(list.name)!.status} to its own ETag with nothing written; nothing here can be measured.`)
    const countsRowsAtRest = untouched.get(NAME_COUNTS.name)!.dbRows

    bookkeepingWrites(cfg, songIds)
    const afterBookkeeping = await revalidate('After bookkeeping writes')
    for (const list of LISTS)
      if (afterBookkeeping.get(list.name)!.status !== 304) missed.add(`${list.name} answered ${afterBookkeeping.get(list.name)!.status} after bookkeeping writes`)
    if (afterBookkeeping.get(NAME_COUNTS.name)!.dbRows > countsRowsAtRest)
      missed.add(`${NAME_COUNTS.name} read ${afterBookkeeping.get(NAME_COUNTS.name)!.dbRows} rows after bookkeeping writes (${countsRowsAtRest} at rest)`)

    await learn()
    const title = `Renamed by the writes scenario ${run}-${Date.now()}`
    const [[originalTitle = ''] = []] = psql(cfg, `SELECT "Title" FROM "Songs" WHERE "Id" = ${renamedId}`)
    psql(cfg, `UPDATE "Songs" SET "Title" = '${title}' WHERE "Id" = ${renamedId}`)
    const afterTitle = await revalidate('After a title change')
    for (const list of LISTS)
      if (afterTitle.get(list.name)!.status !== 200) stale.add(`${list.name} answered ${afterTitle.get(list.name)!.status} after a song was renamed`)
    if (songIn(afterTitle.get('Song list')!.body, renamedId)?.title !== title) stale.add('Song list was sent without the new title')
    psql(cfg, `UPDATE "Songs" SET "Title" = '${originalTitle.replaceAll("'", "''")}' WHERE "Id" = ${renamedId}`)

    await learn()
    psql(cfg, `UPDATE "SongMusicVideos" SET "Status" = 1, "FilePath" = '/perf/video.mp4' WHERE "Id" = ${videoId}`)
    const afterVideo = await revalidate('After a video became ready')
    if (afterVideo.get('Song list')!.status !== 200) stale.add(`Song list answered ${afterVideo.get('Song list')!.status} after a video became ready`)
    if (songIn(afterVideo.get('Song list')!.body, Number(videoSongId))?.hasMusicVideo !== true) stale.add('Song list was sent without the new music video')
    psql(cfg, `UPDATE "SongMusicVideos" SET "Status" = 2, "FilePath" = NULL WHERE "Id" = ${videoId}`)
    console.log('done')
  }

  const rows: WritesRow[] = []
  for (const step of STEPS) {
    for (const request of [...LISTS.map((l) => l.name), NAME_COUNTS.name]) {
      const taken = samples.get(`${step}\u0000${request}`) ?? []
      rows.push({
        step,
        request,
        statuses: [...new Set(taken.map((s) => s.status))].sort(),
        medianMs: median(taken.map((s) => s.ms)),
        minMs: Math.min(...taken.map((s) => s.ms)),
        maxMs: Math.max(...taken.map((s) => s.ms)),
        medianBytes: median(taken.map((s) => s.bytes)),
        medianDbRows: median(taken.map((s) => s.dbRows)),
        runs: taken.length,
      })
    }
  }

  process.stdout.write('[writes] timing single-row song updates … ')
  const rowsPerRepeat = Math.min(2000, Number(psql(cfg, `SELECT count(*) FROM "Songs"`)[0]![0]))
  const repeats = 7
  const bookkeeping: number[] = []
  const visible: number[] = []
  for (let i = 0; i < repeats; i++) {
    bookkeeping.push(timeSongUpdates(cfg, `"LyricsSyncProbeAttempts" = "LyricsSyncProbeAttempts" + 1`, rowsPerRepeat))
    visible.push(timeSongUpdates(cfg, `"PlayCount" = "PlayCount" + 1`, rowsPerRepeat))
  }
  // Those updates left a dead version of every row they touched; later measurements should not pay for them.
  psql(cfg, 'VACUUM ANALYZE "Songs"')
  console.log('done')

  return {
    target: target.label,
    startedAt,
    runs,
    rows,
    songUpdateMicros: { bookkeepingColumn: median(bookkeeping), visibleColumn: median(visible), rowsPerRepeat, repeats },
    stale: [...stale],
    missed: [...missed],
  }
}

export function renderWrites(report: WritesReport, label: string): string {
  const kb = (bytes: number) => (bytes < 1024 ? `${bytes} B` : `${Math.round(bytes / 1024).toLocaleString('en')} kB`)
  const lines = [
    `# Revalidating after writes: ${label}`,
    '',
    `${report.target}, ${report.runs} runs, medians (min to max). "Rows read" is what Postgres returned for the request.`,
    '',
    '| After | Request | Status | Time | Body | Rows read |',
    '|---|---|---|---|---|---|',
    ...report.rows.map((row) =>
      `| ${row.step} | ${row.request} | ${row.statuses.join(', ')} | ${row.medianMs.toFixed(1)} ms (${row.minMs.toFixed(1)} to ${row.maxMs.toFixed(1)}) | ${kb(row.medianBytes)} | ${row.medianDbRows.toLocaleString('en')} |`),
    '',
    `One-row \`UPDATE "Songs"\`, ${report.songUpdateMicros.rowsPerRepeat.toLocaleString('en')} in a transaction, median of ${report.songUpdateMicros.repeats}: ` +
      `${report.songUpdateMicros.bookkeepingColumn.toFixed(1)} µs for a bookkeeping column, ${report.songUpdateMicros.visibleColumn.toFixed(1)} µs for one the lists show.`,
    '',
    report.stale.length ? `**STALE: ${report.stale.join('; ')}.**` : 'Staleness check passed: every visible write came back as a 200 carrying the change.',
    report.missed.length ? `Bookkeeping writes were not ignored: ${report.missed.join('; ')}.` : 'Bookkeeping writes were ignored: the lists answered 304 and the name counts were not recomputed.',
    '',
  ]
  return lines.join('\n')
}
