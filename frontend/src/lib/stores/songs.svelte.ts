/**
 * Shared songs store — owns the full `ApiSong[]` dataset plus the live
 * SSE-driven refresh, lifted out of LibraryV2 so any route can resolve a song
 * (e.g. the global song-detail sidebar opened from the MiniPlayer off-Library).
 *
 * `startLive`/`stopLive` are ref-counted: LibraryV2 and the detail host can both
 * keep the progress stream alive, and it only tears down once the last consumer
 * releases it — so navigating away from Library doesn't kill the stream while
 * the detail panel is still open elsewhere.
 */

import {
  currentGrantors,
  fetchSongs,
  likeSong,
  openProgressStream,
  unlikeSong,
  type ApiSong,
  type Grantor,
  type ProgressSnapshot
} from '$lib/api-client';

let songs = $state<ApiSong[]>([]);
/**
 * Who shared the rows in {@link songs}. Lives HERE, as a rune, rather than in `api-client`:
 * that module is a plain `.ts` file, so its module-level copy cannot be reactive, and a
 * `$derived` reading it would compute once before the first fetch resolves and then stay clean
 * forever — the "shared by X" header would simply never appear on a cold load.
 */
let grantors = $state<Grantor[]>([]);
let isLoading = $state(false);
let error = $state<string | null>(null);
let hasLoaded = false;

async function loadSongs(opts?: { silent?: boolean }): Promise<void> {
  try {
    if (!opts?.silent) isLoading = true;
    const loaded = await fetchSongs();
    songs = loaded;
    // Read AFTER the await: fetchSongs populates the api-client's copy as it resolves.
    grantors = currentGrantors();
    hasLoaded = true;
    error = null;
  } catch (err) {
    error = err instanceof Error ? err.message : 'Failed to load library';
  } finally {
    if (!opts?.silent) isLoading = false;
  }
}

/** Fetch once if we have no data yet and aren't already loading. */
function ensureLoaded(): void {
  if (hasLoaded || isLoading) return;
  void loadSongs();
}

// ── live refresh (ref-counted) ───────────────────────────────────────────────
let liveRefCount = 0;
let liveCleanup: (() => void) | null = null;
let refreshTimer: ReturnType<typeof setTimeout> | null = null;
let lastBuilt = -1;
let sawActive = false;

function scheduleSongRefresh(): void {
  if (refreshTimer) return;
  refreshTimer = setTimeout(() => {
    refreshTimer = null;
    void loadSongs({ silent: true });
  }, 3000);
}

function openStream(): void {
  if (liveCleanup) return;
  lastBuilt = -1;
  sawActive = false;
  liveCleanup = openProgressStream(
    (snap: ProgressSnapshot) => {
      if (snap.built !== lastBuilt) {
        lastBuilt = snap.built;
        sawActive = true;
        scheduleSongRefresh();
      }
      if (snap.isComplete && sawActive) {
        sawActive = false;
        scheduleSongRefresh();
      }
    },
    () => {
      liveCleanup = null;
      if (sawActive) {
        sawActive = false;
        void loadSongs({ silent: true });
      }
    }
  );
}

function startLive(): void {
  liveRefCount += 1;
  if (liveRefCount === 1) openStream();
}

function stopLive(): void {
  liveRefCount = Math.max(0, liveRefCount - 1);
  if (liveRefCount > 0) return;
  if (refreshTimer) {
    clearTimeout(refreshTimer);
    refreshTimer = null;
  }
  if (liveCleanup) {
    liveCleanup();
    liveCleanup = null;
  }
}

// ── likes + plays (optimistic local mutation) ────────────────────────────────

function findSong(id: number): ApiSong | undefined {
  return songs.find((s) => s.id === id);
}

/**
 * Toggle a song's liked state. Mutates the store row optimistically (rows are
 * deep `$state` proxies, so every view reacts) and reverts on API failure —
 * callers surface the thrown error (e.g. demo read-only) however they like.
 */
async function toggleLike(id: number): Promise<void> {
  const song = findSong(id);
  if (!song) return;
  const previous = song.likedAtUtc ?? null;
  const liking = !previous;
  song.likedAtUtc = liking ? new Date().toISOString() : null;
  try {
    const result = liking ? await likeSong(id) : await unlikeSong(id);
    song.likedAtUtc = result.likedAtUtc;
  } catch (err) {
    song.likedAtUtc = previous;
    throw err;
  }
}

/** Reflect a play reported by the player without waiting for the next full refetch. */
function notePlayed(id: number): void {
  const song = findSong(id);
  if (!song) return;
  song.playCount = (song.playCount ?? 0) + 1;
  song.lastPlayedAtUtc = new Date().toISOString();
}

/**
 * Drop all cached data and tear down the live stream. The `(app)` group runs
 * SSR-off, so this module is a singleton that survives a logout → login in the
 * same tab; without this the next user briefly sees the previous session's
 * songs/albums until a refetch lands. Call on sign-out (see `signOutAndReset`).
 */
function reset(): void {
  songs = [];
  grantors = [];
  isLoading = false;
  error = null;
  hasLoaded = false;
  liveRefCount = 0;
  lastBuilt = -1;
  sawActive = false;
  if (refreshTimer) {
    clearTimeout(refreshTimer);
    refreshTimer = null;
  }
  if (liveCleanup) {
    liveCleanup();
    liveCleanup = null;
  }
}

export const songsStore = {
  get songs() {
    return songs;
  },
  get grantors() {
    return grantors;
  },
  /** The grantor of one song, or null when this account owns it. */
  grantorOf(song: Pick<ApiSong, 'sharedByUserId'>): Grantor | null {
    if (!song.sharedByUserId) return null;
    return grantors.find((g) => g.userId === song.sharedByUserId) ?? null;
  },
  get isLoading() {
    return isLoading;
  },
  get error() {
    return error;
  },
  loadSongs,
  ensureLoaded,
  startLive,
  stopLive,
  toggleLike,
  notePlayed,
  reset
};
