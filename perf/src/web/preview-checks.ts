// Checks a deployed instance (a PR preview, or production) for what the Modern Web Guidance audit
// put in place, in headless Chromium: the security headers, the report-only CSP and its report
// endpoint, the landing hero's loading, the login field's passkey autofill, focus outlines under
// forced colors, and — signed in as the demo account — one <main> and only native progress bars on
// the app pages, with no CSP violations or script errors anywhere.
//
//   node src/web/preview-checks.ts https://pr-123.preview.example.com
//
// Prints one PASS/FAIL line per check and exits 1 when any fails. Runs under Node (like run.ts),
// since Playwright loses its browser pipe under Bun. Chromium: PERF_CHROMIUM, else the one
// `node node_modules/playwright-core/cli.js install chromium` puts in place.
import { chromium, type Browser, type BrowserContext, type Page } from 'playwright-core'

const base = (process.argv[2] ?? '').replace(/\/+$/, '')
if (!/^https?:\/\//.test(base)) {
  console.error('Usage: node src/web/preview-checks.ts <base url>')
  process.exit(64)
}

const results: { name: string; ok: boolean }[] = []
function check(name: string, ok: boolean, detail = ''): void {
  results.push({ name, ok })
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? `  — ${detail}` : ''}`)
}

async function finish(browser?: Browser): Promise<never> {
  await browser?.close()
  const failed = results.filter((r) => !r.ok).length
  console.log(`\n${results.length - failed} passed, ${failed} failed`)
  process.exit(failed ? 1 : 0)
}

// ── 1. Response headers ──────────────────────────────────────────────────────────────────────────
const isHttps = base.startsWith('https://')
for (const path of ['/', '/login', '/api/mh/api/auth/me']) {
  const res = await fetch(base + path, { headers: { accept: 'text/html' }, redirect: 'manual' })
  const h = res.headers
  const want: Record<string, (v: string | null) => boolean> = {
    'x-content-type-options': (v) => v === 'nosniff',
    'referrer-policy': (v) => v === 'strict-origin-when-cross-origin',
    'x-frame-options': (v) => v === 'DENY',
    'content-security-policy': (v) => !!v?.includes("frame-ancestors 'none'"),
    'cross-origin-opener-policy': (v) => v === 'same-origin-allow-popups',
    'permissions-policy': (v) => !!v?.includes('camera=()'),
    ...(isHttps ? { 'strict-transport-security': (v: string | null) => /^max-age=\d+/.test(v ?? '') } : {}),
  }
  const wrong = Object.entries(want)
    .filter(([name, ok]) => !ok(h.get(name)))
    .map(([name]) => name)
  check(`headers on ${path} (${res.status})`, wrong.length === 0, wrong.length ? `missing or wrong: ${wrong.join(', ')}` : '')
  if (!path.startsWith('/api/')) {
    const policy = h.get('content-security-policy-report-only') ?? ''
    check(
      `report-only CSP on ${path}`,
      /script-src [^;]*'nonce-/.test(policy) &&
        policy.includes('report-uri /api/csp-report') &&
        (h.get('reporting-endpoints') ?? '').includes('/api/csp-report'),
      policy ? '' : 'no Content-Security-Policy-Report-Only header',
    )
  }
}

// ── 2. CSP report endpoint ───────────────────────────────────────────────────────────────────────
{
  const res = await fetch(base + '/api/csp-report', {
    method: 'POST',
    headers: { 'content-type': 'application/csp-report' },
    body: JSON.stringify({
      'csp-report': {
        'document-uri': `${base}/preview-checks`,
        'effective-directive': 'img-src',
        'blocked-uri': 'inline',
        disposition: 'report',
      },
    }),
  })
  check('POST /api/csp-report answers 204', res.status === 204, `got ${res.status}`)
}

const browser = await chromium.launch(process.env.PERF_CHROMIUM ? { executablePath: process.env.PERF_CHROMIUM } : {})

interface Watched {
  page: Page
  violations: string[]
  errors: string[]
}

/** A page that records CSP violations (report-only still fires the event) and script errors. */
async function watched(context: BrowserContext): Promise<Watched> {
  const page = await context.newPage()
  const violations: string[] = []
  const errors: string[] = []
  await page.exposeFunction('__mhCspViolation', (v: string) => violations.push(v))
  await page.addInitScript(() => {
    document.addEventListener('securitypolicyviolation', (e) =>
      (window as unknown as { __mhCspViolation: (v: string) => void }).__mhCspViolation(
        `${e.effectiveDirective} ${e.blockedURI} @ ${location.pathname}`,
      ),
    )
  })
  page.on('console', (m) => {
    if (m.type() === 'error') errors.push(m.text().slice(0, 160))
  })
  page.on('pageerror', (e) => errors.push(`pageerror: ${e.message.slice(0, 160)}`))
  return { page, violations, errors }
}

/** Failed resource loads (an API 401/404) are not script errors; they are reported, not counted. */
function scriptErrors(errors: string[]): string[] {
  return errors.filter((e) => !/Failed to load resource/.test(e))
}

// ── 3. Landing and login, light and dark ─────────────────────────────────────────────────────────
for (const scheme of ['light', 'dark'] as const) {
  const context = await browser.newContext({ colorScheme: scheme, viewport: { width: 1280, height: 900 } })
  const { page, violations, errors } = await watched(context)
  const heroFiles = new Set<string>()
  page.on('request', (r) => {
    if (r.url().includes('/screenshots/hero')) heroFiles.add(r.url().split('/').pop() ?? '')
  })

  await page.goto(base + '/', { waitUntil: 'networkidle' })
  const landing = await page.evaluate(() => {
    const hero = [...document.querySelectorAll<HTMLImageElement>('img[src*="/screenshots/hero"]')].find(
      (img) => img.offsetParent !== null,
    )
    return {
      dark: document.documentElement.classList.contains('dark'),
      heroFile: hero?.currentSrc.split('/').pop() ?? null,
      fetchpriority: hero?.getAttribute('fetchpriority') ?? null,
      loading: hero?.getAttribute('loading') ?? null,
    }
  })
  check(`[${scheme}] theme class matches the scheme on the first frame`, landing.dark === (scheme === 'dark'))
  const heroPattern = scheme === 'dark' ? /^hero-dark/ : /^hero(-1200)?\.webp$/
  check(
    `[${scheme}] hero shows the theme's file, eager, high priority`,
    heroPattern.test(landing.heroFile ?? '') && landing.fetchpriority === 'high' && landing.loading === null,
    JSON.stringify(landing),
  )
  check(`[${scheme}] one hero file downloaded`, heroFiles.size === 1, [...heroFiles].join(', '))

  await page.goto(base + '/login', { waitUntil: 'networkidle' })
  const autocomplete = await page.getAttribute('#login-email', 'autocomplete')
  check(`[${scheme}] login email field offers passkeys`, autocomplete === 'email webauthn', String(autocomplete))
  check(`[${scheme}] no CSP violations on / and /login`, violations.length === 0, violations.join(' | '))
  check(`[${scheme}] no script errors on / and /login`, scriptErrors(errors).length === 0, scriptErrors(errors).join(' | '))
  await context.close()
}

// ── 4. Focus outline under forced colors ─────────────────────────────────────────────────────────
for (const forcedColors of ['none', 'active'] as const) {
  const context = await browser.newContext({ forcedColors })
  const page = await context.newPage()
  await page.goto(base + '/login', { waitUntil: 'networkidle' })
  await page.focus('#login-email')
  await page.keyboard.press('Tab')
  const style = await page.evaluate(() => (document.activeElement ? getComputedStyle(document.activeElement).outlineStyle : ''))
  check(`focused button outline, forced colors ${forcedColors}`, forcedColors === 'none' || style !== 'none', `outline-style: ${style}`)
  await context.close()
}

// ── 5. App pages, signed in as the demo account ──────────────────────────────────────────────────
{
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 } })
  const { page, violations, errors } = await watched(context)
  await page.goto(base + '/login', { waitUntil: 'networkidle' })
  const demo = page.getByRole('button', { name: /demo/i }).first()
  if ((await demo.count()) === 0) {
    check('demo sign-in available', false, 'no demo button on /login')
    await finish(browser)
  }
  const signedIn = await Promise.all([
    page.waitForURL((u) => !u.pathname.startsWith('/login'), { timeout: 30_000 }),
    demo.click(),
  ]).then(
    () => true,
    () => false,
  )
  check('demo sign-in', signedIn, new URL(page.url()).pathname)
  if (!signedIn) await finish(browser)

  // A Chromium (app) document preloads the library (frontend `#lib/server/library-preload`), and the
  // store's fetch() takes the preload: one Resource Timing entry per list, started by the `link`.
  // A second, `fetch`-initiated entry would mean the store asked for a different URL than the
  // header preloads, and every cold start downloads the library twice.
  {
    const libraryUrls = ['/api/mh/songs?includeDeleted=false', '/api/mh/api/albums']
    const doc = await page.goto(base + '/overview', { waitUntil: 'networkidle' })
    const link = (await doc?.allHeaders())?.link ?? ''
    check(
      'app document preloads the library',
      libraryUrls.every((url) => link.includes(`<${url}>; rel=preload; as=fetch; crossorigin`)),
      link ? `Link: ${link}` : 'no Link header',
    )
    const initiators = await page.evaluate(
      (urls) =>
        urls.map((url) =>
          performance
            .getEntriesByType('resource')
            .filter((e) => {
              const u = new URL(e.name)
              return u.pathname + u.search === url
            })
            .map((e) => (e as PerformanceResourceTiming).initiatorType),
        ),
      libraryUrls,
    )
    check(
      'library preload used, downloaded once',
      initiators.every((list) => list.length === 1 && list[0] === 'link'),
      JSON.stringify(initiators),
    )
  }

  for (const path of ['/overview', '/library', '/artists', '/tracks', '/playlists', '/pipeline', '/quality', '/playlist-sync', '/settings']) {
    await page.goto(base + path, { waitUntil: 'networkidle' })
    await page.waitForTimeout(500)
    const info = await page.evaluate(() => ({
      path: location.pathname,
      mains: document.querySelectorAll('main').length,
      handMadeBars: document.querySelectorAll('[role="progressbar"]:not(progress)').length,
      progress: document.querySelectorAll('progress').length,
    }))
    check(`${path}: one <main>, no hand-made progress bars`, info.mains === 1 && info.handMadeBars === 0, JSON.stringify(info))
  }
  // Installed iOS apps have no browser Back gesture. Exercise the interactive page stack:
  // cancelling must keep the route; committing must reveal live content and remove all copies.
  await page.setViewportSize({ width: 393, height: 852 })
  await page.addInitScript(() => Object.defineProperty(navigator, 'standalone', { value: true }))
  await page.goto(base + '/overview', { waitUntil: 'networkidle' })
  const scrollTop = await page.locator('[data-mh-content] [data-scroll-area-viewport]').first().evaluate((el) => {
    el.scrollTop = el.scrollHeight
    return el.scrollTop
  })
  // Playwright's click would scroll this link back into view, changing the saved offset.
  await page.locator('[data-mh-content] a[href="/playlists"]').evaluate((el) => (el as HTMLElement).click())
  await page.waitForURL('**/playlists')
  await page.getByRole('heading', { name: 'Playlists', exact: true }).first().waitFor()
  const outgoingTop = await page.locator('[data-mh-content] [data-scroll-area-viewport]').first().evaluate((el) => {
    el.scrollTop = el.scrollHeight
    return el.scrollTop
  })
  const touch = (type: string, x: number) => page.evaluate(({ type, x }) => {
    const node = document.querySelector('[data-mh-content]')!
    const point = new Touch({ identifier: 7, target: node, clientX: x, clientY: 300 })
    node.dispatchEvent(new TouchEvent(type, {
      bubbles: true, cancelable: true,
      touches: type === 'touchend' || type === 'touchcancel' ? [] : [point], changedTouches: [point],
    }))
    // Sample synchronously: the outgoing copy only lives for the 220ms settle.
    return [...document.querySelectorAll('[data-mh-back-preview]')].at(-1)
      ?.querySelector('[data-scroll-area-viewport]')?.scrollTop
  }, { type, x })
  await touch('touchstart', 5)
  await touch('touchmove', 180)
  const drag = await page.evaluate(() => {
    const node = document.querySelector<HTMLElement>('[data-mh-content]')!
    const copy = document.querySelector<HTMLElement>('[data-mh-back-preview]')
    return {
      x: new DOMMatrix(getComputedStyle(node).transform).m41,
      inert: copy?.inert, hidden: copy?.getAttribute('aria-hidden'),
      scroll: copy?.querySelector('[data-scroll-area-viewport]')?.scrollTop,
    }
  })
  check('swipe Back follows the finger and reveals an inert page at its old scroll position',
    drag.x === 175 && drag.inert === true && drag.hidden === 'true' && drag.scroll === scrollTop, JSON.stringify(drag))
  await touch('touchcancel', 180)
  await page.waitForFunction(() => !document.querySelector('[data-mh-back-preview]'))
  check('cancelled swipe Back keeps the page', new URL(page.url()).pathname === '/playlists')
  await touch('touchstart', 5)
  await touch('touchmove', 220)
  const leavingTop = await touch('touchend', 220)
  check('outgoing swipe copy keeps the bottom scroll offset', leavingTop === outgoingTop, `${leavingTop} vs ${outgoingTop}`)
  await page.waitForURL('**/overview')
  await page.waitForFunction(() => !document.querySelector('[data-mh-back-preview]') &&
    document.querySelector<HTMLElement>('[data-mh-content]')?.style.opacity === '')
  check('committed swipe Back returns to the live page without lingering layers', true)

  check('no CSP violations on app pages', violations.length === 0, [...new Set(violations)].slice(0, 6).join(' | '))
  check('no script errors on app pages', scriptErrors(errors).length === 0, scriptErrors(errors).slice(0, 6).join(' | '))
  const failedLoads = errors.length - scriptErrors(errors).length
  if (failedLoads) console.log(`note: ${failedLoads} failed resource loads (not counted)`)
  await context.close()
}

await finish(browser)
