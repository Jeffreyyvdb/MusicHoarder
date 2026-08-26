/**
 * Song-detail store — owns the open/closed state for the global song-detail
 * sidebar (the `TrackPanel`). Mounted once via `SongDetailHost` at the app-shell
 * level so it's reachable from every route: the MiniPlayer, Library track rows,
 * deep-links, and the Cmd/Ctrl+I shortcut all drive the same panel.
 *
 * Only the song identifier is stored; the `{ album, song, index }` context is
 * resolved live from `songsStore` so the panel stays fresh after enrichment
 * resets / SSE-driven rebuilds replace the dataset.
 */

import { playerStore } from '$lib/stores/player.svelte';
import { songsStore } from '$lib/stores/songs.svelte';

interface DetailTarget {
  songId: number;
  albumKey?: string;
}

let target = $state<DetailTarget | null>(null);
let isOpen = $state(false);

// The panel's own album set: every song, including unbuilt ones, and per destination folder rather
// than merged by name. Wider than the library grid on purpose — the panel opens from the MiniPlayer
// and from Inbox rows, so a track still waiting on review has to resolve to an album too.
const albums = $derived(songsStore.detailAlbums);

const resolved = $derived.by(() => {
  if (!target) return null;
  const { songId, albumKey } = target;
  const album = albumKey
    ? albums.find((a) => a.key === albumKey) ?? albums.find((a) => a.songs.some((s) => s.id === songId))
    : albums.find((a) => a.songs.some((s) => s.id === songId));
  if (!album) return null;
  const index = album.songs.findIndex((s) => s.id === songId);
  if (index < 0) return null;
  return { album, song: album.songs[index], index };
});

function open(songId: number, albumKey?: string): void {
  target = { songId, albumKey };
  isOpen = true;
  songsStore.ensureLoaded();
  songsStore.ensureDetailAlbums();
}

function close(): void {
  isOpen = false;
  target = null;
}

/** Keyboard-shortcut entry: close if open, else open for the now-playing song. */
function toggle(): void {
  if (isOpen) {
    close();
    return;
  }
  const playing = playerStore.currentSong;
  if (playing) open(playing.id);
}

export const songDetail = {
  get isOpen() {
    return isOpen;
  },
  get target() {
    return target;
  },
  get resolved() {
    return resolved;
  },
  open,
  close,
  toggle
};
