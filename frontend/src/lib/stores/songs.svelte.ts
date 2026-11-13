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

import { untrack } from 'svelte';
import {
  buildArtistGroups,
  currentGrantors,
  fetchAlbums,
  fetchAlbumsIfChanged,
  fetchSongsIfChanged,
  hydrateAlbums,
  likeSong,
  openProgressStream,
  unlikeSong,
  type AlbumQuery,
  type AlbumSummaryDto,
  type ApiSong,
  type Grantor,
  type ProgressSnapshot,
  type Revalidated
} from '#lib/api-client.ts';
import { isBuiltSong } from '#lib/album-sections.ts';
import { isTrackListSong } from '#lib/track-list-view.svelte.ts';

/**
 * Every row the caller may see. `$state.raw`: the rows are plain objects and the array is replaced,
 * never edited in place — a deep proxy over thousands of rows of seventy fields put a trap, and
 * a signal per field read, between every filter, sort and render and the data. A change to one
 * row goes through {@link patchSong}, which swaps that row and the array.
 */
let songs = $state.raw<ApiSong[]>([]);
/**
 * The album cards the server grouped, still as it sent them. Kept apart from {@link albums} so a
 * heart tap can re-join against the changed song rows without another request.
 */
let albumDtos = $state.raw<AlbumSummaryDto[]>([]);
/** Cards for the song-detail panel: every song, per folder, unmerged. Loaded on first use. */
let detailAlbumDtos = $state.raw<AlbumSummaryDto[]>([]);
let detailAlbumsRequested = false;
/**
 * Where the detail cards stand. 'loaded' sticks through a background refresh (and a failed one:
 * the cards already held are still right enough), so the panel's "not in your library view" can
 * trust it; 'error' is only a first load that failed, which the panel offers to retry.
 */
export type DetailAlbumsState = 'idle' | 'loading' | 'loaded' | 'error';
let detailAlbumsState = $state<DetailAlbumsState>('idle');
let detailAlbumsGen = 0;
/** The query behind {@link detailAlbumDtos}: every song, per folder, unmerged. */
const DETAIL_ALBUMS: AlbumQuery = { builtOnly: false, merge: false };
/**
 * The validator (ETag) each list arrived with, sent back on the next load so the server can answer
 * "unchanged" instead of the whole list — most refreshes of a library find nothing new, and used
 * to download and re-render every row to learn that.
 *
 * One rule keeps them honest: **a validator is only ever set in the same statement block as the
 * rows it came with, and is dropped the moment the store's copy stops being those rows** — on
 * sign-out ({@link reset}), and when a row is changed locally ({@link patchSong}). So a non-null
 * validator always means "the array held is exactly what the server sent under this tag", and a
 * load with nothing held asks for the list outright. Memory only, like the rows: a reload or
 * another account starts with none.
 */
let songsEtag: string | null = null;
let albumsEtag: string | null = null;
let detailAlbumsEtag: string | null = null;
/**
 * Who shared the rows in {@link songs}. Lives HERE, as a rune, rather than in `api-client`:
 * that module is a plain `.ts` file, so its module-level copy cannot be reactive, and a
 * `$derived` reading it would compute once before the first fetch resolves and then stay clean
 * forever — the "shared by X" header would simply never appear on a cold load.
 */
let grantors = $state<Grantor[]>([]);

/** What a play changes about a song. */
export type PlayStats = Pick<ApiSong, 'playCount' | 'lastPlayedAtUtc'>;
/**
 * Plays counted here since the rows were fetched, by song id; read through {@link playStatsOf}.
 * They are kept beside the rows rather than patched into them, because a patch replaces
 * {@link songs}, and everything derived from the list then re-derives: the built and Tracks cuts,
 * the artist groups, every mounted list's sort. That was a tenth of a second on a phone with
 * 10,000 songs, at every track start, for two fields that only the Overview's shelves and the
 * palette's "recently played" read. `$state.raw`, and replaced like the rows.
 */
let playStats = $state.raw<ReadonlyMap<number, PlayStats>>(new Map());
/**
 * Plays noted and not shown yet. The player notes a play right after `play()`, and whatever that
 * changed would re-derive in the same task, ahead of the browser asking for the audio. So a play
 * is only written down there, and the views that show it catch up once the song is under way.
 */
let pendingPlays: { id: number; at: string }[] = [];
let playsTimer: ReturnType<typeof setTimeout> | null = null;
/** How long after a track starts its play is shown: long enough for the sound to have started. */
const PLAY_SHOWN_AFTER_MS = 1000;

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

/**
 * Whether an answer can be applied to what the store holds now. Rows always can. "Unchanged" is
 * about the copy whose validator was sent, so it only counts while that is still the copy held: a
 * sign-out, a like or another load landing while the request was out leaves it describing rows
 * the store no longer has — after a sign-out, no rows at all.
 */
function appliesTo<T>(answer: Revalidated<T>, held: string | null): boolean {
  return !answer.notModified || answer.etag === held;
}

async function loadSongs(opts?: { silent?: boolean }): Promise<void> {
  loadsInFlight += 1;
  try {
    if (!opts?.silent) isLoading = true;
    // Both in one round trip. They are two views of the same library, so fetching them together
    // keeps the album cards from describing a song list that has already moved on.
    let [loaded, grouped] = await Promise.all([
      fetchSongsIfChanged(songsEtag),
      fetchAlbumsIfChanged({}, albumsEtag)
    ]);
    // An "unchanged" that no longer applies is answered by asking for the lists themselves, which
    // can only come back as rows — so nothing below can mark the library loaded without any. No
    // await separates this check from the assignments, so it cannot go stale in between.
    while (!appliesTo(loaded, songsEtag) || !appliesTo(grouped, albumsEtag)) {
      [loaded, grouped] = await Promise.all([
        fetchSongsIfChanged(null),
        fetchAlbumsIfChanged({}, null)
      ]);
    }
    // "Unchanged" leaves the array alone on purpose: replacing it, even with equal rows, would
    // recompute every view derived from it and re-render every row on screen.
    if (!loaded.notModified) {
      songs = loaded.data;
      songsEtag = loaded.etag;
      forgetPlays(); // the server's counts are the truth again
      // Read AFTER the await: the fetch populates the api-client's copy as it resolves.
      grantors = currentGrantors();
    }
    if (!grouped.notModified) {
      albumDtos = grouped.data;
      albumsEtag = grouped.etag;
    }
    if (detailAlbumsRequested) void loadDetailAlbums();
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
    const loaded = await fetchAlbumsIfChanged(DETAIL_ALBUMS, detailAlbumsEtag);
    if (gen !== detailAlbumsGen) return; // a newer request (or a sign-out) superseded this one
    // Only this function and reset() touch the detail cards, and both move the generation — so an
    // "unchanged" that gets past the line above is about the cards still held.
    if (!loaded.notModified) {
      detailAlbumDtos = loaded.data;
      detailAlbumsEtag = loaded.etag;
    }
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
  // lastBuilt is kept across a reopen: what was built while the stream was closed still shows up
  // as a change, and a stream that reopens onto the same count refetches nothing.
  sawActive = false;
  liveCleanup = openProgressStream(
    (snap: ProgressSnapshot) => {
      if (lastBuilt === -1) {
        // The first snapshot this document sees is where the count stands, not a change to it.
        // Treating it as one refetched the whole library a few seconds after every page load.
        lastBuilt = snap.built;
      } else if (snap.built !== lastBuilt) {
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
 * Change one row: a new object in a new array, so everything derived from the list sees it. The
 * rows are not proxies, so assigning to a field of one would change nothing on screen.
 */
function patchSong(id: number, patch: Partial<ApiSong>): void {
  const index = songs.findIndex((s) => s.id === id);
  if (index < 0) return;
  const next = songs.slice();
  next[index] = { ...songs[index], ...patch };
  songs = next;
  // No longer the rows the server sent, so its validator no longer describes them: the next load
  // asks for the list itself and overwrites this, as a refresh after a like or a play always has.
  songsEtag = null;
}

/**
 * Toggle a song's liked state. Updates the store row optimistically (see {@link patchSong}, so
 * every view reacts) and reverts on API failure — callers surface the thrown error (e.g. demo
 * read-only) however they like.
 */
async function toggleLike(id: number): Promise<void> {
  const song = findSong(id);
  if (!song) return;
  const previous = song.likedAtUtc ?? null;
  const liking = !previous;
  patchSong(id, { likedAtUtc: liking ? new Date().toISOString() : null });
  try {
    const result = liking ? await likeSong(id) : await unlikeSong(id);
    patchSong(id, { likedAtUtc: result.likedAtUtc });
    // Liking an album-fill track promotes it to "yours", which can move the album's added-date and
    // so its place in "Recently added". That rule is the server's now, so ask it again rather than
    // keeping a second copy here — it is one small request, and only on a real like.
    if (song.acquisitionIntent === 'AlbumFill' || song.isAlbumFill) void refreshAlbums();
  } catch (err) {
    patchSong(id, { likedAtUtc: previous });
    throw err;
  }
}

/** Re-fetch just the album cards, leaving the song rows (and their overlays) alone. */
async function refreshAlbums(): Promise<void> {
  try {
    albumDtos = await fetchAlbums();
    // Fetched without a validator; the one held came with the cards this just replaced.
    albumsEtag = null;
    if (detailAlbumsRequested) await loadDetailAlbums();
  } catch {
    // Keep the cards we have; the next full refresh reconciles.
  }
}

/**
 * Reflect a play reported by the player without waiting for the next full refetch. Called as a
 * track starts, so it does no more there than write the play down (see {@link pendingPlays}).
 */
function notePlayed(id: number): void {
  // A track that is not a row here (one streamed through a share link) has no count to show, and
  // the rows stay exactly what the server sent: there is nothing to write down, and nothing that
  // should cost them their validator and the next refresh a download of the whole list. Looked
  // up untracked: this runs in whatever started the track, and must not tie it to the song list.
  if (!untrack(() => findSong(id))) return;
  pendingPlays.push({ id, at: new Date().toISOString() });
  playsTimer ??= setTimeout(showPlays, PLAY_SHOWN_AFTER_MS);
  // What is shown is about to differ from what the server sent, so its validator must not vouch
  // for it: the next load asks for the list itself and brings the server's own count back. That
  // is the only count there is when the report of this play was refused (the demo account) or
  // never arrived, and "unchanged" would leave this one standing.
  songsEtag = null;
}

/** Count the plays noted since the last time, in one change. */
function showPlays(): void {
  playsTimer = null;
  const plays = pendingPlays;
  pendingPlays = [];
  const next = new Map(playStats);
  let counted = false;
  for (const { id, at } of plays) {
    const song = findSong(id);
    if (!song) continue; // not a row of this library (a share link's track): nothing shows it
    const before = next.get(id) ?? song;
    next.set(id, { playCount: (before.playCount ?? 0) + 1, lastPlayedAtUtc: at });
    counted = true;
  }
  if (counted) playStats = next;
}

/** Drop the plays counted here: the rows they were counted on top of are gone. */
function forgetPlays(): void {
  if (playsTimer) {
    clearTimeout(playsTimer);
    playsTimer = null;
  }
  pendingPlays = [];
  if (playStats.size > 0) playStats = new Map();
}

/**
 * A song's play count and last play, including the plays counted here since its row was fetched.
 * The row's own two fields are the server's and go stale at the first play, so read these instead.
 */
function playStatsOf(song: Pick<ApiSong, 'id' | 'playCount' | 'lastPlayedAtUtc'>): PlayStats {
  return playStats.get(song.id) ?? song;
}

/**
 * Drop all cached data and tear down the live stream. The `(app)` group runs
 * SSR-off, so this module is a singleton that survives a logout → login in the
 * same tab; without this the next user briefly sees the previous session's
 * songs/albums until a refetch lands. Call on sign-out (see `signOutAndReset`).
 */
function reset(): void {
  songs = [];
  forgetPlays();
  lastBuilt = -1;
  albumDtos = [];
  detailAlbumDtos = [];
  // With the rows go their validators: a load after this must ask for the lists outright, and an
  // "unchanged" still on its way back must find nothing here that it could be about.
  songsEtag = null;
  albumsEtag = null;
  detailAlbumsEtag = null;
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
   * an optimistic like. Use with {@link hydrateAlbums}. A play is not on the row: see
   * {@link playStatsOf}.
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
  playStatsOf,
  reset
};
