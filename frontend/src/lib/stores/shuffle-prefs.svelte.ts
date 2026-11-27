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

export const shufflePrefs = {
  /** Load the account's settings. A no-op when they are already loaded. */
  init(userId: string) {
    const key = shufflePrefsKey(userId);
    if (storageKey === key) return;
    storageKey = key;
    try {
      prefs = parseShufflePrefs(storage()?.getItem(key) ?? null);
    } catch {
      prefs = [];
    }
  },
  isOn(scope: ShuffleScope): boolean {
    return isShuffleOn(prefs, scope);
  },
  set(scope: ShuffleScope, on: boolean) {
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
