// Runs under Node, not Bun (see run.ts): Playwright talks to the browser over pipes, and under Bun
// that pipe is sometimes closed mid-run, which leaves the next call waiting forever.
//
// Drives the built web app in headless Chromium and times what a listener waits for: a page
// becoming usable from a cold start, moving between pages, a search keystroke, a scroll, and the
// gap between pressing play and hearing sound.

import { existsSync, readdirSync, writeFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'
import { chromium, type Browser, type BrowserContext, type CDPSession, type Page } from 'playwright-core'
import type { Target } from '../config.ts'
import { median, percentile, summarize, type Summary } from '../stats.ts'

export interface Profile {
  description: string
  viewport: { width: number; height: number }
  mobile: boolean
  /** How many times slower than this machine the CPU is made to run. */
  cpuSlowdown: number
  network: { downMbps: number; upMbps: number; latencyMs: number } | null
}

export const PROFILES: Record<string, Profile> = {
  desktop: {
    description: 'Desktop, 1440×900, this machine\'s CPU, no network limit',
    viewport: { width: 1440, height: 900 },
    mobile: false,
    cpuSlowdown: 1,
    network: null,
  },
  phone: {
    description: 'Phone, 390×844, CPU 4× slower, good Wi-Fi (20 Mbit/s, 40 ms)',
    viewport: { width: 390, height: 844 },
    mobile: true,
    cpuSlowdown: 4,
    network: { downMbps: 20, upMbps: 10, latencyMs: 40 },
  },
  'phone-4g': {
    description: 'Phone, 390×844, CPU 4× slower, mobile data (9 Mbit/s, 85 ms)',
    viewport: { width: 390, height: 844 },
    mobile: true,
    cpuSlowdown: 4,
    network: { downMbps: 9, upMbps: 1.5, latencyMs: 85 },
  },
}

export interface ApiCall {
  path: string
  startMs: number
  durationMs: number
  ttfbMs: number
  transferBytes: number
  decodedBytes: number
}

export interface ScenarioResult {
  profile: string
  name: string
  kind: 'load' | 'interaction'
  /** What the headline number is: "usable", "sound", "worst keystroke"… */
  primaryLabel: string
  primary: Summary
  /** Supporting numbers, each the median of the runs. */
  metrics: Record<string, number | null>
  /** The slowest API calls of the median run, covers excluded. */
  api: ApiCall[]
  /** API requests the page made more than once while opening, in the median run. */
  repeats: Repeat[]
  budgetMs: number | null
  overBudget: string[]
  error?: string
}

export interface WebReport {
  target: string
  startedAt: string
  chromium: string
  profiles: Record<string, string>
  scenarios: ScenarioResult[]
}

export interface WebBenchOptions {
  profiles: string[]
  runs: number
  filter: string | null
  /** Folder for one CPU profile per interaction and browser profile, or null for none. */
  cpuProfileDir: string | null
}

// Runs in the page before any app code. Everything is read back through window.__perf.
const INIT_SCRIPT = `(() => {
  const perf = { fcp: null, lcp: null, cls: 0, longTasks: [], events: [], seenAt: null, playCalledAt: null, playingAt: null, audio: {} }
  window.__perf = perf
  const observe = (type, handle, extra) => {
    try { new PerformanceObserver((list) => list.getEntries().forEach(handle)).observe({ type, buffered: true, ...extra }) } catch {}
  }
  observe('paint', (e) => { if (e.name === 'first-contentful-paint') perf.fcp = e.startTime })
  observe('largest-contentful-paint', (e) => { perf.lcp = e.startTime })
  observe('layout-shift', (e) => { if (!e.hadRecentInput) perf.cls += e.value })
  observe('longtask', (e) => { perf.longTasks.push([e.startTime, e.duration]) })
  observe('event', (e) => { perf.events.push([e.startTime, e.duration, e.name]) }, { durationThreshold: 16 })

  // "Usable" is the frame after the watched condition first holds, not the mutation itself.
  let predicate = null
  const check = () => {
    if (!predicate || perf.seenAt !== null) return
    let ok = false
    try { ok = !!predicate() } catch {}
    if (!ok) return
    predicate = null
    requestAnimationFrame(() => { perf.seenAt = performance.now() })
  }
  perf.watch = (source) => { perf.seenAt = null; predicate = new Function('return (' + source + ')()'); check() }
  new MutationObserver(check).observe(document, { childList: true, subtree: true, attributes: true })

  const play = HTMLMediaElement.prototype.play
  HTMLMediaElement.prototype.play = function () {
    if (perf.playCalledAt === null) perf.playCalledAt = performance.now()
    this.addEventListener('playing', () => { if (perf.playingAt === null) perf.playingAt = performance.now() }, { once: true })
    for (const name of ['loadstart', 'loadedmetadata', 'canplay'])
      this.addEventListener(name, () => { perf.audio[name] ??= performance.now() }, { once: true })
    return play.apply(this, arguments)
  }
  if (window.__perfWatch) perf.watch(window.__perfWatch)
})()`

const has = (selector: string) => `() => document.querySelector(${JSON.stringify(selector)})`
const ALBUM_CARD = 'a[aria-label^="Open album"]'
const TRACK_ROW = 'button[aria-label^="More for "]'
const TRACKS_READY = `() => document.querySelector('input[aria-label="Search tracks"]') && document.querySelector(${JSON.stringify(TRACK_ROW)})`

interface LoadScenario {
  name: string
  path: string
  ready: string
  budget: { desktop: number; phone: number }
}

// One line per page worth a cold-start number. `ready` is a function source that turns truthy
// once the page shows its real content.
const LOADS: LoadScenario[] = [
  { name: 'Open the app (Overview)', path: '/overview', ready: has(ALBUM_CARD), budget: { desktop: 2000, phone: 4000 } },
  { name: 'Open Albums', path: '/library', ready: has(ALBUM_CARD), budget: { desktop: 2000, phone: 4000 } },
  { name: 'Open Tracks', path: '/tracks', ready: TRACKS_READY, budget: { desktop: 2000, phone: 4000 } },
  { name: 'Open Artists', path: '/artists', ready: `() => document.querySelector('[data-mh-content] a[href*="artist="]')`, budget: { desktop: 2000, phone: 4000 } },
]

const INTERACTION_BUDGETS: Record<string, { desktop: number; phone: number }> = {
  'Tracks → Albums': { desktop: 300, phone: 800 },
  'Open an album': { desktop: 300, phone: 800 },
  'Albums → Tracks': { desktop: 300, phone: 800 },
  'Type in track search (worst keystroke)': { desktop: 100, phone: 200 },
  'Scroll the track list (95th percentile frame)': { desktop: 20, phone: 34 },
  'Press play → sound': { desktop: 400, phone: 800 },
}

function findChromium(): string {
  if (process.env.PERF_CHROMIUM) return process.env.PERF_CHROMIUM
  const roots = [process.env.PLAYWRIGHT_BROWSERS_PATH, join(homedir(), '.cache/ms-playwright'), join(homedir(), 'Library/Caches/ms-playwright')]
  for (const root of roots) {
    if (!root || !existsSync(root)) continue
    for (const build of readdirSync(root).filter((d) => /^chromium-\d+$/.test(d)).sort().reverse()) {
      for (const candidate of ['chrome-linux64/chrome', 'chrome-linux/chrome', 'chrome-mac/Chromium.app/Contents/MacOS/Chromium', 'chrome-mac-arm64/Google Chrome for Testing.app/Contents/MacOS/Google Chrome for Testing']) {
        const path = join(root, build, candidate)
        if (existsSync(path)) return path
      }
    }
  }
  const bundled = chromium.executablePath()
  if (existsSync(bundled)) return bundled
  throw new Error('No Chromium found. Install one with "bunx playwright install chromium", or point PERF_CHROMIUM at a Chrome/Chromium binary.')
}

interface Session {
  context: BrowserContext
  page: Page
  cdp: CDPSession
}

async function openSession(browser: Browser, profile: Profile, target: Target, watch: string | null): Promise<Session> {
  const context = await browser.newContext({
    viewport: profile.viewport,
    isMobile: profile.mobile,
    hasTouch: profile.mobile,
    deviceScaleFactor: profile.mobile ? 3 : 1,
  })
  await context.addCookies(
    target.cookies.map((cookie) => ({ name: cookie.slice(0, cookie.indexOf('=')), value: cookie.slice(cookie.indexOf('=') + 1), url: target.webBase! })),
  )
  if (watch) await context.addInitScript(`window.__perfWatch = ${JSON.stringify(watch)}`)
  await context.addInitScript(INIT_SCRIPT)
  const page = await context.newPage()
  const cdp = await context.newCDPSession(page)
  await cdp.send('Performance.enable')
  if (profile.cpuSlowdown > 1) await cdp.send('Emulation.setCPUThrottlingRate', { rate: profile.cpuSlowdown })
  if (profile.network) {
    await cdp.send('Network.enable')
    await cdp.send('Network.emulateNetworkConditions', {
      offline: false,
      latency: profile.network.latencyMs,
      downloadThroughput: (profile.network.downMbps * 1_000_000) / 8,
      uploadThroughput: (profile.network.upMbps * 1_000_000) / 8,
    })
  }
  return { context, page, cdp }
}

/** Resolves with the page's clock at the frame the watched condition first held. */
async function seen(page: Page, timeoutMs: number): Promise<number> {
  const handle = await page.waitForFunction('window.__perf && window.__perf.seenAt', undefined, { timeout: timeoutMs, polling: 50 })
  return (await handle.jsonValue()) as number
}

/** Waits until no request has finished for a moment, so late covers and API calls are counted. */
async function settle(page: Page, maxMs = 20_000) {
  await page.evaluate(async (max) => {
    const started = performance.now()
    let count = -1
    let stableSince = performance.now()
    while (performance.now() - started < max) {
      const now = performance.getEntriesByType('resource').length
      if (now !== count) {
        count = now
        stableSince = performance.now()
      } else if (performance.now() - stableSince > 700) return
      await new Promise((resolve) => setTimeout(resolve, 100))
    }
  }, maxMs)
}

async function cdpMetrics(cdp: CDPSession): Promise<Record<string, number>> {
  const { metrics } = await cdp.send('Performance.getMetrics')
  return Object.fromEntries(metrics.map((m) => [m.name, m.value]))
}

export interface Repeat {
  path: string
  count: number
  transferBytes: number
}

interface LoadRun {
  repeats: Repeat[]
  ready: number
  metrics: Record<string, number | null>
  api: ApiCall[]
}

async function measureLoad(browser: Browser, profile: Profile, target: Target, scenario: LoadScenario): Promise<LoadRun> {
  const { context, page, cdp } = await openSession(browser, profile, target, scenario.ready)
  try {
    await page.goto(`${target.webBase}${scenario.path}`, { waitUntil: 'commit', timeout: 60_000 })
    const ready = await seen(page, 180_000)
    await settle(page)
    // Stay a little longer: a refetch a few seconds after start is still part of opening the page.
    await page.waitForTimeout(4000)
    await settle(page)
    const cpu = await cdpMetrics(cdp)
    const inPage = await page.evaluate(() => {
      const perf = (window as unknown as { __perf: { fcp: number | null; lcp: number | null; cls: number; longTasks: [number, number][] } }).__perf
      const nav = performance.getEntriesByType('navigation')[0] as PerformanceNavigationTiming
      const resources = (performance.getEntriesByType('resource') as PerformanceResourceTiming[]).map((r) => {
        const url = new URL(r.name)
        return {
          path: url.pathname + url.search,
          type: r.initiatorType,
          startMs: r.startTime,
          durationMs: r.duration,
          ttfbMs: r.responseStart > 0 ? r.responseStart - r.startTime : r.duration,
          endMs: r.responseEnd,
          transferBytes: r.transferSize,
          decodedBytes: r.decodedBodySize,
        }
      })
      return {
        ttfb: nav.responseStart,
        documentBytes: nav.transferSize,
        fcp: perf.fcp,
        lcp: perf.lcp,
        cls: perf.cls,
        longTasks: perf.longTasks,
        domNodes: document.getElementsByTagName('*').length,
        resources,
      }
    })

    const isApi = (path: string) => path.startsWith('/api/')
    const isCover = (path: string) => /\/cover(\?|$)/.test(path)
    const script = inPage.resources.filter((r) => /\.js(\?|$)/.test(r.path))
    const style = inPage.resources.filter((r) => /\.css(\?|$)/.test(r.path))
    const api = inPage.resources.filter((r) => isApi(r.path) && !isCover(r.path))
    const covers = inPage.resources.filter((r) => isCover(r.path))
    const sum = (list: { transferBytes: number }[]) => list.reduce((total, r) => total + r.transferBytes, 0)
    const blocking = (until: number) =>
      inPage.longTasks.filter(([start]) => start < until).reduce((total, [, duration]) => total + Math.max(0, duration - 50), 0)

    const byPath = new Map<string, Repeat>()
    for (const call of api) {
      const entry = byPath.get(call.path) ?? { path: call.path, count: 0, transferBytes: 0 }
      entry.count++
      entry.transferBytes += call.transferBytes
      byPath.set(call.path, entry)
    }

    return {
      repeats: [...byPath.values()].filter((r) => r.count > 1).sort((a, b) => b.transferBytes - a.transferBytes),
      ready,
      metrics: {
        'Server responded (TTFB)': inPage.ttfb,
        'First paint (FCP)': inPage.fcp,
        'Largest paint (LCP)': inPage.lcp,
        'Covers finished': covers.length ? Math.max(...covers.map((r) => r.endMs)) : null,
        'Main thread blocked before usable': blocking(ready),
        'Script time': (cpu.ScriptDuration ?? 0) * 1000,
        'Layout and style time': ((cpu.LayoutDuration ?? 0) + (cpu.RecalcStyleDuration ?? 0)) * 1000,
        'JS heap': cpu.JSHeapUsedSize ?? null,
        'DOM nodes': inPage.domNodes,
        'Layout shift (CLS)': inPage.cls,
        Requests: inPage.resources.length + 1,
        'Transferred, total': inPage.documentBytes + sum(inPage.resources),
        'Transferred, JavaScript': sum(script),
        'Transferred, CSS': sum(style),
        'Transferred, API': sum(api),
        'Transferred, covers': sum(covers),
        'API calls': api.length,
        'Cover requests': covers.length,
      },
      api: api
        .sort((a, b) => b.durationMs - a.durationMs)
        .slice(0, 10)
        .map(({ path, startMs, durationMs, ttfbMs, transferBytes, decodedBytes }) => ({
          // Ids and device names vary per run; the route is what matters.
          path: path.replace(/\?.*$/, (query) => (query.length > 40 ? '?…' : query)),
          startMs, durationMs, ttfbMs, transferBytes, decodedBytes,
        })),
    }
  } finally {
    await context.close()
  }
}

type Interactions = Record<string, { value: number; metrics: Record<string, number | null>; api?: ApiCall[] }>

/** Client-side navigation the way a link does it, identical in every layout: the router handles any same-origin anchor. */
const followLink = (path: string, ready: string) => `(() => {
  const link = document.createElement('a')
  link.href = ${JSON.stringify(path)}
  link.style.display = 'none'
  document.body.appendChild(link)
  window.__perf.watch(${JSON.stringify(ready)})
  const started = performance.now()
  link.click()
  link.remove()
  return started
})()`

/** Runs the steps in order; a step that fails ends the sequence but keeps what was measured before it. */
async function measureInteractions(browser: Browser, profile: Profile, target: Target, cpuProfilePrefix: string | null): Promise<{ steps: Interactions; failure?: string }> {
  const { context, page, cdp } = await openSession(browser, profile, target, TRACKS_READY)
  const out: Interactions = {}
  try {
    await page.goto(`${target.webBase}/tracks`, { waitUntil: 'commit', timeout: 60_000 })
    await seen(page, 180_000)
    await settle(page)

    if (cpuProfilePrefix) {
      await cdp.send('Profiler.enable')
      await cdp.send('Profiler.setSamplingInterval', { interval: 500 })
      await cdp.send('Profiler.start')
    }
    /** Ends the profile of the step just measured, as its own file, and starts the next one. */
    const endProfile = async (step: string) => {
      if (!cpuProfilePrefix) return
      const { profile: cpuProfile } = await cdp.send('Profiler.stop')
      writeFileSync(`${cpuProfilePrefix}-${step}.cpuprofile`, JSON.stringify(cpuProfile))
      await cdp.send('Profiler.start')
    }
    /** Main-thread time in long tasks since `from`: what kept the page from answering. */
    const blockedSince = (from: number) =>
      page.evaluate((start) => {
        const tasks = (window as unknown as { __perf: { longTasks: [number, number][] } }).__perf.longTasks
        return tasks.filter(([at]) => at >= start - 1).reduce((total, [, duration]) => total + duration, 0)
      }, from)

    const navigate = async (name: string, script: string) => {
      const started = (await page.evaluate(script)) as number
      const value = (await seen(page, 60_000)) - started
      out[name] = { value, metrics: { 'Main thread busy': await blockedSince(started) } }
      await settle(page, 8000)
    }
    await navigate('Tracks → Albums', followLink('/library', has(ALBUM_CARD)))
    await endProfile('tracks-to-albums')
    await navigate('Open an album', `(() => {
      window.__perf.watch(${JSON.stringify(has(TRACK_ROW))})
      const started = performance.now()
      document.querySelector(${JSON.stringify(ALBUM_CARD)}).click()
      return started
    })()`)
    await endProfile('open-album')
    await navigate('Albums → Tracks', followLink('/tracks', TRACKS_READY))
    await endProfile('albums-to-tracks')

    // Real key events, so the browser's own Event Timing measures input-to-next-frame.
    await page.evaluate(() => { (window as unknown as { __perf: { events: unknown[]; longTasks: unknown[] } }).__perf.events.length = 0 })
    await page.click('input[aria-label="Search tracks"]')
    await page.keyboard.type('night river of', { delay: 70 })
    await page.waitForTimeout(600)
    const typing = await page.evaluate(() => (window as unknown as { __perf: { events: [number, number, string][] } }).__perf.events.filter((e) => e[2].startsWith('key') || e[2] === 'input'))
    const durations = typing.map((e) => e[1])
    out['Type in track search (worst keystroke)'] = {
      value: durations.length ? Math.max(...durations) : 16,
      metrics: { 'Typical keystroke': durations.length ? median(durations) : 16 },
    }
    await endProfile('search')
    await page.fill('input[aria-label="Search tracks"]', '')
    await page.waitForTimeout(400)

    const frames = (await page.evaluate(async (rowSelector) => {
      // The list's own scroller: the nearest scrollable ancestor of a row.
      let scroller: Element | null = document.querySelector(rowSelector)
      while (scroller && !(scroller.scrollHeight > scroller.clientHeight + 200 && /auto|scroll/.test(getComputedStyle(scroller).overflowY)))
        scroller = scroller.parentElement
      const element = scroller ?? document.scrollingElement!
      const deltas: number[] = []
      let last = performance.now()
      const started = last
      await new Promise<void>((resolve) => {
        const step = (now: number) => {
          deltas.push(now - last)
          last = now
          element.scrollTop += 90
          if (now - started < 2500) requestAnimationFrame(step)
          else resolve()
        }
        requestAnimationFrame(step)
      })
      element.scrollTop = 0
      return deltas.slice(1)
    }, TRACK_ROW)) as number[]
    await endProfile('scroll')
    out['Scroll the track list (95th percentile frame)'] = {
      value: percentile(frames, 95),
      metrics: {
        'Typical frame': median(frames),
        'Slow frames (over 34 ms)': frames.length ? (frames.filter((f) => f > 34).length / frames.length) * 100 : 0,
      },
    }
    await page.waitForTimeout(400)

    const pressed = (await page.evaluate((rowSelector) => {
      const perf = (window as unknown as { __perf: { playCalledAt: number | null; playingAt: number | null; audio: Record<string, number> } }).__perf
      perf.playCalledAt = null
      perf.playingAt = null
      perf.audio = {}
      // The row's play button where there is one (pointer layouts), the row itself on touch. Never
      // the song the player already holds: tapping that one opens Now Playing instead of playing.
      const controls = [...document.querySelectorAll<HTMLElement>('button[aria-label="Play track"], [role="listitem"] button[data-row-main]')]
      const control = controls.find((el, index) => index >= 2 && !el.closest('[aria-current]')) ?? controls[0]
      if (!control) throw new Error(`No playable row next to ${rowSelector}`)
      const started = performance.now()
      control.click()
      return started
    }, TRACK_ROW)) as number
    const playingHandle = await page.waitForFunction('window.__perf.playingAt', undefined, { timeout: 30_000, polling: 25 })
    const playingAt = (await playingHandle.jsonValue()) as number
    const audio = await page.evaluate((from) => {
      const perf = (window as unknown as { __perf: { playCalledAt: number | null; audio: Record<string, number> } }).__perf
      const since = (at: number | null | undefined) => (at != null ? at - from : null)
      // What the app asked the server for between the press and the sound, slowest first.
      const calls = (performance.getEntriesByType('resource') as PerformanceResourceTiming[])
        .filter((r) => r.startTime >= from && new URL(r.name).pathname.startsWith('/api/') && !/\/cover(\?|$)/.test(r.name))
        .map((r) => ({
          path: new URL(r.name).pathname,
          startMs: r.startTime - from,
          durationMs: r.duration,
          ttfbMs: r.responseStart > 0 ? r.responseStart - r.startTime : r.duration,
          transferBytes: r.transferSize,
          decodedBytes: r.decodedBodySize,
        }))
        .sort((a, b) => b.durationMs - a.durationMs)
        .slice(0, 8)
      return {
        asked: since(perf.playCalledAt),
        loadStart: since(perf.audio.loadstart),
        metadata: since(perf.audio.loadedmetadata),
        canPlay: since(perf.audio.canplay),
        calls,
      }
    }, pressed)
    out['Press play → sound'] = {
      value: playingAt - pressed,
      metrics: {
        'Main thread busy': await blockedSince(pressed),
        'Player asks for audio': audio.asked,
        'Audio request starts': audio.loadStart,
        'Audio metadata read': audio.metadata,
        'Audio can play': audio.canPlay,
      },
      api: audio.calls,
    }
    await endProfile('play')
    return { steps: out }
  } catch (error) {
    return { steps: out, failure: (error as Error).message.split('\n')[0] }
  } finally {
    await context.close()
  }
}

function medianMetrics(runs: Record<string, number | null>[]): Record<string, number | null> {
  const keys = [...new Set(runs.flatMap((run) => Object.keys(run)))]
  return Object.fromEntries(
    keys.map((key) => {
      const values = runs.map((run) => run[key]).filter((v): v is number => v != null)
      return [key, values.length ? median(values) : null]
    }),
  )
}

export async function runWebBench(target: Target, options: WebBenchOptions): Promise<WebReport> {
  if (!target.webBase) throw new Error('This target has no web app to open.')
  const executablePath = findChromium()
  const report: WebReport = {
    target: target.label,
    startedAt: new Date().toISOString(),
    chromium: executablePath,
    profiles: {},
    scenarios: [],
  }
  const wanted = (name: string) => !options.filter || name.toLowerCase().includes(options.filter.toLowerCase())

  for (const profileName of options.profiles) {
    const profile = PROFILES[profileName]
    if (!profile) throw new Error(`Unknown profile "${profileName}". Use: ${Object.keys(PROFILES).join(', ')}.`)
    report.profiles[profileName] = profile.description
    const budgetKey = profile.mobile ? 'phone' : 'desktop'
    // One browser per profile, a fresh context per run: every load starts with an empty cache.
    const browser = await chromium.launch({ executablePath, args: ['--autoplay-policy=no-user-gesture-required'] })
    try {
      for (const scenario of LOADS.filter((s) => wanted(s.name))) {
        process.stdout.write(`[web] ${profileName} · ${scenario.name} … `)
        const result: ScenarioResult = {
          profile: profileName, name: scenario.name, kind: 'load', primaryLabel: 'usable',
          primary: summarize([]), metrics: {}, api: [], repeats: [], budgetMs: scenario.budget[budgetKey], overBudget: [],
        }
        try {
          const runs: LoadRun[] = []
          for (let i = 0; i < options.runs; i++) runs.push(await measureLoad(browser, profile, target, scenario))
          result.primary = summarize(runs.map((r) => r.ready))
          result.metrics = medianMetrics(runs.map((r) => r.metrics))
          const middle = [...runs].sort((a, b) => a.ready - b.ready)[Math.floor(runs.length / 2)]!
          result.api = middle.api
          result.repeats = middle.repeats
          console.log(`${Math.round(result.primary.median)} ms`)
        } catch (error) {
          result.error = (error as Error).message.split('\n')[0]
          console.log(`failed: ${result.error}`)
        }
        report.scenarios.push(result)
      }

      if (Object.keys(INTERACTION_BUDGETS).some(wanted)) {
        process.stdout.write(`[web] ${profileName} · interactions … `)
        const runs: Interactions[] = []
        let failure: string | undefined
        for (let i = 0; i < options.runs; i++) {
          // Profiling slows the page down, so only the first run carries it.
          const cpuProfilePrefix = i === 0 && options.cpuProfileDir ? join(options.cpuProfileDir, `${profileName}`) : null
          const run = await measureInteractions(browser, profile, target, cpuProfilePrefix)
          runs.push(run.steps)
          failure ??= run.failure
        }
        console.log(failure ? `stopped early: ${failure}` : 'done')
        for (const name of Object.keys(INTERACTION_BUDGETS).filter(wanted)) {
          const samples = runs.map((run) => run[name]).filter((s) => s != null)
          report.scenarios.push({
            profile: profileName, name, kind: 'interaction',
            primaryLabel: name.includes('→ sound') ? 'sound' : name.includes('keystroke') ? 'input to frame' : name.includes('frame') ? 'frame' : 'usable',
            primary: summarize(samples.map((s) => s.value)),
            metrics: medianMetrics(samples.map((s) => s.metrics)),
            api: samples.find((sample) => sample.api?.length)?.api ?? [],
            repeats: [],
            budgetMs: INTERACTION_BUDGETS[name]![budgetKey],
            overBudget: [],
            error: samples.length === 0 ? (failure ?? 'not reached') : undefined,
          })
        }
      }
    } finally {
      await browser.close()
    }
  }

  for (const scenario of report.scenarios) {
    if (scenario.budgetMs != null && scenario.primary.median > scenario.budgetMs)
      scenario.overBudget.push(`${Math.round(scenario.primary.median)} ms over ${scenario.budgetMs} ms`)
  }
  return report
}
