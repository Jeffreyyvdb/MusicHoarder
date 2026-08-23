/**
 * The friend-side counterpart of `songsStore`: the songs other accounts shared with the current
 * one, fetched from the grant-scoped `/api/shared` endpoints. Deliberately trimmed — no SSE live
 * refresh (friends have no pipeline to watch), no likes/plays (owner-only columns today). Albums
 * and artists are derived client-side with the same helpers the owner library uses.
 *
 * Like every module-scoped store in an ssr=false group, it survives a logout → login in the same
 * tab, so `reset()` is called from `signOutAndReset`.
 */

import { buildAlbumsFromSongs, fetchSharedSongs, getSharedSongCoverUrl, type AlbumSummary, type ApiSong } from '$lib/api-client';

let songs = $state<ApiSong[]>([]);
let isLoading = $state(false);
let error = $state<string | null>(null);
let hasLoaded = false;

async function load(opts?: { silent?: boolean }): Promise<void> {
  try {
    if (!opts?.silent) isLoading = true;
    const loaded = await fetchSharedSongs();
    // Stamp each row's cover to the shared endpoint here, once: coverUrlForSong prefers
    // `albumArt`, so every downstream surface (grid, player, media session) resolves art
    // through the grant-scoped route without knowing it's a shared song.
    for (const song of loaded) {
      if (song.hasCoverArt) song.albumArt = getSharedSongCoverUrl(song.id);
    }
    songs = loaded;
    hasLoaded = true;
    error = null;
  } catch (err) {
    error = err instanceof Error ? err.message : 'Failed to load shared music';
  } finally {
    if (!opts?.silent) isLoading = false;
  }
}

/** Fetch once if we have no data yet and aren't already loading. */
function ensureLoaded(): void {
  if (hasLoaded || isLoading) return;
  void load();
}

function reset(): void {
  songs = [];
  isLoading = false;
  error = null;
  hasLoaded = false;
}

export const sharedLibraryStore = {
  get songs() {
    return songs;
  },
  get albums(): AlbumSummary[] {
    return buildAlbumsFromSongs(songs);
  },
  get isLoading() {
    return isLoading;
  },
  get error() {
    return error;
  },
  load,
  ensureLoaded,
  reset
};
