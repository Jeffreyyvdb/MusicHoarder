/**
 * Shuffle as a remembered setting, the way Spotify keeps it: each album and each playlist has its
 * own (off until someone turns it on), and everything else (Tracks, an artist) shares one. A
 * list's queue starts in the shuffle state its scope holds, and whatever state that queue is left
 * in is what the scope holds next.
 *
 * Pure (storage is passed in), so the key and the stored shape are testable without a browser.
 */

export type ShuffleScope = 'library' | `album:${string}` | `playlist:${number}`;

export const shuffleScopes = {
  library: 'library' as const,
  album: (albumKey: string): ShuffleScope => `album:${albumKey}`,
  playlist: (playlistId: number): ShuffleScope => `playlist:${playlistId}`
};

export function isShuffleScope(value: unknown): value is ShuffleScope {
  if (typeof value !== 'string') return false;
  if (value === 'library') return true;
  if (value.startsWith('album:')) return value.length > 'album:'.length;
  return /^playlist:\d+$/.test(value);
}

/** Every scope defaults to off, so only the ones turned on are stored, the most recent last. */
export type ShufflePrefs = readonly ShuffleScope[];

/** Enough for every album someone shuffles; the oldest are forgotten first. */
export const MAX_REMEMBERED = 500;

export const SHUFFLE_PREFS_KEY_PREFIX = 'mh:shuffle:';

export function shufflePrefsKey(userId: string): string {
  return SHUFFLE_PREFS_KEY_PREFIX + userId;
}

export function parseShufflePrefs(raw: string | null): ShufflePrefs {
  if (!raw) return [];
  try {
    const value: unknown = JSON.parse(raw);
    if (!Array.isArray(value)) return [];
    return [...new Set(value.filter(isShuffleScope))].slice(-MAX_REMEMBERED);
  } catch {
    return [];
  }
}

export function isShuffleOn(prefs: ShufflePrefs, scope: ShuffleScope): boolean {
  return prefs.includes(scope);
}

/** `prefs` with `scope` set to `on`; the same array when nothing changes. */
export function withShuffle(prefs: ShufflePrefs, scope: ShuffleScope, on: boolean): ShufflePrefs {
  if (prefs.includes(scope) === on && (!on || prefs.at(-1) === scope)) return prefs;
  const rest = prefs.filter((s) => s !== scope);
  return on ? [...rest, scope].slice(-MAX_REMEMBERED) : rest;
}
