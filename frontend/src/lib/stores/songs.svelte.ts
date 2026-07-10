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

import { fetchSongs, openProgressStream, type ApiSong, type ProgressSnapshot } from '$lib/api-client';

let songs = $state<ApiSong[]>([]);
let isLoading = $state(false);
let error = $state<string | null>(null);
let hasLoaded = false;

async function loadSongs(opts?: { silent?: boolean }): Promise<void> {
  try {
    if (!opts?.silent) isLoading = true;
    const loaded = await fetchSongs();
    songs = loaded;
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

/**
 * Drop all cached data and tear down the live stream. The `(app)` group runs
 * SSR-off, so this module is a singleton that survives a logout → login in the
 * same tab; without this the next user briefly sees the previous session's
 * songs/albums until a refetch lands. Call on sign-out (see `signOutAndReset`).
 */
function reset(): void {
  songs = [];
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
  reset
};
