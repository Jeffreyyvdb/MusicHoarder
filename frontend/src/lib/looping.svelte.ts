import { untrack } from 'svelte';
import { PUBLIC_LOOPING_URL } from '$app/env/public';

/**
 * Looping, the maintainer's in-app problem reporter (github.com/Jeffreyyvdb/looping). A report
 * carries the page, a screenshot, console errors and the network calls with their `x-request-id`s,
 * which is what lets the backend logs behind it be found.
 *
 * It is off unless an instance names an inbox (`PUBLIC_LOOPING_URL`, runtime env), and even then
 * the widget is fetched only on a device holding a reporter key: app.html's first script checks
 * for one before anything loads. So a self-hosted instance, and every ordinary visitor, never
 * fetch a byte of it.
 *
 * A key is stored under `looping:key` in this origin's localStorage, by either of:
 *  - a visit to any page with `?looping=<key>` (the widget's own way; `?looping=off` forgets it);
 *  - a search for `looping:<key>` (`looping:off`) — the way in for the iOS and Android app shell
 *    and the installed web app, which have no address bar, and whose storage is not Safari's.
 *
 * On a phone the widget's floating button would sit on the tab bar's search circle, so app.html
 * hides it there and the account menu's "Report a problem" opens the reporter instead.
 */

const KEY_STORAGE = 'looping:key';
/** What a reporter key looks like; anything else is a typo, not worth a reload. */
const KEY_SHAPE = /^[\w.~-]{8,256}$/;

export type LoopingCommand = { kind: 'on'; key: string } | { kind: 'off' };

/** The widget's API (`window.looping`), as much of it as this app uses. */
type LoopingApi = {
  readonly enabled: boolean;
  open(): void;
  setContext(key: string, value: unknown): void;
  setUser(user: Record<string, unknown> | null): void;
};

/** True when this instance names a Looping inbox. */
export function loopingConfigured(): boolean {
  return Boolean(PUBLIC_LOOPING_URL?.trim());
}

/**
 * A whole search query that turns the reporter on or off: `looping:<key>`, `looping <key>`, or
 * `looping:off`. Null for anything else, and always null on an instance without an inbox.
 */
export function parseLoopingCommand(query: string): LoopingCommand | null {
  if (!loopingConfigured()) return null;
  const match = /^\s*looping[:\s]\s*(\S+)\s*$/i.exec(query);
  if (!match) return null;
  const value = match[1];
  if (value.toLowerCase() === 'off') return { kind: 'off' };
  return KEY_SHAPE.test(value) ? { kind: 'on', key: value } : null;
}

/** True when this device holds a reporter key (whether or not the widget has loaded yet). */
export function hasLoopingKey(): boolean {
  try {
    return localStorage.getItem(KEY_STORAGE) !== null;
  } catch {
    return false;
  }
}

/**
 * Stores or forgets the key and reloads, since the widget is loaded (or not) by app.html's first
 * script. False when storage is blocked and nothing changed.
 */
export function applyLoopingCommand(command: LoopingCommand): boolean {
  try {
    if (command.kind === 'on') localStorage.setItem(KEY_STORAGE, command.key);
    else localStorage.removeItem(KEY_STORAGE);
  } catch {
    return false;
  }
  location.reload();
  return true;
}

let api = $state<LoopingApi | null>(null);
const contexts = new Map<string, () => unknown>();

function adopt(candidate: LoopingApi | undefined): void {
  if (!candidate?.enabled || api) return;
  api = candidate;
  for (const [key, read] of contexts) candidate.setContext(key, read);
}

/**
 * Picks up the widget once app.html's loader has run it. Call once from the root layout; returns
 * the cleanup. It listens even when no key is stored yet: on a first `?looping=<key>` visit the
 * widget stores the key itself, and its script may run after this.
 */
export function installLooping(): () => void {
  if (typeof window === 'undefined' || !loopingConfigured()) return () => {};
  const win = window as Window & { looping?: LoopingApi };
  adopt(win.looping);
  const onReady = () => adopt(win.looping);
  window.addEventListener('looping:ready', onReady);
  return () => window.removeEventListener('looping:ready', onReady);
}

/**
 * State attached to every report, read when one is sent (so pass a function that reads the live
 * value). Safe to call before the widget has loaded, or on a device without it.
 */
export function setLoopingContext(key: string, read: () => unknown): () => void {
  // Untracked: an effect registering context must not re-run when the widget turns up (adopt
  // hands it everything registered so far).
  const current = () => untrack(() => api);
  contexts.set(key, read);
  current()?.setContext(key, read);
  return () => {
    if (contexts.get(key) !== read) return;
    contexts.delete(key);
    current()?.setContext(key, undefined);
  };
}

export const looping = {
  /** The reporter is loaded on this device and its key was accepted. */
  get active(): boolean {
    return api !== null;
  },
  /**
   * Opens the reporter. It screenshots the page as it opens, so a caller that is covering the page
   * (a sheet, a menu) closes first and passes how long its exit animation takes.
   */
  open(afterMs = 0): void {
    const target = api;
    if (!target) return;
    if (afterMs > 0) setTimeout(() => target.open(), afterMs);
    else target.open();
  }
};
