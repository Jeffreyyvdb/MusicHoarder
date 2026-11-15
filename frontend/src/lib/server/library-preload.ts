import { API_PREFIX, LIBRARY_ALBUMS_PATH, LIBRARY_SONGS_PATH } from '#lib/library-requests.ts';
import { hasSessionCookie } from '#lib/server/session.ts';

/**
 * Start the library download together with the (app) document, not after the JavaScript boots.
 *
 * Every (app) page mounts `AppShellV2`, and the shell loads the library (`songsStore.ensureLoaded`)
 * for every account — admin, demo and member alike, since `/songs` and `/api/albums` scope to the
 * caller. Left to the bundle, that request leaves only once the scripts have run and `__data.json`
 * has come back: about 1.3 s into a cold start on a phone, for a 2 MB response. A `Link: preload`
 * header on the document starts it at once, and the store's `fetch()` then takes the response
 * already on its way. Measured with `perf/` (10,000 songs, Chromium): cold starts 18% faster on
 * the phone profile and about 30% on desktop, with the same bytes on the wire.
 *
 * **Chromium only.** A preload is worth something only when the later `fetch()` reuses it, and in
 * Playwright's three engines only Chromium does. WebKit downloaded the library a second time in
 * every variant tried (header or `<link>`, in flight or finished, `fetch` or XHR), and Firefox does
 * for the store's `cache: 'no-store'`. So Safari, every iOS browser and the installed iOS app
 * (all WebKit) and Firefox get no header: for them it would only double the download. A script in
 * app.html that starts the requests itself does work in every engine, and was measured too: a few
 * percent in WebKit at best, nothing on the throttled phone profile, and slower than no preload in
 * unthrottled Chromium — not worth a second mechanism.
 *
 * Matching also needs the same credentials mode: `crossorigin` is CORS-anonymous, i.e. `same-origin`
 * credentials, which is what the store's plain `fetch()` uses, so the session cookie goes with
 * both. The CSP needs nothing: a `fetch` preload is checked against `connect-src`, which allows
 * 'self'. The page is per-session HTML that nothing caches, so the header varying by browser is
 * not a caching concern.
 */

/** What the store asks for on a cold start, in the order it asks. */
export const LIBRARY_PRELOAD_URLS: readonly string[] = [
  `${API_PREFIX}${LIBRARY_SONGS_PATH}`,
  `${API_PREFIX}${LIBRARY_ALBUMS_PATH}`
];

/** The `Link` header value that preloads them. */
export const LIBRARY_PRELOAD_LINK = LIBRARY_PRELOAD_URLS.map(
  (url) => `<${url}>; rel=preload; as=fetch; crossorigin`
).join(', ');

export interface PreloadCandidate {
  method: string;
  /** SvelteKit's `event.route.id`; null when no route matched. */
  routeId: string | null;
  cookieHeader: string | null;
  userAgent: string | null;
  status: number;
  contentType: string | null;
}

/**
 * Whether the browser runs Chromium's engine, the one that hands a `fetch` preload to `fetch()`.
 * Every Chromium browser names its `Chrome/` version (Edge, Opera, Samsung Internet, Android's
 * WebView — so the Android app too). Chrome and Edge on iOS are WebKit and say `CriOS/`/`EdgiOS/`
 * instead, so they are left out, as they should be.
 */
export function isChromiumEngine(userAgent: string | null): boolean {
  return userAgent !== null && /(?:Chrome|Chromium)\/\d/.test(userAgent);
}

/**
 * Whether this response is an (app) page whose browser will use the preload.
 *
 * - Only an (app) route: the marketing, login and share pages never load the library.
 * - Only with a session cookie: without one the auth gate redirects, so there is no page.
 * - Only a 200 HTML document: a redirect (signed out, a stale cookie, a page this account may not
 *   open), the gate's 503 error page and `__data.json` must not start a 2 MB download nobody reads.
 * - Only Chromium ({@link isChromiumEngine}).
 */
export function shouldPreloadLibrary(candidate: PreloadCandidate): boolean {
  return (
    candidate.method === 'GET' &&
    candidate.status === 200 &&
    (candidate.routeId?.startsWith('/(app)') ?? false) &&
    (candidate.contentType?.toLowerCase().startsWith('text/html') ?? false) &&
    hasSessionCookie(candidate.cookieHeader) &&
    isChromiumEngine(candidate.userAgent)
  );
}

/** Adds `link` to the response's `Link` header, after any value already there. */
export function appendLink(headers: Headers, link: string): void {
  const existing = headers.get('link');
  headers.set('Link', existing ? `${existing}, ${link}` : link);
}
