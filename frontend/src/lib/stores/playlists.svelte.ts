/**
 * The account's playlists — the ones made here and the ones that follow a collected Spotify, Deezer
 * or YouTube playlist — shared by the Playlists page, a playlist's page and the "Add to playlist"
 * sheet, so adding a song from a row updates the page behind it without another request.
 *
 * A playlist holds song ids; pages join them against `songsStore.songsById` (see
 * {@link playlistSongs}), the same way album cards join their track ids.
 */

import {
  addPlaylistSongs,
  createPlaylist,
  deletePlaylist,
  fetchPlaylists,
  removePlaylistSong,
  reorderPlaylistSongs,
  updatePlaylist,
  type ApiSong,
  type LibraryPlaylist,
  type PlaylistSongsResult
} from '$lib/api-client';

let playlists = $state<LibraryPlaylist[]>([]);
let canExport = $state(false);
let isLoading = $state(false);
let error = $state<string | null>(null);
let hasLoaded = $state(false);
let loadedAt = 0;
let inFlight: Promise<void> | null = null;

const byId = $derived(new Map(playlists.map((p) => [p.id, p])));

/**
 * How long a loaded list is trusted when a page opens. A synced playlist grows as its downloads
 * land, so a visit after this asks again — in the background, over what is already shown.
 */
const REVALIDATE_AFTER_MS = 30_000;

async function load(opts?: { silent?: boolean }): Promise<void> {
  if (inFlight) return inFlight;
  inFlight = (async () => {
    if (!opts?.silent) isLoading = true;
    try {
      const response = await fetchPlaylists();
      playlists = response.playlists;
      canExport = response.canExport;
      hasLoaded = true;
      loadedAt = Date.now();
      error = null;
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not load playlists';
    } finally {
      if (!opts?.silent) isLoading = false;
      inFlight = null;
    }
  })();
  return inFlight;
}

/** A page opened: load when nothing is held, refresh quietly when the copy is stale. */
function revalidate(): void {
  if (!hasLoaded) void load();
  else if (Date.now() - loadedAt >= REVALIDATE_AFTER_MS) void load({ silent: true });
}

/** Put the server's copy of one playlist in place (or add it). */
function upsert(playlist: LibraryPlaylist): LibraryPlaylist {
  const index = playlists.findIndex((p) => p.id === playlist.id);
  if (index === -1) playlists = [...playlists, playlist].sort(byName);
  else playlists[index] = playlist;
  return playlist;
}

function byName(a: LibraryPlaylist, b: LibraryPlaylist): number {
  return a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }) || a.id - b.id;
}

async function create(name: string, songIds: number[] = []): Promise<LibraryPlaylist> {
  return upsert(await createPlaylist(name, songIds));
}

async function addSongs(id: number, songIds: number[]): Promise<PlaylistSongsResult> {
  const result = await addPlaylistSongs(id, songIds);
  upsert(result.playlist);
  return result;
}

async function removeSong(id: number, songId: number): Promise<LibraryPlaylist> {
  return upsert(await removePlaylistSong(id, songId));
}

async function reorder(id: number, songIds: number[]): Promise<LibraryPlaylist> {
  return upsert(await reorderPlaylistSongs(id, songIds));
}

async function rename(id: number, name: string): Promise<LibraryPlaylist> {
  return upsert(await updatePlaylist(id, { name }));
}

async function setExport(id: number, exportToLibrary: boolean): Promise<LibraryPlaylist> {
  return upsert(await updatePlaylist(id, { exportToLibrary }));
}

async function remove(id: number): Promise<void> {
  await deletePlaylist(id);
  playlists = playlists.filter((p) => p.id !== id);
}

/**
 * A playlist's songs in play order, joined against the library the client holds. Ids the list does
 * not hold (a song still loading, or one that left the library since) are skipped.
 */
export function playlistSongs(
  playlist: Pick<LibraryPlaylist, 'songIds'>,
  songsById: ReadonlyMap<number, ApiSong>
): ApiSong[] {
  const out: ApiSong[] = [];
  for (const id of playlist.songIds) {
    const song = songsById.get(id);
    if (song) out.push(song);
  }
  return out;
}

export const playlistsStore = {
  get playlists() {
    return playlists;
  },
  get byId() {
    return byId;
  },
  /** Whether this account's playlists can be written into the library (the library owner's). */
  get canExport() {
    return canExport;
  },
  get isLoading() {
    return isLoading;
  },
  get hasLoaded() {
    return hasLoaded;
  },
  get error() {
    return error;
  },
  load,
  revalidate,
  create,
  addSongs,
  removeSong,
  reorder,
  rename,
  setExport,
  remove
};
