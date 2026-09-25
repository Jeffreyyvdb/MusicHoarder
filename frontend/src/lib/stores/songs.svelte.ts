/**
 * Shared songs store — owns the full `ApiSong[]` dataset plus the live
 * SSE-driven refresh, lifted out of LibraryV2 so any route can resolve a song
 * (e.g. the global Now Playing overlay opened from the MiniPlayer off-Library).
 *
 * `startLive`/`stopLive` are ref-counted: LibraryV2 and the detail host can both
 * keep the progress stream alive, and it only tears down once the last consumer
 * releases it — so navigating away from Library doesn't kill the stream while
 * the detail panel is still open elsewhere.
 */

import {
  buildArtistGroups,
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
import { isBuiltSong } from '$lib/album-sections';
import { isTrackListSong } from '$lib/track-list-view.svelte';

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
 * Where the detail cards stand. 'loaded' sticks through a background refresh (and a failed one:
 * the cards already held are still right enough), so the panel's "not in your library view" can
 * trust it; 'error' is only a first load that failed, which the panel offers to retry.
 */
export type DetailAlbumsState = 'idle' | 'loading' | 'loaded' | 'error';
let detailAlbumsState = $state<DetailAlbumsState>('idle');
let detailAlbumsGen = 0;
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
/** When the last successful load landed (epoch ms) — what {@link revalidate} measures against. */
let loadedAt = 0;
/** Loads in flight, silent ones included, so a page opening mid-load does not start another. */
let loadsInFlight = 0;

/**
 * How long a loaded library is trusted when a page opens. Opening the Overview, Tracks or an album
 * used to download the whole library again and re-render every row — on a phone, most of what
 * moving between those pages cost, and a stall just as you started to scroll. The live stream
 * still refreshes it as soon as the pipeline builds something; this only decides whether a page
 * visit asks again.
 */
const REVALIDATE_AFTER_MS = 30_000;

async function loadSongs(opts?: { silent?: boolean }): Promise<void> {
  loadsInFlight += 1;
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
    loadedAt = Date.now();
    error = null;
  } catch (err) {
    error = err instanceof Error ? err.message : 'Failed to load library';
  } finally {
    loadsInFlight -= 1;
    if (!opts?.silent) isLoading = false;
  }
}

/**
 * A library page opened: load the library when there is none yet, refresh it in the background
 * when the copy held is older than {@link REVALIDATE_AFTER_MS}, and otherwise use it as it is.
 */
function revalidate(): void {
  if (loadsInFlight > 0) return;
  if (!hasLoaded) void loadSongs();
  else if (Date.now() - loadedAt >= REVALIDATE_AFTER_MS) void loadSongs({ silent: true });
}

async function loadDetailAlbums(): Promise<void> {
  const gen = ++detailAlbumsGen;
  if (detailAlbumsState !== 'loaded') detailAlbumsState = 'loading';
  try {
    const loaded = await fetchAlbums({ builtOnly: false, merge: false });
    if (gen !== detailAlbumsGen) return; // a newer request (or a sign-out) superseded this one
    detailAlbumDtos = loaded;
    detailAlbumsState = 'loaded';
  } catch {
    if (gen !== detailAlbumsGen) return;
    // The library grid is unaffected either way; the panel says so and offers a retry.
    if (detailAlbumsState !== 'loaded') detailAlbumsState = 'error';
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
/**
 * How long the stream stays open once the last page lets go of it. Moving between library pages
 * releases it and takes it straight back; closing it in between made every such move reopen it,
 * and a reopened stream's first snapshot refetches the whole library a few seconds later.
 */
const LIVE_LINGER_MS = 10_000;
let lingerTimer: ReturnType<typeof setTimeout> | null = null;

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
  if (lingerTimer) {
    clearTimeout(lingerTimer);
    lingerTimer = null;
  }
  if (liveRefCount === 1) openStream();
}

function stopLive(): void {
  liveRefCount = Math.max(0, liveRefCount - 1);
  if (liveRefCount > 0 || lingerTimer) return;
  lingerTimer = setTimeout(() => {
    lingerTimer = null;
    if (liveRefCount === 0) closeLive();
  }, LIVE_LINGER_MS);
}

function closeLive(): void {
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
  detailAlbumsState = 'idle';
  detailAlbumsGen += 1;
  grantors = [];
  isLoading = false;
  error = null;
  hasLoaded = false;
  loadedAt = 0;
  liveRefCount = 0;
  lastBuilt = -1;
  sawActive = false;
  if (lingerTimer) {
    clearTimeout(lingerTimer);
    lingerTimer = null;
  }
  closeLive();
}

/**
 * Song rows by id, rebuilt whenever the dataset changes. The values are the store's own `$state`
 * proxies, which is what lets an album view see a heart tap without a refetch.
 */
const songsById = $derived(new Map(songs.map((song) => [song.id, song])));

const albums = $derived(hydrateAlbums(albumDtos, songsById));
const detailAlbums = $derived(hydrateAlbums(detailAlbumDtos, songsById));

// The library's shared cuts, read by the Overview, the Library tabs and the command palette alike.
// Filtering or grouping the whole library reads every row through its `$state` proxy, which is the
// bulk of what a page costs to open once a library runs to thousands of songs — so it is done once
// here, not once per page visit. They are only reused while something watches them, though: Svelte
// recomputes a derived that nothing is subscribed to on every read, and a page's effects are torn
// down as you leave it. `retainViews` is that watcher, held by the app shell for as long as it lives.
/** Songs built into the library — what the album and artist grids are made of. */
const builtSongs = $derived(songs.filter(isBuiltSong));
/** What the Tracks list covers (see `isTrackListSong`). */
const trackListSongs = $derived(songs.filter(isTrackListSong));
/** The Artists grid's default: built songs grouped by lead artist. */
const leadArtistGroups = $derived(buildArtistGroups(builtSongs, { primaryOnly: true }));

let viewHolds = 0;
let releaseViews: (() => void) | null = null;

function retainViews(): () => void {
  if (viewHolds++ === 0) {
    releaseViews = $effect.root(() => {
      $effect(() => {
        void builtSongs;
        void trackListSongs;
        void leadArtistGroups;
      });
    });
  }
  let released = false;
  return () => {
    if (released) return;
    released = true;
    if (--viewHolds === 0) {
      releaseViews?.();
      releaseViews = null;
    }
  };
}

// Whether the rows on screen come from more than one library: two grantors, or a grant beside the
// account's own music (an admin someone shared with). Only then does a per-row "shared" mark tell
// a row apart; with one source the list subtitle's "Shared by X" already says it for every row.
const mixedSources = $derived(
  grantors.length > 1 || (grantors.length > 0 && songs.some((s) => !s.sharedByUserId))
);

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
  /** Built songs only — see the shared cuts above. */
  get builtSongs() {
    return builtSongs;
  },
  /** The Tracks list's base, which the Overview's Library rows count too. */
  get trackListSongs() {
    return trackListSongs;
  },
  /** Built songs by lead artist: the Artists grid's default grouping and the Overview's count. */
  get leadArtistGroups() {
    return leadArtistGroups;
  },
  /**
   * Keep the shared cuts computed while the caller lives, so a page visit reuses them instead of
   * re-reading every row. Returns the release; the app shell holds one for the whole session.
   */
  retainViews,
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
  /** {@link DetailAlbumsState}: whether an unresolved song means "missing" or "not loaded yet". */
  get detailAlbumsState() {
    return detailAlbumsState;
  },
  /** Fetch the detail cards again now — the panel's Retry after a failed first load. */
  reloadDetailAlbums(): Promise<void> {
    detailAlbumsRequested = true;
    return loadDetailAlbums();
  },
  get grantors() {
    return grantors;
  },
  /** More than one library is in the list — see `mixedSources` above. */
  get hasMixedSources() {
    return mixedSources;
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
  revalidate,
  refreshAlbums,
  ensureLoaded,
  startLive,
  stopLive,
  toggleLike,
  notePlayed,
  reset
};
