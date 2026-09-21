/// <reference types="@sveltejs/kit" />
/// <reference no-default-lib="true"/>
/// <reference lib="esnext" />
/// <reference lib="webworker" />

import { base, version } from '$service-worker';

/**
 * Deliberately minimal service worker.
 *
 * What it does: precaches one static page and serves it when a *top-level navigation* fails at
 * the network level, so an installed (home-screen) app launched without connectivity shows a
 * MusicHoarder "you're offline" screen instead of the browser's error page. SvelteKit registers
 * it automatically because this file exists (`kit.serviceWorker.register` defaults to true).
 *
 * What it deliberately does NOT do: cache HTML, API responses, audio, or the build's JS/CSS.
 * The app is a thin client over its own API (nothing works offline anyway), `/_app/immutable/*`
 * already ships with `Cache-Control: immutable` so the HTTP cache covers it, and the root layout's
 * version poll + stale-chunk recovery assume HTML always comes from the server. A caching layer
 * here would turn every deploy into a stale-shell bug — keep it this small.
 */
const sw = self as unknown as ServiceWorkerGlobalScope;

const CACHE = `mh-offline-${version}`;
const OFFLINE_URL = `${base}/offline.html`;

sw.addEventListener('install', (event) => {
  event.waitUntil(
    caches
      .open(CACHE)
      // `reload` bypasses the HTTP cache so a redeploy always picks up the current page.
      .then((cache) => cache.add(new Request(OFFLINE_URL, { cache: 'reload' })))
      // Nothing else is cached, so taking over open tabs immediately is safe.
      .then(() => sw.skipWaiting())
  );
});

sw.addEventListener('activate', (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) =>
        Promise.all(keys.filter((key) => key !== CACHE).map((key) => caches.delete(key)))
      )
      .then(() => sw.clients.claim())
  );
});

sw.addEventListener('fetch', (event) => {
  // Only top-level navigations. Everything else — API calls, SSE streams, audio range requests,
  // build chunks, the `_app/version.json` poll — never passes through this worker.
  if (event.request.mode !== 'navigate' || event.request.method !== 'GET') return;

  event.respondWith(
    fetch(event.request).catch(async (err: unknown) => {
      // Only a *network* failure lands here; a server response (5xx included) went through above,
      // so the app's own error page keeps handling those.
      const offline = await caches.match(OFFLINE_URL);
      if (offline) return offline;
      throw err;
    })
  );
});
