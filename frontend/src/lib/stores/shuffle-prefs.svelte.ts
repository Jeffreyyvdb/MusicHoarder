/**
 * The account's remembered shuffle settings (see `#lib/shuffle-prefs`), in localStorage per
 * account so they survive closing the app and never pass to another account signed in here.
 * Before `init` (the share page, a test) they live in memory only.
 */
import {
  isShuffleOn,
  parseShufflePrefs,
  shufflePrefsKey,
  withShuffle,
  type ShufflePrefs,
  type ShuffleScope
} from '#lib/shuffle-prefs.ts';

let prefs = $state.raw<ShufflePrefs>([]);
let storageKey: string | null = null;

function storage(): Storage | null {
  try {
    return typeof window === 'undefined' ? null : window.localStorage;
  } catch {
    return null;
  }
}

/** The stored settings, or what this tab holds when storage cannot be read. */
function read(key: string): ShufflePrefs {
  try {
    const store = storage();
    return store ? parseShufflePrefs(store.getItem(key)) : prefs;
  } catch {
    return prefs;
  }
}

export const shufflePrefs = {
  /** Load the account's settings. A no-op when they are already loaded. */
  init(userId: string) {
    const key = shufflePrefsKey(userId);
    if (storageKey === key) return;
    storageKey = key;
    prefs = read(key);
    // A toggle in another tab shows here at once (the event only fires in the other tabs).
    if (typeof window !== 'undefined' && typeof window.addEventListener === 'function') {
      window.addEventListener('storage', (e) => {
        if (e.key === storageKey) prefs = parseShufflePrefs(e.newValue);
      });
    }
  },
  isOn(scope: ShuffleScope): boolean {
    return isShuffleOn(prefs, scope);
  },
  set(scope: ShuffleScope, on: boolean) {
    // Another tab of this account may have changed others since this one read them.
    if (storageKey) prefs = read(storageKey);
    const next = withShuffle(prefs, scope, on);
    if (next === prefs) return;
    prefs = next;
    if (!storageKey) return;
    try {
      storage()?.setItem(storageKey, JSON.stringify(next));
    } catch {
      /* quota or private mode: the setting still holds until the page closes */
    }
  }
};
