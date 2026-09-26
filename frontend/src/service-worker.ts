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
 * It also shows chat notifications (Web Push): a `push` becomes a notification, and tapping one
 * brings the app forward on the conversation. Neither touches a cache or a fetch.
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

// ── Notifications ──────────────────────────────────────────────────────────────────────────────

/** What the API sends (Chat/ChatPushService.cs, the test in PushEndpoints.cs). */
type PushPayload = {
  type?: string;
  userId?: string;
  conversationId?: string;
  title?: string;
  body?: string;
  url?: string;
  tag?: string;
  /** The account's unread total, for the app icon. */
  badge?: number;
};

type BadgeNavigator = WorkerNavigator & { setAppBadge?: (n?: number) => Promise<void> };

/** Same-origin paths only: a notification can never send the app somewhere else. */
function targetUrl(payload: PushPayload): string {
  const path = typeof payload.url === 'string' && payload.url.startsWith('/') && !payload.url.startsWith('//')
    ? payload.url
    : '/chats';
  const url = new URL(path, sw.location.origin);
  // A browser can hold several accounts; the page switches if this one is not the active one.
  if (payload.userId) url.searchParams.set('account', payload.userId);
  return url.pathname + url.search;
}

sw.addEventListener('push', (event) => {
  let payload: PushPayload = {};
  try {
    payload = (event.data?.json() ?? {}) as PushPayload;
  } catch {
    payload = { body: event.data?.text() };
  }

  const title = payload.title || 'MusicHoarder';
  const options: NotificationOptions & { renotify?: boolean } = {
    body: payload.body || 'New message',
    icon: `${base}/icon-192.png`,
    badge: `${base}/icon-maskable-192.png`,
    // One notification per conversation: a newer message replaces the older one — and still buzzes.
    tag: payload.tag || 'chat',
    renotify: true,
    data: { url: targetUrl(payload) }
  };

  const badge = typeof payload.badge === 'number' ? payload.badge : null;
  const nav = sw.navigator as BadgeNavigator;
  event.waitUntil(
    Promise.all([
      // Every push must show a notification (Safari revokes a subscription that stays silent).
      sw.registration.showNotification(title, options),
      badge != null && nav.setAppBadge ? nav.setAppBadge(badge).catch(() => {}) : Promise.resolve()
    ])
  );
});

sw.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const url = (event.notification.data as { url?: string } | null)?.url ?? '/chats';

  event.waitUntil(
    (async () => {
      const windows = await sw.clients.matchAll({ type: 'window', includeUncontrolled: true });
      const open = windows.find((c) => new URL(c.url).origin === sw.location.origin);
      if (open) {
        // Ask the page to navigate itself (a client-side goto), so music playing in it keeps playing
        // — `WindowClient.navigate` would reload the whole app.
        open.postMessage({ type: 'mh:navigate', url });
        await open.focus().catch(() => undefined);
        return;
      }
      await sw.clients.openWindow(url);
    })()
  );
});
