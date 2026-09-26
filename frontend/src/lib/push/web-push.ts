import { fetchPushConfig, removePushSubscription, savePushSubscription } from '$lib/api-client';
import { isInstalledApp } from '$lib/hooks/viewport-insets.svelte';
import { isIosDevice } from '$lib/ios-safari';

/**
 * Browser notifications for chat (Web Push). The service worker shows them (`src/service-worker.ts`);
 * this module turns them on and off and keeps the server's copy of the subscription current.
 *
 * Where it works: Chrome, Edge, Firefox and Samsung Internet on Android and desktop, Safari on
 * macOS, and on an iPhone or iPad only inside the app added to the Home Screen (iOS 16.4+) — Safari
 * tabs there cannot receive push at all. Asking for permission must happen inside a tap (iOS
 * refuses otherwise), so {@link enablePush} asks first and does its network work after.
 *
 * One browser holds one push subscription, however many accounts are signed in to it; the server
 * keeps a row per (account, subscription). Whether THIS account wants notifications here is
 * remembered per account in localStorage, so switching accounts does not switch them on for the
 * other one by accident.
 */

export type PushState =
  /** This browser cannot receive push at all. */
  | 'unsupported'
  /** An iPhone/iPad in a Safari tab: it can, once the app is added to the Home Screen. */
  | 'install'
  /** The server has notifications switched off. */
  | 'unavailable'
  /** The person blocked notifications for this site; only the browser's settings can undo it. */
  | 'denied'
  | 'off'
  | 'on';

const PREF_PREFIX = 'mh:push:';

function pref(userId: string): 'on' | 'off' | null {
  try {
    const value = localStorage.getItem(PREF_PREFIX + userId);
    return value === 'on' || value === 'off' ? value : null;
  } catch {
    return null;
  }
}

function setPref(userId: string, value: 'on' | 'off'): void {
  try {
    localStorage.setItem(PREF_PREFIX + userId, value);
  } catch {
    // Not remembered; the server row still decides what is sent.
  }
}

export function pushSupported(): boolean {
  return (
    typeof window !== 'undefined' &&
    'serviceWorker' in navigator &&
    'PushManager' in window &&
    'Notification' in window
  );
}

async function registration(): Promise<ServiceWorkerRegistration | null> {
  if (!('serviceWorker' in navigator)) return null;
  try {
    return await navigator.serviceWorker.ready;
  } catch {
    return null;
  }
}

/** Where this browser and this account stand. Cheap; no network. */
export async function pushState(userId: string | null | undefined): Promise<PushState> {
  if (!pushSupported()) return isIosDevice() && !isInstalledApp() ? 'install' : 'unsupported';
  if (Notification.permission === 'denied') return 'denied';
  if (!userId || Notification.permission !== 'granted' || pref(userId) !== 'on') return 'off';
  const reg = await registration();
  const subscription = await reg?.pushManager.getSubscription();
  return subscription ? 'on' : 'off';
}

/** The key as the browser wants it: raw bytes of the uncompressed point. */
export function urlBase64ToBytes(base64url: string): Uint8Array<ArrayBuffer> {
  const padded = base64url + '='.repeat((4 - (base64url.length % 4)) % 4);
  const raw = atob(padded.replace(/-/g, '+').replace(/_/g, '/'));
  const bytes = new Uint8Array(new ArrayBuffer(raw.length));
  for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
  return bytes;
}

function sameKey(a: ArrayBuffer | null | undefined, b: Uint8Array): boolean {
  if (!a) return false;
  const left = new Uint8Array(a);
  return left.length === b.length && left.every((v, i) => v === b[i]);
}

/**
 * The browser's subscription made against the server's current key: reused when it matches, made
 * again when the server's key changed (its data-protection keys were reset), created when missing.
 */
async function ensureSubscription(publicKey: string): Promise<PushSubscription | null> {
  const reg = await registration();
  if (!reg) return null;
  const key = urlBase64ToBytes(publicKey);
  const existing = await reg.pushManager.getSubscription();
  if (existing && sameKey(existing.options.applicationServerKey, key)) return existing;
  if (existing) await existing.unsubscribe().catch(() => false);
  return reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key });
}

/** Turn notifications on for this account in this browser. Call it from a tap. */
export async function enablePush(userId: string): Promise<PushState> {
  if (!pushSupported()) return pushState(userId);
  // First, while the tap still counts: iOS only shows the prompt inside a user gesture.
  const permission =
    Notification.permission === 'default' ? await Notification.requestPermission() : Notification.permission;
  if (permission === 'denied') return 'denied';
  if (permission !== 'granted') return 'off';

  const config = await fetchPushConfig();
  if (!config.enabled || !config.publicKey) return 'unavailable';
  const subscription = await ensureSubscription(config.publicKey);
  if (!subscription) return 'unsupported';
  await savePushSubscription(subscription.toJSON());
  setPref(userId, 'on');
  return 'on';
}

/**
 * Stop notifying this account here. The browser's subscription itself stays when another account
 * in this browser may still use it; the server just forgets it for this one.
 */
export async function disablePush(userId: string): Promise<void> {
  setPref(userId, 'off');
  const reg = await registration();
  const subscription = await reg?.pushManager.getSubscription();
  if (subscription) await removePushSubscription(subscription.endpoint);
}

/**
 * On app start: when this account has notifications on here, make sure the server still holds this
 * browser's current subscription (browsers rotate them, the server drops dead ones, and its key may
 * have changed). Quiet — never prompts.
 */
export async function syncPush(userId: string | null | undefined): Promise<void> {
  if (!userId || !pushSupported() || Notification.permission !== 'granted' || pref(userId) !== 'on') return;
  try {
    const config = await fetchPushConfig();
    if (!config.enabled || !config.publicKey) return;
    const subscription = await ensureSubscription(config.publicKey);
    if (subscription) await savePushSubscription(subscription.toJSON());
  } catch {
    // Next start tries again.
  }
}

/** Signing out of this browser: this account's notifications must stop arriving here. */
export async function forgetPushOnSignOut(): Promise<void> {
  if (!pushSupported()) return;
  const reg = await registration();
  const subscription = await reg?.pushManager.getSubscription();
  if (subscription) await removePushSubscription(subscription.endpoint);
}

/** Put the unread count on the installed app's icon (where the browser supports it). */
export function setAppBadge(count: number): void {
  const nav = navigator as Navigator & {
    setAppBadge?: (n?: number) => Promise<void>;
    clearAppBadge?: () => Promise<void>;
  };
  try {
    if (count > 0) void nav.setAppBadge?.(count).catch(() => {});
    else void nav.clearAppBadge?.().catch(() => {});
  } catch {
    // Not supported here.
  }
}
