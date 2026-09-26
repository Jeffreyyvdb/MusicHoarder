import type { DeviceKind } from './wire';

/**
 * Who this player is, for the account's playback session.
 *
 * On the web a device is one tab (each has its own `<audio>`), so its id lives in
 * `sessionStorage`: a reload keeps it, a new tab gets a fresh one. Duplicating a tab copies
 * `sessionStorage` too, so every tab asks the others over a `BroadcastChannel` at startup whether
 * its id is taken, and draws a new one if it is. The install id lives in `localStorage` — one per
 * browser profile — so another tab of this same browser reads as "Another tab" rather than as a
 * second computer. Every storage access is guarded: private modes and blocked site data throw.
 */

export const DEVICE_ID_KEY = 'mh:device-id';
export const INSTALL_ID_KEY = 'mh:install-id';
export const DEVICE_CHANNEL_NAME = 'mh-device';
/** How long a starting tab waits for another tab to say its id is taken. */
export const DEVICE_PROBE_MS = 200;
/** The server's limit on a device name. */
export const DEVICE_NAME_MAX = 64;

const ID_PATTERN = /^[A-Za-z0-9_-]{8,64}$/;

type StorageLike = Pick<Storage, 'getItem' | 'setItem'>;

export function isValidDeviceId(value: unknown): value is string {
  return typeof value === 'string' && ID_PATTERN.test(value);
}

/** A fresh id in the contract's format: a UUID where the platform has one. */
export function randomDeviceId(): string {
  const cryptoApi = (globalThis as { crypto?: Crypto }).crypto;
  if (cryptoApi && typeof cryptoApi.randomUUID === 'function') return cryptoApi.randomUUID();
  const bytes = new Uint8Array(16);
  if (cryptoApi && typeof cryptoApi.getRandomValues === 'function') {
    cryptoApi.getRandomValues(bytes);
  } else {
    for (let i = 0; i < bytes.length; i++) bytes[i] = Math.floor(Math.random() * 256);
  }
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
}

/** The stored id, or a new one written back. A store that throws still yields an id (unsaved). */
export function readOrCreateId(
  storage: StorageLike | null,
  key: string,
  create: () => string = randomDeviceId
): string {
  let existing: string | null = null;
  try {
    existing = storage?.getItem(key) ?? null;
  } catch {
    existing = null;
  }
  if (isValidDeviceId(existing)) return existing;
  const id = create();
  writeId(storage, key, id);
  return id;
}

function writeId(storage: StorageLike | null, key: string, id: string): void {
  try {
    storage?.setItem(key, id);
  } catch {
    // Not saved: this page keeps the id for its lifetime, and the next load draws another.
  }
}

/** The slice of `BroadcastChannel` the duplicate-tab check uses, so a test can fake it. */
export interface ChannelLike {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', listener: (event: MessageEvent) => void): void;
  removeEventListener(type: 'message', listener: (event: MessageEvent) => void): void;
  close(): void;
}

export interface TabIdentity {
  /** The id as it stands; it can change once, while the probe runs. */
  readonly deviceId: string;
  /** Resolves with the settled id once no other tab has objected for {@link DEVICE_PROBE_MS}. */
  readonly ready: Promise<string>;
  /** Stop answering other tabs (the channel closes). */
  dispose(): void;
}

/**
 * This tab's device id, checked against the other open tabs. The tab says `hello` with its id; a
 * tab already holding that id answers `taken`, and the newcomer — only while it is still probing —
 * draws a fresh id. After the probe the tab keeps listening, so a tab duplicated from it later is
 * told in turn. Without a channel (old browsers, tests) the stored id simply stands.
 */
export function claimTabDeviceId(options: {
  storage: StorageLike | null;
  channel: ChannelLike | null;
  create?: () => string;
  probeMs?: number;
  setTimer?: (fn: () => void, ms: number) => unknown;
}): TabIdentity {
  const { storage, channel } = options;
  const create = options.create ?? randomDeviceId;
  const setTimer = options.setTimer ?? ((fn, ms) => setTimeout(fn, ms));
  let deviceId = readOrCreateId(storage, DEVICE_ID_KEY, create);
  let probing = channel !== null;

  const onMessage = (event: MessageEvent) => {
    const message = event.data as { type?: unknown; deviceId?: unknown } | null;
    if (!message || typeof message !== 'object' || message.deviceId !== deviceId) return;
    if (message.type === 'hello') {
      safePost(channel, { type: 'taken', deviceId });
    } else if (message.type === 'taken' && probing) {
      deviceId = create();
      writeId(storage, DEVICE_ID_KEY, deviceId);
    }
  };

  let ready: Promise<string>;
  if (channel) {
    try {
      channel.addEventListener('message', onMessage);
    } catch {
      probing = false;
    }
    safePost(channel, { type: 'hello', deviceId });
    ready = new Promise((resolve) => {
      setTimer(() => {
        probing = false;
        resolve(deviceId);
      }, options.probeMs ?? DEVICE_PROBE_MS);
    });
  } else {
    ready = Promise.resolve(deviceId);
  }

  return {
    get deviceId() {
      return deviceId;
    },
    ready,
    dispose() {
      if (!channel) return;
      try {
        channel.removeEventListener('message', onMessage);
        channel.close();
      } catch {
        // already closed
      }
    }
  };
}

function safePost(channel: ChannelLike | null, message: unknown): void {
  try {
    channel?.postMessage(message);
  } catch {
    // A closed channel: nobody is listening on it any more.
  }
}

export interface DeviceDescription {
  name: string;
  kind: DeviceKind;
}

interface Platform {
  /** "iPhone", "Mac" — what follows "on" in the name; null when the platform is unknown. */
  label: string | null;
  kind: DeviceKind;
}

/**
 * What to call this browser in another device's picker, from its user agent: "Safari on Mac",
 * "Chrome on Windows", "Safari on iPhone", or "MusicHoarder on iPhone" for the installed app (it
 * has no browser to name). `maxTouchPoints` tells an iPad asking for desktop sites — which sends a
 * Mac user agent — from a Mac, which reports none.
 */
export function describeDevice(
  userAgent: string,
  options: { installed?: boolean; maxTouchPoints?: number } = {}
): DeviceDescription {
  const platform = detectPlatform(userAgent, options.maxTouchPoints ?? 0);
  const app = options.installed ? 'MusicHoarder' : detectBrowser(userAgent);
  const name = platform.label ? `${app} on ${platform.label}` : app;
  return { name: name.slice(0, DEVICE_NAME_MAX), kind: platform.kind };
}

function detectPlatform(ua: string, maxTouchPoints: number): Platform {
  if (/\biPhone\b/.test(ua)) return { label: 'iPhone', kind: 'phone' };
  if (/\biPod\b/.test(ua)) return { label: 'iPod', kind: 'phone' };
  if (/\biPad\b/.test(ua)) return { label: 'iPad', kind: 'tablet' };
  if (/\bMacintosh\b/.test(ua) && maxTouchPoints > 1) return { label: 'iPad', kind: 'tablet' };
  if (/\bAndroid\b/.test(ua)) {
    return /\bMobile\b/.test(ua)
      ? { label: 'Android', kind: 'phone' }
      : { label: 'Android tablet', kind: 'tablet' };
  }
  if (/\bCrOS\b/.test(ua)) return { label: 'ChromeOS', kind: 'computer' };
  if (/\bMacintosh\b|\bMac OS X\b/.test(ua)) return { label: 'Mac', kind: 'computer' };
  if (/\bWindows\b/.test(ua)) return { label: 'Windows', kind: 'computer' };
  if (/\bLinux\b/.test(ua)) return { label: 'Linux', kind: 'computer' };
  return { label: null, kind: 'unknown' };
}

// Most specific first: Edge, Opera and Samsung Internet all carry "Chrome/" too, and every iOS
// browser carries "Safari/".
function detectBrowser(ua: string): string {
  if (/\bEdg(?:e|A|iOS)?\//.test(ua)) return 'Edge';
  if (/\bOPR\/|\bOPT\/|\bOpera\b/.test(ua)) return 'Opera';
  if (/\bSamsungBrowser\//.test(ua)) return 'Samsung Internet';
  if (/\bFirefox\/|\bFxiOS\//.test(ua)) return 'Firefox';
  if (/\bCriOS\/|\bChrome\/|\bChromium\//.test(ua)) return 'Chrome';
  if (/\bSafari\/|\bAppleWebKit\//.test(ua)) return 'Safari';
  return 'Browser';
}
