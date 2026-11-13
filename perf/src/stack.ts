// Starts and stops the local stack the suite measures: a throwaway Postgres, a seeded library,
// the API and the built frontend — the same shape as production, minus the reverse proxy.

import { spawn, spawnSync } from 'node:child_process'
import { existsSync, mkdirSync, openSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { REPO_ROOT, PERF_ROOT, type StackConfig, type Target } from './config.ts'

interface StackState {
  apiPid: number
  webPid: number
  bearer: string
  cookies: string[]
}

const statePath = (cfg: StackConfig) => join(cfg.workDir, 'state.json')
const log = (message: string) => console.log(`[stack] ${message}`)

function run(command: string, args: string[], options: { cwd?: string; env?: Record<string, string>; quiet?: boolean } = {}) {
  const result = spawnSync(command, args, {
    cwd: options.cwd,
    env: { ...process.env, ...options.env },
    encoding: 'utf8',
    maxBuffer: 64 * 1024 * 1024,
  })
  if (result.status !== 0) {
    const output = `${result.stdout ?? ''}${result.stderr ?? ''}`.trim().split('\n').slice(-30).join('\n')
    throw new Error(`${command} ${args.slice(0, 4).join(' ')} failed (${result.status ?? result.error?.message}):\n${output}`)
  }
  if (!options.quiet && result.stdout?.trim()) console.log(result.stdout.trim().split('\n').slice(-3).join('\n'))
  return result.stdout ?? ''
}

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

async function waitFor(what: string, probe: () => Promise<boolean> | boolean, timeoutMs = 120_000) {
  const started = Date.now()
  while (Date.now() - started < timeoutMs) {
    try {
      if (await probe()) return
    } catch {
      // not up yet
    }
    await sleep(500)
  }
  throw new Error(`Timed out waiting for ${what}.`)
}

function alive(pid: number): boolean {
  try {
    process.kill(pid, 0)
    return true
  } catch {
    return false
  }
}

/** CPU seconds a process has used so far (user + system), from /proc. Linux only; null elsewhere. */
export function cpuSeconds(pid: number): number | null {
  try {
    const stat = readFileSync(`/proc/${pid}/stat`, 'utf8')
    const fields = stat.slice(stat.lastIndexOf(')') + 2).split(' ')
    return (Number(fields[11]) + Number(fields[12])) / 100
  } catch {
    return null
  }
}

/** Resident memory of a process in bytes, from /proc. Linux only; null elsewhere. */
export function residentBytes(pid: number): number | null {
  try {
    const match = /VmRSS:\s+(\d+) kB/.exec(readFileSync(`/proc/${pid}/status`, 'utf8'))
    return match ? Number(match[1]) * 1024 : null
  } catch {
    return null
  }
}

/** The startup scan and the first sweeps run right after boot; measuring through them is noise. */
async function waitUntilIdle(pid: number, name: string) {
  let calm = 0
  for (let i = 0; i < 90 && calm < 3; i++) {
    const before = cpuSeconds(pid)
    await sleep(1000)
    const after = cpuSeconds(pid)
    if (before == null || after == null) return
    calm = after - before < 0.15 ? calm + 1 : 0
  }
  if (calm < 3) log(`${name} is still busy after 90 s; measuring anyway.`)
}

function makeTemplates(dir: string) {
  mkdirSync(dir, { recursive: true })
  // Four tracks with different covers: pink noise under a tone, so the FLAC is a realistic size
  // (a pure tone compresses to nothing) and the embedded 1000px cover is a realistic JPEG.
  for (let i = 0; i < 4; i++) {
    const out = join(dir, `template-${i}.flac`)
    if (existsSync(out)) continue
    // The cover first, then muxed in as a still: asking for one video frame in the same command
    // ends the audio after that frame.
    const cover = join(dir, `cover-${i}.jpg`)
    run('ffmpeg', [
      '-v', 'error', '-y',
      '-f', 'lavfi', '-i', `mandelbrot=size=1000x1000:rate=1:start_scale=${1 + i}`,
      '-vf', `hue=h=${i * 80}`, '-frames:v', '1', '-q:v', '3', cover,
    ], { quiet: true })
    run('ffmpeg', [
      '-v', 'error', '-y',
      '-f', 'lavfi', '-i', `sine=frequency=${220 * (i + 1)}:duration=150`,
      '-f', 'lavfi', '-i', 'anoisesrc=color=pink:duration=150:amplitude=0.25',
      '-i', cover,
      '-filter_complex', '[0:a][1:a]amix=inputs=2[a]',
      '-map', '[a]', '-map', '2:v',
      '-c:a', 'flac', '-ar', '44100', '-ac', '2', '-sample_fmt', 's16',
      '-c:v', 'copy', '-disposition:v', 'attached_pic',
      out,
    ], { quiet: true })
    rmSync(cover)
  }
}

function startPostgres(cfg: StackConfig) {
  const exists = spawnSync('docker', ['inspect', '-f', '{{.State.Running}}', cfg.pgContainer], { encoding: 'utf8' })
  if (exists.status !== 0) {
    log(`starting Postgres (${cfg.pgContainer}) on 127.0.0.1:${cfg.pgPort}`)
    run('docker', [
      'run', '-d', '--name', cfg.pgContainer,
      '-e', 'POSTGRES_PASSWORD=perf', '-e', 'POSTGRES_DB=musichoarderdb',
      '-p', `127.0.0.1:${cfg.pgPort}:5432`,
      '-v', `${cfg.pgVolume}:/var/lib/postgresql/data`,
      'postgres:17.6',
      '-c', 'shared_preload_libraries=pg_stat_statements',
      '-c', 'pg_stat_statements.track=top',
      '-c', 'pg_stat_statements.max=5000',
    ], { quiet: true })
  } else if (exists.stdout.trim() !== 'true') {
    run('docker', ['start', cfg.pgContainer], { quiet: true })
  }
}

/** Runs SQL in the perf database and returns psql's unaligned, tab-separated rows. */
export function psql(cfg: StackConfig, sql: string): string[][] {
  const out = run(
    'docker',
    ['exec', '-i', cfg.pgContainer, 'psql', '-U', 'postgres', '-d', 'musichoarderdb', '-At', '-F', '\t', '-c', sql],
    { quiet: true },
  )
  return out.split('\n').filter(Boolean).map((line) => line.split('\t'))
}

function spawnDetached(command: string, args: string[], cwd: string, env: Record<string, string>, logFile: string): number {
  const fd = openSync(logFile, 'w')
  const child = spawn(command, args, { cwd, env: { ...process.env, ...env }, detached: true, stdio: ['ignore', fd, fd] })
  child.unref()
  if (!child.pid) throw new Error(`Could not start ${command}.`)
  return child.pid
}

function apiEnvironment(cfg: StackConfig): Record<string, string> {
  return {
    ASPNETCORE_ENVIRONMENT: 'Production',
    ASPNETCORE_URLS: cfg.apiUrl,
    ConnectionStrings__musichoarderdb: cfg.connection,
    MusicEnricher__SourceDirectory: join(cfg.workDir, 'source'),
    MusicEnricher__DestinationDirectory: join(cfg.workDir, 'destination'),
    Auth__OwnerEmail: cfg.ownerEmail,
    Auth__DataProtectionKeysPath: join(cfg.workDir, 'dpkeys'),
    Artwork__CoverCacheDir: join(cfg.workDir, 'covers'),
    Frontend__PublicBaseUrl: cfg.webUrl,
    // Nothing here may call out: the rows are invented, and a provider lookup for them would be
    // both pointless traffic and noise in the numbers.
    MusicEnricher__AutoScanIntervalMinutes: '0',
    MusicEnricher__EnableUpdateCheck: 'false',
    MusicEnricher__EnableAcoustIdProvider: 'false',
    MusicEnricher__EnableMusicBrainzWebProvider: 'false',
    MusicEnricher__EnableSpotifyApiProvider: 'false',
    MusicEnricher__EnableTrackerProvider: 'false',
    MusicEnricher__EnableDeezerProvider: 'false',
    MusicEnricher__EnableAppleMusicProvider: 'false',
    MusicEnricher__EnableYeTrackerProvider: 'false',
    MusicEnricher__EnableCanonicalAlbumFetch: 'false',
    MusicEnricher__EnableExternalCoverArtFetch: 'false',
    MusicEnricher__EnableArtistImages: 'false',
    MusicEnricher__EnableLyricsBackfillSweep: 'false',
    MusicEnricher__EnableLyricsRecheckSweep: 'false',
    MusicEnricher__EnableAlbumSplitSelfHeal: 'false',
    MusicEnricher__EnableArtistCreditSelfHeal: 'false',
    MusicEnricher__EnableArtistCreditRepairSelfHeal: 'false',
    MusicEnricher__EnablePlaylistExport: 'false',
    MusicEnricher__EnableAutomaticQualityUpgrades: 'false',
    OTEL_EXPORTER_OTLP_ENDPOINT: '',
    Logging__LogLevel__Default: 'Information',
  }
}

async function signIn(cfg: StackConfig, apiLog: string): Promise<{ bearer: string; cookies: string[] }> {
  // The console sender logs the link when no mail provider is configured; that log is the inbox.
  const requestToken = async (): Promise<string> => {
    const seen = (readFileSync(apiLog, 'utf8').match(/\[MAGIC LINK\]/g) ?? []).length
    const response = await fetch(`${cfg.apiUrl}/api/auth/request-link`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ email: cfg.ownerEmail }),
    })
    if (!response.ok) throw new Error(`request-link answered ${response.status}: ${await response.text()}`)
    let token: string | null = null
    await waitFor('the magic link in the API log', () => {
      const links = [...readFileSync(apiLog, 'utf8').matchAll(/\[MAGIC LINK\][^\n]*[?&]token=([A-Za-z0-9_\-.%]+)/g)]
      if (links.length <= seen) return false
      token = decodeURIComponent(links[links.length - 1]![1]!)
      return true
    }, 15_000)
    return token!
  }

  const tokenResponse = await fetch(`${cfg.apiUrl}/api/auth/token`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ token: await requestToken() }),
  })
  if (!tokenResponse.ok) throw new Error(`auth/token answered ${tokenResponse.status}`)
  const tokenBody = (await tokenResponse.json()) as Record<string, string>
  const bearer = tokenBody.accessToken ?? tokenBody.access_token ?? tokenBody.token
  if (!bearer) throw new Error(`auth/token returned no token: ${Object.keys(tokenBody).join(', ')}`)

  const consume = await fetch(`${cfg.apiUrl}/api/auth/consume`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ token: await requestToken() }),
  })
  if (!consume.ok) throw new Error(`auth/consume answered ${consume.status}`)
  const cookies = consume.headers.getSetCookie().map((cookie) => cookie.split(';')[0]!).filter((c) => !c.endsWith('='))
  if (cookies.length === 0) throw new Error('auth/consume set no cookie.')
  return { bearer, cookies }
}

export interface UpOptions {
  build: boolean
}

export async function up(cfg: StackConfig, options: UpOptions): Promise<Target> {
  await down(cfg, { quiet: true, keepDatabase: true })
  for (const dir of ['templates', 'source', 'destination', 'dpkeys', 'covers', 'logs'])
    mkdirSync(join(cfg.workDir, dir), { recursive: true })

  log('generating template tracks')
  makeTemplates(join(cfg.workDir, 'templates'))

  startPostgres(cfg)
  await waitFor('Postgres', () =>
    spawnSync('docker', ['exec', cfg.pgContainer, 'psql', '-U', 'postgres', '-d', 'musichoarderdb', '-c', 'select 1']).status === 0)

  const seedProject = join(PERF_ROOT, 'seed')
  const seedDll = join(seedProject, 'bin/Release/net10.0/MusicHoarder.PerfSeed.dll')
  const apiDir = join(REPO_ROOT, 'MusicHoarder.Api/bin/Release/net10.0')
  if (options.build || !existsSync(seedDll)) {
    log('building the API and the seeder (Release)')
    run('dotnet', ['build', seedProject, '-c', 'Release', '--nologo', '-v', 'quiet'], { quiet: true })
  }

  log(`seeding ${cfg.songs} songs (skipped when already there)`)
  run('dotnet', [
    seedDll,
    '--connection', cfg.connection,
    '--songs', String(cfg.songs),
    '--source', join(cfg.workDir, 'source'),
    '--destination', join(cfg.workDir, 'destination'),
    '--templates', join(cfg.workDir, 'templates'),
  ])
  psql(cfg, 'CREATE EXTENSION IF NOT EXISTS pg_stat_statements')

  const frontendDir = join(REPO_ROOT, 'frontend')
  if (options.build || !existsSync(join(frontendDir, 'build/index.js'))) {
    log('building the frontend')
    if (!existsSync(join(frontendDir, 'node_modules'))) run('bun', ['install', '--frozen-lockfile'], { cwd: frontendDir, quiet: true })
    run('bun', ['run', 'build'], { cwd: frontendDir, quiet: true })
  }

  const apiLog = join(cfg.workDir, 'logs/api.log')
  log(`starting the API on ${cfg.apiUrl}`)
  const apiPid = spawnDetached('dotnet', [join(apiDir, 'MusicHoarder.Api.dll')], apiDir, apiEnvironment(cfg), apiLog)
  log(`starting the frontend on ${cfg.webUrl}`)
  const webPid = spawnDetached('bun', ['build/index.js'], frontendDir, {
    NODE_ENV: 'production',
    PORT: String(cfg.webPort),
    HOST: '127.0.0.1',
    ORIGIN: cfg.webUrl,
    MUSICHOARDER_API_URL: cfg.apiUrl,
    OTEL_EXPORTER_OTLP_ENDPOINT: '',
  }, join(cfg.workDir, 'logs/web.log'))
  writeFileSync(statePath(cfg), JSON.stringify({ apiPid, webPid, bearer: '', cookies: [] } satisfies StackState))

  try {
    await waitFor('the API', async () => {
      if (!alive(apiPid)) throw new Error('exited')
      return (await fetch(`${cfg.apiUrl}/api/auth/me`)).status === 401
    })
    await waitFor('the frontend', async () => (await fetch(`${cfg.webUrl}/api/health`)).ok, 60_000)
  } catch (error) {
    const tail = readFileSync(apiLog, 'utf8').split('\n').slice(-25).join('\n')
    throw new Error(`${(error as Error).message}\n--- api.log ---\n${tail}`)
  }

  await waitUntilIdle(apiPid, 'the API')
  const session = await signIn(cfg, apiLog)
  writeFileSync(statePath(cfg), JSON.stringify({ apiPid, webPid, ...session } satisfies StackState))
  log('stack is up')
  return target(cfg)
}

/** The running local stack as a measurement target; throws when it is not up. */
export function target(cfg: StackConfig): Target {
  if (!existsSync(statePath(cfg))) throw new Error(`No ${cfg.size} stack is running. Start one with: bun perf up --size ${cfg.size}`)
  const state = JSON.parse(readFileSync(statePath(cfg), 'utf8')) as StackState
  if (!alive(state.apiPid) || !alive(state.webPid) || !state.bearer)
    throw new Error(`The ${cfg.size} stack is not running any more. Start it again with: bun perf up --size ${cfg.size}`)
  return {
    apiBase: cfg.apiUrl,
    webBase: cfg.webUrl,
    proxyBase: `${cfg.webUrl}/api/mh`,
    headers: { authorization: `Bearer ${state.bearer}` },
    cookies: state.cookies,
    local: cfg,
    label: `local stack, ${cfg.songs.toLocaleString('en')} songs`,
  }
}

export function apiPid(cfg: StackConfig): number | null {
  if (!existsSync(statePath(cfg))) return null
  return (JSON.parse(readFileSync(statePath(cfg), 'utf8')) as StackState).apiPid
}

export async function down(cfg: StackConfig, options: { quiet?: boolean; keepDatabase?: boolean; purge?: boolean } = {}) {
  if (existsSync(statePath(cfg))) {
    const state = JSON.parse(readFileSync(statePath(cfg), 'utf8')) as StackState
    for (const pid of [state.apiPid, state.webPid]) {
      if (!alive(pid)) continue
      // Negative pid: the whole process group, since each was started detached as its own leader.
      try { process.kill(-pid, 'SIGTERM') } catch { /* already gone */ }
    }
    await sleep(800)
    for (const pid of [state.apiPid, state.webPid]) {
      if (alive(pid)) try { process.kill(-pid, 'SIGKILL') } catch { /* already gone */ }
    }
    rmSync(statePath(cfg))
    if (!options.quiet) log('stopped the API and the frontend')
  }
  if (options.keepDatabase) return
  spawnSync('docker', ['stop', cfg.pgContainer])
  if (options.purge) {
    spawnSync('docker', ['rm', '-f', cfg.pgContainer])
    spawnSync('docker', ['volume', 'rm', cfg.pgVolume])
    rmSync(cfg.workDir, { recursive: true, force: true })
    if (!options.quiet) log('removed the database and the generated library')
  } else if (!options.quiet) {
    log(`stopped Postgres (the seeded database is kept; "down --purge" removes it)`)
  }
}
