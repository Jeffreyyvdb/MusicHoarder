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
  fetchAlbums,
  fetchSongs,
  hydrateAlbums,
  likeSong,
  openProgressStream,
  unlikeSong,
  type AlbumSummaryDto,
  type ApiSong,
  type Grantor,
  type ProgressSnapshot
} from '$lib/api-client';

let songs = $state<ApiSong[]>([]);
/**
 * The album cards the server grouped, still as it sent them. Kept apart from {@link albums} so a
 * heart tap or a play can re-join against the mutated song rows without another request.
 */
let albumDtos = $state<AlbumSummaryDto[]>([]);
/** Cards for the song-detail panel: every song, per folder, unmerged. Loaded on first use. */
let detailAlbumDtos = $state<AlbumSummaryDto[]>([]);
let detailAlbumsRequested = false;
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
    // Both in one round trip. They are two views of the same library, so fetching them together
    // keeps the album cards from describing a song list that has already moved on.
    const [loaded, grouped] = await Promise.all([fetchSongs(), fetchAlbums()]);
    songs = loaded;
    albumDtos = grouped;
    if (detailAlbumsRequested) void loadDetailAlbums();
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

async function loadDetailAlbums(): Promise<void> {
  try {
    detailAlbumDtos = await fetchAlbums({ builtOnly: false, merge: false });
  } catch {
    // The panel degrades to no album context; the library grid is unaffected.
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
    // Liking an album-fill track promotes it to "yours", which can move the album's added-date and
    // so its place in "Recently added". That rule is the server's now, so ask it again rather than
    // keeping a second copy here — it is one small request, and only on a real like.
    if (song.acquisitionIntent === 'AlbumFill' || song.isAlbumFill) void refreshAlbums();
  } catch (err) {
    song.likedAtUtc = previous;
    throw err;
  }
}

/** Re-fetch just the album cards, leaving the song rows (and their overlays) alone. */
async function refreshAlbums(): Promise<void> {
  try {
    albumDtos = await fetchAlbums();
    if (detailAlbumsRequested) await loadDetailAlbums();
  } catch {
    // Keep the cards we have; the next full refresh reconciles.
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
  albumDtos = [];
  detailAlbumDtos = [];
  detailAlbumsRequested = false;
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

/**
 * Song rows by id, rebuilt whenever the dataset changes. The values are the store's own `$state`
 * proxies, which is what lets an album view see a heart tap without a refetch.
 */
const songsById = $derived(new Map(songs.map((song) => [song.id, song])));

const albums = $derived(hydrateAlbums(albumDtos, songsById));
const detailAlbums = $derived(hydrateAlbums(detailAlbumDtos, songsById));

export const songsStore = {
  get songs() {
    return songs;
  },
  /**
   * The store's rows by id — the objects themselves, so anything joining against them keeps seeing
   * the optimistic like/play overlays. Use with {@link hydrateAlbums}.
   */
  get songsById() {
    return songsById;
  },
  /** The library's album cards: built songs only, folder-split albums folded together. */
  get albums() {
    return albums;
  },
  /**
   * Cards for the song-detail panel — every song including unbuilt ones, one card per destination
   * folder. Empty until {@link ensureDetailAlbums} has been called.
   */
  get detailAlbums() {
    return detailAlbums;
  },
  /**
   * Start loading the detail panel's cards. Only that panel needs them, so nothing else pays for
   * them; called when the panel opens rather than from the getter, because a read that quietly
   * starts a fetch is a side effect inside whatever `$derived` happens to touch it.
   */
  ensureDetailAlbums(): void {
    if (detailAlbumsRequested) return;
    detailAlbumsRequested = true;
    void loadDetailAlbums();
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
  refreshAlbums,
  ensureLoaded,
  startLive,
  stopLive,
  toggleLike,
  notePlayed,
  reset
};
