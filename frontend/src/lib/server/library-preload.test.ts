import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  appendLink,
  isChromiumEngine,
  LIBRARY_PRELOAD_LINK,
  LIBRARY_PRELOAD_URLS,
  shouldPreloadLibrary,
  type PreloadCandidate
} from './library-preload';

vi.mock('#lib/server/api-target.ts', () => ({ getApiBaseUrl: () => 'http://api.test' }));

const UA = {
  chromeDesktop:
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36',
  edge: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36 Edg/142.0.0.0',
  chromeAndroid:
    'Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Mobile Safari/537.36',
  // The Android app shell's web view.
  androidWebView:
    'Mozilla/5.0 (Linux; Android 14; Pixel 8; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/142.0.0.0 Mobile Safari/537.36',
  headless:
    'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/142.0.0.0 Safari/537.36',
  safariMac:
    'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15',
  // The installed iOS app and Safari on iPhone.
  safariIphone:
    'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1',
  // Chrome on iOS is WebKit.
  chromeIos:
    'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/142.0.0.0 Mobile/15E148 Safari/604.1',
  firefox: 'Mozilla/5.0 (X11; Linux x86_64; rv:144.0) Gecko/20100101 Firefox/144.0'
};

const APP_PAGE: PreloadCandidate = {
  method: 'GET',
  routeId: '/(app)/overview',
  cookieHeader: 'theme=dark; mh_session=abc',
  userAgent: UA.chromeDesktop,
  status: 200,
  contentType: 'text/html'
};

describe('isChromiumEngine', () => {
  it('is true for the browsers that reuse a fetch preload', () => {
    for (const ua of [
      UA.chromeDesktop,
      UA.edge,
      UA.chromeAndroid,
      UA.androidWebView,
      UA.headless
    ]) {
      expect(isChromiumEngine(ua)).toBe(true);
    }
  });

  it('is false for WebKit (all of iOS) and Firefox, which would download the library twice', () => {
    for (const ua of [UA.safariMac, UA.safariIphone, UA.chromeIos, UA.firefox, null]) {
      expect(isChromiumEngine(ua)).toBe(false);
    }
  });
});

describe('shouldPreloadLibrary', () => {
  it('preloads on every signed-in (app) page, since the shell loads the library on all of them', () => {
    for (const routeId of [
      '/(app)/overview',
      '/(app)/settings',
      '/(app)/manage',
      '/(app)/inbox/tags'
    ]) {
      expect(shouldPreloadLibrary({ ...APP_PAGE, routeId })).toBe(true);
    }
    expect(shouldPreloadLibrary({ ...APP_PAGE, contentType: 'text/html; charset=utf-8' })).toBe(
      true
    );
  });

  it('skips pages outside (app), which never load the library', () => {
    for (const routeId of ['/(marketing)', '/login', '/share/[token]', '/auth/callback', null]) {
      expect(shouldPreloadLibrary({ ...APP_PAGE, routeId })).toBe(false);
    }
  });

  it('skips a request without a session cookie, which the auth gate redirects', () => {
    expect(shouldPreloadLibrary({ ...APP_PAGE, cookieHeader: null })).toBe(false);
    expect(shouldPreloadLibrary({ ...APP_PAGE, cookieHeader: 'theme=dark' })).toBe(false);
    expect(shouldPreloadLibrary({ ...APP_PAGE, cookieHeader: 'x_mh_session=abc' })).toBe(false);
    // Parked accounts alone are not a signed-in session.
    expect(shouldPreloadLibrary({ ...APP_PAGE, cookieHeader: 'mh_session_alts=abc' })).toBe(false);
  });

  it('skips anything but a 200 HTML document', () => {
    expect(shouldPreloadLibrary({ ...APP_PAGE, status: 303, contentType: null })).toBe(false);
    expect(shouldPreloadLibrary({ ...APP_PAGE, status: 503 })).toBe(false);
    // __data.json shares the route id.
    expect(shouldPreloadLibrary({ ...APP_PAGE, contentType: 'application/json' })).toBe(false);
    expect(shouldPreloadLibrary({ ...APP_PAGE, method: 'HEAD' })).toBe(false);
    expect(shouldPreloadLibrary({ ...APP_PAGE, method: 'POST' })).toBe(false);
  });

  it('skips the browsers that would not use it', () => {
    expect(shouldPreloadLibrary({ ...APP_PAGE, userAgent: UA.safariIphone })).toBe(false);
    expect(shouldPreloadLibrary({ ...APP_PAGE, userAgent: UA.firefox })).toBe(false);
    expect(shouldPreloadLibrary({ ...APP_PAGE, userAgent: null })).toBe(false);
    expect(shouldPreloadLibrary({ ...APP_PAGE, userAgent: UA.androidWebView })).toBe(true);
  });
});

describe('the Link header', () => {
  it('preloads both lists as same-origin fetches', () => {
    expect(LIBRARY_PRELOAD_LINK).toBe(
      '</api/mh/songs?includeDeleted=false>; rel=preload; as=fetch; crossorigin, ' +
        '</api/mh/api/albums>; rel=preload; as=fetch; crossorigin'
    );
  });

  it('is appended after an existing Link value rather than replacing it', () => {
    const headers = new Headers({ Link: '</about.md>; rel="alternate"; type="text/markdown"' });
    appendLink(headers, LIBRARY_PRELOAD_LINK);
    expect(headers.get('link')).toBe(
      `</about.md>; rel="alternate"; type="text/markdown", ${LIBRARY_PRELOAD_LINK}`
    );

    const empty = new Headers();
    appendLink(empty, LIBRARY_PRELOAD_LINK);
    expect(empty.get('link')).toBe(LIBRARY_PRELOAD_LINK);
  });
});

/**
 * The cache contract. A browser hands a preload only to a request for the very same URL, made with
 * the same credentials mode; anything else downloads the library a second time. So this loads the
 * REAL songs store and api-client and checks what a cold start asks for — change either side alone
 * and it fails.
 */
describe('the songs store on a cold start', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.resetModules();
  });

  it('asks for exactly the URLs the document preloads, the way the preload asks', async () => {
    const requests: { url: string; init: RequestInit | undefined }[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        requests.push({ url, init });
        const body = url.includes('/api/albums') ? { albums: [] } : { songs: [], grantors: [] };
        return new Response(JSON.stringify(body), {
          status: 200,
          headers: { 'content-type': 'application/json' }
        });
      })
    );

    const { songsStore } = await import('#lib/stores/songs.svelte.ts');
    songsStore.ensureLoaded();
    await vi.waitFor(() => expect(songsStore.hasLoaded).toBe(true));

    expect(requests.map((r) => r.url)).toEqual(LIBRARY_PRELOAD_URLS);
    for (const { init } of requests) {
      expect(init?.method ?? 'GET').toBe('GET');
      // `crossorigin` (anonymous) is same-origin credentials; another mode would not match.
      expect(init?.credentials ?? 'same-origin').toBe('same-origin');
      expect(new Headers(init?.headers).has('if-none-match')).toBe(false);
    }
  });
});
