// Times every endpoint in the catalogue against a target and explains each number: how big the
// answer is and which fields make it big, how many SQL statements it ran, how much CPU it cost
// the API, and what it does when several callers ask at once.

import type { Target } from '../config.ts'
import { apiPid, cpuSeconds, psql, residentBytes } from '../stack.ts'
import { summarize, type Summary } from '../stats.ts'
import { ENDPOINTS, type Endpoint, type Sample } from './endpoints.ts'

export interface FieldShare {
  field: string
  bytes: number
  share: number
}

export interface Statement {
  callsPerRequest: number
  msPerRequest: number
  rowsPerRequest: number
  sql: string
}

export interface EndpointResult {
  name: string
  group: string
  path: string
  status: number
  error?: string
  /** Time to response headers. */
  ttfb: Summary
  /** Time to the last byte of the body. */
  total: Summary
  wireBytes: number
  contentEncoding: string | null
  cacheControl: string | null
  decodedBytes: number
  gzipBytes: number | null
  rows: number | null
  fields: FieldShare[]
  cpuMsPerRequest: number | null
  db: { queriesPerRequest: number; msPerRequest: number; top: Statement[] } | null
  load: { concurrency: number; requestsPerSecond: number; total: Summary; errors: number } | null
  /** The same request through the frontend proxy: its timing, and what the browser is sent. */
  proxy: { ttfb: Summary; total: Summary; wireBytes: number; contentEncoding: string | null } | null
  budget: { p95Ms?: number; gzipBytes?: number }
  overBudget: string[]
}

export interface ApiReport {
  target: string
  startedAt: string
  songs: number | null
  apiResidentBytes: { before: number | null; after: number | null }
  endpoints: EndpointResult[]
}

export interface ApiBenchOptions {
  iterations: number
  /** Stop repeating an endpoint once it has used this long, as long as three samples exist. */
  timeBudgetMs: number
  concurrency: number
  filter: string | null
}

interface Timed {
  status: number
  ttfbMs: number
  totalMs: number
  bytes: number
  headers: Headers
  body: Uint8Array | null
}

async function timed(url: string, headers: Record<string, string>, keepBody: boolean): Promise<Timed> {
  const started = performance.now()
  // decompress: false keeps the body as it crossed the wire, so the byte count is the real one.
  const response = await fetch(url, { headers, decompress: false, redirect: 'manual', signal: AbortSignal.timeout(120_000) } as RequestInit)
  const ttfbMs = performance.now() - started
  // An event stream never ends; it has no "time to last byte" to measure.
  if (response.headers.get('content-type')?.startsWith('text/event-stream')) {
    await response.body?.cancel()
    throw new Error('an event stream, not a request with an end')
  }
  const chunks: Uint8Array[] = []
  let bytes = 0
  if (response.body) {
    for await (const chunk of response.body as unknown as AsyncIterable<Uint8Array>) {
      bytes += chunk.byteLength
      if (keepBody) chunks.push(chunk)
    }
  }
  const totalMs = performance.now() - started
  return { status: response.status, ttfbMs, totalMs, bytes, headers: response.headers, body: keepBody ? concat(chunks, bytes) : null }
}

function concat(chunks: Uint8Array[], length: number): Uint8Array {
  const out = new Uint8Array(length)
  let offset = 0
  for (const chunk of chunks) {
    out.set(chunk, offset)
    offset += chunk.byteLength
  }
  return out
}

function decode(body: Uint8Array, encoding: string | null): Uint8Array {
  if (encoding === 'gzip') return Bun.gunzipSync(body as Uint8Array<ArrayBuffer>)
  if (encoding === 'br') return new Uint8Array(require('node:zlib').brotliDecompressSync(body))
  if (encoding === 'zstd') return new Uint8Array(require('node:zlib').zstdDecompressSync(body))
  if (encoding === 'deflate') return Bun.inflateSync(body as Uint8Array<ArrayBuffer>)
  return body
}

/** The list a JSON payload is mostly made of: itself, or its largest array property. */
function mainRows(json: unknown): unknown[] | null {
  if (Array.isArray(json)) return json
  if (json && typeof json === 'object') {
    const arrays = Object.values(json).filter(Array.isArray) as unknown[][]
    arrays.sort((a, b) => b.length - a.length)
    if (arrays[0] && arrays[0].length > 0) return arrays[0]
  }
  return null
}

/** Which fields a list payload spends its bytes on, largest first. */
function fieldShares(rows: unknown[]): FieldShare[] {
  const bytes = new Map<string, number>()
  let total = 0
  for (const row of rows) {
    if (!row || typeof row !== 'object') return []
    for (const [key, value] of Object.entries(row)) {
      // The key, its quotes and colon, the value and the comma — what the field costs per row.
      const cost = key.length + 4 + (JSON.stringify(value)?.length ?? 4)
      bytes.set(key, (bytes.get(key) ?? 0) + cost)
      total += cost
    }
  }
  return [...bytes.entries()]
    .map(([field, b]) => ({ field, bytes: b, share: b / total }))
    .sort((a, b) => b.bytes - a.bytes)
    .slice(0, 8)
}

async function pickSample(target: Target): Promise<{ sample: Sample; songs: number }> {
  const response = await fetch(`${target.apiBase}/songs`, { headers: target.headers })
  if (!response.ok) throw new Error(`GET /songs answered ${response.status}; is the session valid?`)
  const json = (await response.json()) as unknown
  const rows = (mainRows(json) ?? []) as Record<string, unknown>[]
  const built = rows.filter((r) => r.destinationPath && r.album && (r.albumArtist || r.artist))
  if (built.length < 2) throw new Error('The library needs at least two built songs to measure.')

  // The album with the most tracks among the first few hundred rows: a full album page.
  const byAlbum = new Map<string, Record<string, unknown>[]>()
  for (const row of built) {
    const key = `${row.albumArtist ?? row.artist}\u0000${row.album}`
    byAlbum.set(key, [...(byAlbum.get(key) ?? []), row])
  }
  const album = [...byAlbum.values()].sort((a, b) => b.length - a.length)[0]!
  const first = album[0]!
  const lyrics = built.find((r) => r.syncedLyrics || r.lyricsStatus === 'Fetched' || r.lyricsStatus === 1) ?? first

  let playlistId: number | null = null
  const playlists = await fetch(`${target.apiBase}/api/playlists`, { headers: target.headers })
  if (playlists.ok) {
    const list = mainRows(await playlists.json()) as Record<string, unknown>[] | null
    const biggest = list?.filter((p) => typeof p.id === 'number').sort((a, b) => Number(b.trackCount ?? 0) - Number(a.trackCount ?? 0))[0]
    playlistId = (biggest?.id as number | undefined) ?? null
  }

  return {
    songs: rows.length,
    sample: {
      songId: first.id as number,
      nextSongId: built.find((r) => r.id !== first.id && r.album !== first.album)!.id as number,
      lyricsSongId: lyrics.id as number,
      artist: String(first.albumArtist ?? first.artist),
      album: String(first.album),
      year: (first.year as number | null) ?? null,
      playlistId,
      albumSongIds: built.slice(0, 40).map((r) => r.id as number),
    },
  }
}

async function repeat(url: string, headers: Record<string, string>, iterations: number, timeBudgetMs: number) {
  const ttfb: number[] = []
  const total: number[] = []
  const started = performance.now()
  for (let i = 0; i < iterations; i++) {
    const result = await timed(url, headers, false)
    ttfb.push(result.ttfbMs)
    total.push(result.totalMs)
    if (i >= 2 && performance.now() - started > timeBudgetMs) break
  }
  return { ttfb, total }
}

async function underLoad(url: string, headers: Record<string, string>, concurrency: number, durationMs: number) {
  const latencies: number[] = []
  let errors = 0
  const started = performance.now()
  await Promise.all(
    Array.from({ length: concurrency }, async () => {
      while (performance.now() - started < durationMs) {
        const result = await timed(url, headers, false)
        if (result.status >= 400) errors++
        latencies.push(result.totalMs)
      }
    }),
  )
  const elapsed = (performance.now() - started) / 1000
  return { concurrency, requestsPerSecond: latencies.length / elapsed, total: summarize(latencies), errors }
}

function statements(target: Target, url: string, headers: Record<string, string>) {
  const cfg = target.local!
  const requests = 3
  return (async () => {
    psql(cfg, 'SELECT pg_stat_statements_reset()')
    for (let i = 0; i < requests; i++) await timed(url, headers, false)
    const rows = psql(
      cfg,
      `SELECT calls, round(total_exec_time::numeric, 3), rows, left(regexp_replace(query, '\\s+', ' ', 'g'), 260)
         FROM pg_stat_statements
        WHERE query NOT ILIKE '%pg_stat_statements%' AND query NOT ILIKE 'SET %' AND query NOT ILIKE 'DISCARD%'
        ORDER BY total_exec_time DESC LIMIT 100`,
    )
    const all = rows.map(([calls, ms, rowCount, sql]) => ({
      callsPerRequest: Number(calls) / requests,
      msPerRequest: Number(ms) / requests,
      rowsPerRequest: Number(rowCount) / requests,
      sql: sql ?? '',
    }))
    return {
      queriesPerRequest: all.reduce((sum, s) => sum + s.callsPerRequest, 0),
      msPerRequest: all.reduce((sum, s) => sum + s.msPerRequest, 0),
      top: all.slice(0, 3),
    }
  })()
}

function blank(endpoint: Endpoint, path: string, first: Timed): EndpointResult {
  return {
    name: endpoint.name,
    group: endpoint.group,
    path,
    status: first.status,
    ttfb: summarize([]),
    total: summarize([]),
    wireBytes: first.bytes,
    contentEncoding: first.headers.get('content-encoding'),
    cacheControl: first.headers.get('cache-control'),
    decodedBytes: first.bytes,
    gzipBytes: null,
    rows: null,
    fields: [],
    cpuMsPerRequest: null,
    db: null,
    load: null,
    proxy: null,
    budget: endpoint.budget ?? {},
    overBudget: [],
  }
}

async function measure(endpoint: Endpoint, sample: Sample, target: Target, options: ApiBenchOptions): Promise<EndpointResult | null> {
  const path = endpoint.path(sample)
  if (path === null) return null
  const url = `${target.apiBase}${path}`
  const headers: Record<string, string> = {
    ...target.headers,
    'accept-encoding': 'gzip, br, zstd',
    ...(endpoint.range ? { range: endpoint.range } : {}),
  }

  let first: Timed
  try {
    if (endpoint.revalidate) {
      // One full answer to learn the tag; every request after it is the conditional one.
      const learned = await timed(url, headers, false)
      const etag = learned.headers.get('etag')
      if (!etag) return { ...blank(endpoint, path, learned), error: `HTTP ${learned.status} with no ETag to send back` }
      headers['if-none-match'] = etag
    }
    first = await timed(url, headers, true)
  } catch (error) {
    first = { status: 0, ttfbMs: NaN, totalMs: NaN, bytes: 0, headers: new Headers(), body: null }
    return { ...blank(endpoint, path, first), error: (error as Error).message }
  }
  const result = blank(endpoint, path, first)
  if (first.status >= 400) {
    result.error = `HTTP ${first.status}`
    return result
  }
  if (endpoint.revalidate && first.status !== 304) {
    // Timing a full download under this name would hide that revalidation stopped working.
    result.error = `HTTP ${first.status}, not the 304 an unchanged list should get`
    return result
  }

  if (!endpoint.binary && first.body && first.bytes > 0) {
    const decoded = decode(first.body, result.contentEncoding)
    result.decodedBytes = decoded.byteLength
    result.gzipBytes = result.contentEncoding ? first.bytes : Bun.gzipSync(decoded as Uint8Array<ArrayBuffer>).byteLength
    try {
      const rows = mainRows(JSON.parse(new TextDecoder().decode(decoded)))
      if (rows) {
        result.rows = rows.length
        result.fields = fieldShares(rows)
      }
    } catch {
      // not JSON; the sizes above still stand
    }
  }

  const pid = target.local ? apiPid(target.local) : null
  const cpuBefore = pid ? cpuSeconds(pid) : null
  const runs = await repeat(url, headers, options.iterations, options.timeBudgetMs)
  const cpuAfter = pid ? cpuSeconds(pid) : null
  result.ttfb = summarize(runs.ttfb)
  result.total = summarize(runs.total)
  if (cpuBefore != null && cpuAfter != null) result.cpuMsPerRequest = ((cpuAfter - cpuBefore) * 1000) / runs.total.length

  if (target.local) result.db = await statements(target, url, headers)
  // Several callers at once only where one caller is quick: eight copies of a multi-second
  // response measure the machine's memory, not the endpoint.
  if (options.concurrency > 1 && result.total.median < 400)
    result.load = await underLoad(url, headers, options.concurrency, 3000)

  if (endpoint.viaProxy && target.proxyBase) {
    const proxied = await repeat(`${target.proxyBase}${path}`, headers, Math.max(3, Math.ceil(options.iterations / 2)), options.timeBudgetMs)
    const sent = await timed(`${target.proxyBase}${path}`, headers, false)
    result.proxy = { ttfb: summarize(proxied.ttfb), total: summarize(proxied.total), wireBytes: sent.bytes, contentEncoding: sent.headers.get('content-encoding') }
  }

  if (result.budget.p95Ms != null && result.total.p95 > result.budget.p95Ms)
    result.overBudget.push(`p95 ${Math.round(result.total.p95)} ms over ${result.budget.p95Ms} ms`)
  if (result.budget.gzipBytes != null && result.gzipBytes != null && result.gzipBytes > result.budget.gzipBytes)
    result.overBudget.push(`${Math.round(result.gzipBytes / 1024)} kB gzipped over ${Math.round(result.budget.gzipBytes / 1024)} kB`)
  return result
}

export async function runApiBench(target: Target, options: ApiBenchOptions): Promise<ApiReport> {
  const startedAt = new Date().toISOString()
  const { sample, songs } = await pickSample(target)
  const pid = target.local ? apiPid(target.local) : null
  const before = pid ? residentBytes(pid) : null

  const endpoints: EndpointResult[] = []
  for (const endpoint of ENDPOINTS) {
    if (options.filter && !endpoint.name.toLowerCase().includes(options.filter.toLowerCase())) continue
    process.stdout.write(`[api] ${endpoint.name} … `)
    const result = await measure(endpoint, sample, target, options)
    if (!result) {
      console.log('skipped (nothing in the library to ask for)')
      continue
    }
    console.log(result.error ?? `${Math.round(result.total.median)} ms median, ${Math.round(result.decodedBytes / 1024)} kB`)
    endpoints.push(result)
  }

  return {
    target: target.label,
    startedAt,
    songs,
    apiResidentBytes: { before, after: pid ? residentBytes(pid) : null },
    endpoints,
  }
}
