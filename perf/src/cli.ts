#!/usr/bin/env bun
// Entry point of the performance suite. `bun perf help` lists the commands; perf/README.md
// explains what each number means.

import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { runApiBench, type ApiReport } from './api/bench.ts'
import { compare } from './compare.ts'
import { PERF_ROOT, SIZES, stackConfig, type SizeName, type StackConfig, type Target } from './config.ts'
import { acquire, describeHolder, ref, releaseLock, releaseMine, running, updateLock, type LockHolder } from './lock.ts'
import { FOOTPRINT_MB } from './memory.ts'
import { renderHtml, renderMarkdown, type RunResult } from './report.ts'
import { scoped, unitName, useScopes } from './scope.ts'
import { down, target as localTarget, up } from './stack.ts'
import { PROFILES, type WebBenchOptions, type WebReport } from './web/bench.ts'

const HELP = `MusicHoarder performance suite

  bun perf all      [options]   start a local stack, measure the API and the web app, write a report, stop
  bun perf up       [options]   start the local stack and leave it running (it keeps the machine's stack lock until "down")
  bun perf measure  [options]   measure both against the running stack (or --url)
  bun perf api      [options]   measure the API (against the running stack, or --url)
  bun perf web      [options]   measure the web app in a browser (against the running stack, or --url)
  bun perf down     [--purge]   stop the local stack (--purge also deletes its database and files)
  bun perf compare <before> <after>   what changed between two runs (folders or labels under results/)
  bun perf report <run>         write a run's report again from its result.json
  bun perf list                 the runs under results/

Options
  --size tiny|small|medium|large   library to seed: ${Object.entries(SIZES).map(([k, v]) => `${k} ${v.toLocaleString('en')}`).join(', ')} songs (default medium)
  --label <name>                   name the run (default: the git branch)
  --no-build                       reuse the last API and frontend builds
  --keep                           with "all": leave the stack running afterwards (and the lock taken, until "down")
  --no-wait                        with "up"/"all": fail at once when another stack holds the machine's lock, instead of waiting
  --force                          with "up"/"all": start even when the machine looks short of memory
  --scope                          run the API, the frontend and Chromium in systemd scopes with memory limits
                                   (Linux with systemd only; same as PERF_SYSTEMD_SCOPE=1)
  --iterations <n>                 samples per endpoint (default 12)
  --concurrency <n>                parallel callers in the load pass, 0 to skip (default 8)
  --only <text>                    only endpoints / scenarios whose name contains this
  --profiles <a,b>                 browser profiles: ${Object.keys(PROFILES).join(', ')} (default desktop,phone)
  --runs <n>                       repeats of each browser scenario (default 3)
  --cpu-profile                    also save a CPU profile per interaction (open one in Chrome DevTools)
  --strict                         exit 1 when anything is over its budget

Any instance, read-only (GET requests only)
  --url <origin>                   e.g. https://music.example.com
  --cookie <name=value>            its session cookie; or set PERF_COOKIE
`

function parseArgs(argv: string[]) {
  const flags = new Map<string, string>()
  const positional: string[] = []
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i]!
    if (!arg.startsWith('--')) positional.push(arg)
    else if (i + 1 < argv.length && !argv[i + 1]!.startsWith('--')) flags.set(arg.slice(2), argv[++i]!)
    else flags.set(arg.slice(2), 'true')
  }
  return { flags, positional }
}

function git(args: string[]): string {
  const result = Bun.spawnSync(['git', ...args], { cwd: PERF_ROOT })
  return result.success ? result.stdout.toString().trim() : ''
}

const resultsRoot = join(PERF_ROOT, 'results')

/**
 * The browser scenarios run in a Node child process: Playwright drives Chromium over pipes, which
 * Bun does not keep open reliably. Node runs the same TypeScript as it is (22.18 or newer).
 */
function runWebInNode(dir: string, target: Target, options: WebBenchOptions, scope: { cfg: StackConfig } | null): WebReport {
  const job = join(dir, 'web-job.json')
  const out = join(dir, 'web-report.json')
  // The job holds the session cookie, so it is removed again whatever happens.
  writeFileSync(job, JSON.stringify({ target, options, out }), { mode: 0o600 })
  try {
    // Chromium is a child of this Node process, so a scope around Node holds the browser too.
    const args = [join(PERF_ROOT, 'src/web/run.ts'), job]
    const [command, argv] = scope
      ? scoped('node', args, { name: unitName(['mh-perf', scope.cfg.instance, scope.cfg.size, 'web']), peakMb: FOOTPRINT_MB[scope.cfg.size].web })
      : ['node', args]
    const result = Bun.spawnSync([command, ...argv], { stdio: ['ignore', 'inherit', 'inherit'] })
    if (!result.success) throw new Error('The browser scenarios failed (see above). They need Node 22.18 or newer on PATH.')
    return JSON.parse(readFileSync(out, 'utf8')) as WebReport
  } finally {
    rmSync(job, { force: true })
    rmSync(out, { force: true })
  }
}

function resolveRun(name: string): string {
  if (existsSync(join(name, 'result.json'))) return resolve(name)
  const runs = existsSync(resultsRoot) ? readdirSync(resultsRoot).sort() : []
  const match = runs.filter((run) => run === name || run.endsWith(`_${name}`)).pop()
  if (!match) throw new Error(`No run named "${name}" under ${resultsRoot}. "bun perf list" shows them.`)
  return join(resultsRoot, match)
}

const loadRun = (dir: string) => JSON.parse(readFileSync(join(dir, 'result.json'), 'utf8')) as RunResult

/**
 * Takes the machine's stack lock for `up`/`all`, waiting for another stack to finish unless told
 * not to. A stack this same work dir left running is taken over, since `up` restarts it anyway.
 * The lock goes with this process when it exits, unless `handOver` gave it to the stack.
 */
async function takeStackLock(cfg: StackConfig, command: string, wait: boolean) {
  const me: LockHolder = {
    owner: ref(process.pid),
    stack: [],
    command,
    workDir: cfg.workDir,
    size: cfg.size,
    startedAt: new Date().toISOString(),
  }
  await acquire(cfg.lockFile, me, {
    wait,
    takeOver: (holder) => holder.workDir === cfg.workDir && !(holder.owner && running(holder.owner)),
    onBusy: (holder) =>
      console.log(`[lock] waiting for ${holder ? describeHolder(holder) : 'a perf command that is just starting'} (--no-wait fails instead)`),
  })
  process.on('exit', () => releaseMine(cfg.lockFile))
  // Ctrl-C or a kill: stop what this run started, then let the lock go.
  let stopping = false
  const stop = (signal: NodeJS.Signals) => {
    if (stopping) return
    stopping = true
    console.log(`\n[stack] ${signal}: stopping the stack`)
    down(cfg, { quiet: true }).finally(() => process.exit(signal === 'SIGINT' ? 130 : 143))
  }
  const signals: NodeJS.Signals[] = ['SIGINT', 'SIGTERM', 'SIGHUP']
  for (const signal of signals) process.on(signal, stop)
  return {
    /** The stack's processes keep the lock alive from here on. */
    follow: (pids: number[]) => updateLock(cfg.lockFile, (holder) => ({ ...holder, stack: pids.map(ref) })),
    /** The stack stays up after this process: the lock is the stack's now, released by `down`. */
    handOver: () => {
      for (const signal of signals) process.off(signal, stop)
      updateLock(cfg.lockFile, (holder) => ({ ...holder, owner: null }))
    },
  }
}

/** `down`'s release: this work dir's lock, unless a live command elsewhere still owns it (it releases it itself). */
const releaseStackLock = (cfg: StackConfig) =>
  releaseLock(cfg.lockFile, (holder) => holder.workDir === cfg.workDir && !(holder.owner && holder.owner.pid !== process.pid && running(holder.owner)))

async function main() {
  const [command = 'help', ...rest] = process.argv.slice(2)
  const { flags, positional } = parseArgs(rest)
  const size = (flags.get('size') ?? 'medium') as SizeName
  if (!(size in SIZES)) throw new Error(`Unknown size "${size}". Use one of: ${Object.keys(SIZES).join(', ')}.`)
  const cfg = stackConfig(size)
  const scopes = useScopes(flags.has('scope'))
  const upOptions = (lock: { follow: (pids: number[]) => void }) => ({
    build: !flags.has('no-build'),
    force: flags.has('force'),
    scopes,
    onStarted: lock.follow,
  })

  const remoteUrl = flags.get('url')?.replace(/\/$/, '')
  const remoteTarget = (): Target => {
    const cookie = flags.get('cookie') ?? process.env.PERF_COOKIE
    if (!cookie) throw new Error('A --url target needs its session cookie: --cookie mh_session=… or PERF_COOKIE.')
    return {
      apiBase: `${remoteUrl}/api/mh`,
      webBase: remoteUrl!,
      proxyBase: null,
      headers: { cookie },
      cookies: cookie.split(';').map((c) => c.trim()).filter(Boolean),
      local: null,
      label: new URL(remoteUrl!).host,
    }
  }

  const measure = async (target: Target, parts: { api: boolean; web: boolean }) => {
    const label = flags.get('label') ?? (git(['rev-parse', '--abbrev-ref', 'HEAD']) || 'run')
    const run: RunResult = {
      label,
      target: target.label,
      commit: git(['rev-parse', '--short', 'HEAD']),
      dirty: git(['status', '--porcelain', '--', '..', ':!perf']) !== '',
      startedAt: new Date().toISOString(),
      api: null,
      web: null,
    }
    const stamp = run.startedAt.replace(/[-:]/g, '').replace('T', '-').slice(0, 15)
    const dir = join(resultsRoot, `${stamp}_${label.replace(/[^A-Za-z0-9._-]+/g, '-')}`)
    mkdirSync(dir, { recursive: true })
    if (parts.api) {
      run.api = (await runApiBench(target, {
        iterations: Number(flags.get('iterations') ?? 12),
        timeBudgetMs: 8000,
        concurrency: Number(flags.get('concurrency') ?? 8),
        filter: flags.get('only') ?? null,
      })) satisfies ApiReport
    }
    if (parts.web) {
      run.web = runWebInNode(dir, target, {
        profiles: (flags.get('profiles') ?? 'desktop,phone').split(','),
        runs: Number(flags.get('runs') ?? 3),
        filter: flags.get('only') ?? null,
        cpuProfileDir: flags.has('cpu-profile') ? dir : null,
      }, scopes ? { cfg } : null)
    }

    writeFileSync(join(dir, 'result.json'), JSON.stringify(run, null, 2))
    const markdown = renderMarkdown(run)
    writeFileSync(join(dir, 'report.md'), markdown)
    writeFileSync(join(dir, 'report.html'), renderHtml(run))
    console.log(`\n${markdown}\nWritten to ${dir}`)

    const over = [
      ...(run.api?.endpoints.filter((e) => e.overBudget.length || e.error) ?? []),
      ...(run.web?.scenarios.filter((s) => s.overBudget.length || s.error) ?? []),
    ]
    if (flags.has('strict') && over.length) process.exitCode = 1
  }

  switch (command) {
    case 'up': {
      const lock = await takeStackLock(cfg, 'up', !flags.has('no-wait'))
      try {
        await up(cfg, upOptions(lock))
      } catch (error) {
        // Nothing half-started stays behind holding memory (or the lock).
        await down(cfg, { quiet: true })
        throw error
      }
      lock.handOver()
      console.log(`API ${cfg.apiUrl} · web ${cfg.webUrl} · logs ${join(cfg.workDir, 'logs')}`)
      console.log(`This stack holds the machine's perf lock until "bun perf down --size ${cfg.size}".`)
      break
    }
    case 'down':
      await down(cfg, { purge: flags.has('purge') })
      if (releaseStackLock(cfg)) console.log('[lock] released the machine\'s perf lock')
      break
    case 'measure':
    case 'api':
    case 'web':
      await measure(remoteUrl ? remoteTarget() : localTarget(cfg), { api: command !== 'web', web: command !== 'api' })
      break
    case 'all': {
      if (remoteUrl) {
        await measure(remoteTarget(), { api: true, web: true })
        break
      }
      const lock = await takeStackLock(cfg, 'all', !flags.has('no-wait'))
      let started = false
      try {
        const target = await up(cfg, upOptions(lock))
        started = true
        await measure(target, { api: true, web: true })
      } finally {
        // --keep leaves a stack that came up running, even when measuring it failed.
        if (started && flags.has('keep')) lock.handOver()
        else await down(cfg)
      }
      break
    }
    case 'compare': {
      if (positional.length !== 2) throw new Error('compare takes two runs: bun perf compare <before> <after>')
      console.log(compare(loadRun(resolveRun(positional[0]!)), loadRun(resolveRun(positional[1]!))))
      break
    }
    case 'report': {
      // Re-render a run's report from its result.json, e.g. after the report itself changed.
      if (positional.length !== 1) throw new Error('report takes one run: bun perf report <run>')
      const dir = resolveRun(positional[0]!)
      const run = loadRun(dir)
      writeFileSync(join(dir, 'report.md'), renderMarkdown(run))
      writeFileSync(join(dir, 'report.html'), renderHtml(run))
      console.log(`${renderMarkdown(run)}\nWritten to ${dir}`)
      break
    }
    case 'list':
      for (const run of existsSync(resultsRoot) ? readdirSync(resultsRoot).sort() : []) console.log(run)
      break
    default:
      console.log(HELP)
  }
}

main().catch((error) => {
  console.error(`\n${(error as Error).message}`)
  process.exit(1)
})
