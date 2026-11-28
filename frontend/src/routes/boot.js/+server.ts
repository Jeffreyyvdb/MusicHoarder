import { PUBLIC_LOOPING_APP, PUBLIC_LOOPING_URL } from '$app/env/public';
import { version } from '$app/env';
import { SKIN_STYLESHEET_HREF } from '#lib/hardware/skin-stylesheet.ts';
import type { RequestHandler } from './$types';

/**
 * The first-paint script every page's <head> loads (`app.html` references it as `/boot.js`).
 *
 * It was two inline scripts in `app.html` until the /docs pages started prerendering: a template
 * that contains `%sveltekit.nonce%` cannot prerender (SvelteKit's `prerender_template_nonce`), and
 * without the nonce the report-only CSP (nonce mode on dynamically rendered pages, even in `auto`
 * mode) fires a `script-src-elem inline` violation on every page load — the deploy pipeline's
 * preview checks fail on exactly that. An external, same-origin script needs no nonce:
 * `script-src 'self'` covers it, and as a parser-blocking head script it still runs before the
 * first paint, which is the whole point of everything below.
 *
 * The values the template used to substitute are filled in here, on the server:
 *  - the Looping inbox origin and app name (runtime env, `$app/env/public`),
 *  - the build's version (`%sveltekit.version%`),
 *  - the hashed hardware-skin stylesheet URL (`%mh.skinStylesheet%`, filled by hooks.server.ts in
 *    the template world — {@link SKIN_STYLESHEET_HREF}).
 *
 * Keep this file's output byte-for-byte equivalent to what the two scripts did. The mobile shell
 * (`mobile/`) appends `MusicHoarderApp` to its user agent; the installed-app markers below are read
 * by app.css and the login page.
 */
const LOOPING_URL = JSON.stringify(PUBLIC_LOOPING_URL ?? '');
const LOOPING_APP = JSON.stringify(PUBLIC_LOOPING_APP || 'musichoarder');
const BUILD_VERSION = JSON.stringify(version);
const SKIN_HREF = JSON.stringify(SKIN_STYLESHEET_HREF);

const script = `// First-paint boot script — served by src/routes/boot.js, referenced from app.html. See the
// module comment there; edit both together.
(function () {
  // Looping, the problem reporter (#lib/looping.svelte.ts). First so it sees the page's earliest
  // errors, and inert unless the instance names an inbox (PUBLIC_LOOPING_URL, runtime env) AND this
  // device holds a reporter key (\`?looping=<key>\` once, or a search for \`looping:<key>\`). On a
  // phone or a touch screen the floating button would cover the tab bar's search circle, so it is
  // hidden there; the account menu opens the reporter instead.
  try {
    var inbox = ${LOOPING_URL}.replace(/\\/+$/, '');
    if (!/^https?:\\/\\/[^/]+$/.test(inbox)) return;
    if (!/[?&]looping=/.test(location.search) && !localStorage.getItem('looping:key')) return;
    var s = document.createElement('script');
    s.src = inbox + '/widget.js';
    s.dataset.app = ${LOOPING_APP};
    s.dataset.release = ${BUILD_VERSION};
    if (matchMedia('(max-width: 767.98px), (pointer: coarse)').matches) {
      s.dataset.button = 'false';
    }
    document.head.appendChild(s);
  } catch (_) {}
})();

(function () {
  try {
    var mode = localStorage.getItem('mode-watcher-mode');
    var dark =
      mode === 'dark' ||
      (mode !== 'light' && matchMedia('(prefers-color-scheme: dark)').matches);
    if (dark) {
      // The dark tokens hang off .dark (app.css), which mode-watcher sets. On an SSR page its head
      // script does that before the first frame; the (app) pages are ssr = false, so nothing does
      // until the bundle runs, and their first frame was #ffffff in dark mode — a white flash on
      // every cold load and hard reload (the app shell's launch, sign-in).
      document.documentElement.classList.add('dark');
      document.querySelector('meta[name="theme-color"]').setAttribute('content', '#000000');
    }
    // Native controls (inputs, scrollbars, <select> popups) follow color-scheme; claim it at first
    // paint too, so they don't render light inside a dark shell until mode-watcher runs.
    document.documentElement.style.colorScheme = dark ? 'dark' : 'light';
    // The dark class mode-watcher's own head script would set. That script is turned off
    // (disableHeadScriptInjection in the root layout) because it duplicates this one.
    document.documentElement.classList.toggle('dark', dark);
    // The hardware skin (an easter egg, #lib/hardware/hardware-mode.svelte.ts) before the first
    // frame, so a reload never shows the plain skin first. Its paper and graphite are mirrored
    // there as HARDWARE_THEME_COLOR. Its stylesheet is requested only here and only then
    // (#lib/hardware/skin-stylesheet); document.write makes it a parser-inserted, render-blocking
    // stylesheet, which an appended <link> is not. The server wrote the hashed href in.
    if (localStorage.getItem('mh:hardware') === 'on') {
      document.write(
        '<link rel="stylesheet" href="' + ${SKIN_HREF} + '" data-mh-skin-css="" />'
      );
      document.documentElement.setAttribute('data-mh-skin', 'hardware');
      document
        .querySelector('meta[name="theme-color"]')
        .setAttribute('content', dark ? '#22221f' : '#f3f2ee');
    }
  } catch (_) {}
  // Marks the installed iOS app (the same test as isHomeScreenWebApp + isIosDevice) before the
  // first paint, so the login page's action blocks can wait for the client only there.
  try {
    var nav = navigator;
    if (
      nav.standalone === true &&
      (/\\b(iPhone|iPad|iPod)\\b/.test(nav.userAgent) ||
        (/\\bMacintosh\\b/.test(nav.userAgent) && nav.maxTouchPoints > 1))
    ) {
      document.documentElement.setAttribute('data-mh-ios-app', '');
    }
  } catch (_) {}
  // Marks any installed app (the same test as isInstalledApp: the app shell or a Home Screen web
  // app) for app.css, which then keeps a held link or image from opening iOS's link preview.
  try {
    if (
      /\\bMusicHoarderApp\\b/.test(navigator.userAgent) ||
      navigator.standalone === true ||
      matchMedia('(display-mode: standalone), (display-mode: fullscreen)').matches
    ) {
      document.documentElement.setAttribute('data-mh-installed', '');
    }
  } catch (_) {}
  // The app shell (mobile/, which appends MusicHoarderApp to its user agent) does not zoom. Its
  // web view (Capacitor's default, zoomEnabled: false) turns off the pinch gesture the moment a
  // zoom begins, so a double-tap that zoomed the page in could never be pinched back out. In a web
  // view the viewport's scale limits are honoured (Safari ignores them), and a page that cannot be
  // scaled gets no double-tap zoom at all, nor a zoom on focusing a field. Browsers and the Home
  // Screen app keep the scalable viewport: touch-action in app.css stops their double-tap zoom,
  // and pinch-zoom stays theirs.
  try {
    if (/\\bMusicHoarderApp\\b/.test(navigator.userAgent)) {
      document
        .querySelector('meta[name="viewport"]')
        .setAttribute(
          'content',
          'width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no, viewport-fit=cover'
        );
    }
  } catch (_) {}
  // iOS Dynamic Type: scale the iOS text styles by the iPhone's Text Size setting before the first
  // frame, so the app does not open at the default size and then jump. The same measurement as
  // $lib/dynamic-type.ts (which keeps it current); iOS only, as there.
  try {
    var ua = navigator.userAgent;
    var probe = document.createElement('span');
    probe.style.cssText = 'position:absolute;visibility:hidden;pointer-events:none;';
    probe.style.font = '-apple-system-body';
    if (
      (/\\b(iPhone|iPad|iPod)\\b/.test(ua) ||
        (/\\bMacintosh\\b/.test(ua) && navigator.maxTouchPoints > 1)) &&
      probe.style.font
    ) {
      document.documentElement.appendChild(probe);
      var px = parseFloat(getComputedStyle(probe).fontSize);
      probe.remove();
      if (px > 0) {
        var dt = Math.min(1.5, Math.max(0.82, px / 17));
        document.documentElement.style.setProperty('--mh-dt', String(Math.round(dt * 1000) / 1000));
      }
    }
  } catch (_) {}
})();
`;

export const GET: RequestHandler = async () => {
  return new Response(script, {
    headers: {
      // Content changes with every deploy (the version and the skin stylesheet's hash); always
      // revalidate rather than risk one deploy serving another's hashes.
      'Cache-Control': 'no-cache',
      'Content-Type': 'text/javascript; charset=utf-8'
    }
  });
};
