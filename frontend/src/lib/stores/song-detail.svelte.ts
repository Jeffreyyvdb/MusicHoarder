/**
 * Song-detail store — owns the open/closed state of Now Playing (the full-screen `TrackPanel`
 * overlay). Mounted once via `SongDetailHost` at the app-shell level so it's reachable from every
 * route: the MiniPlayer, Library track rows, row menus, deep-links, and the Cmd/Ctrl+I shortcut
 * all drive the same overlay.
 *
 * Only the song identifier is stored; the `{ album, song, index }` context is
 * resolved live from `songsStore` so the panel stays fresh after enrichment
 * resets / SSE-driven rebuilds replace the dataset.
 */

import { playerStore } from '$lib/stores/player.svelte';
import { songsStore } from '$lib/stores/songs.svelte';

/**
 * What the middle of the player shows. `player` is the artwork, `lyrics` the karaoke view,
 * `video` the music video (only when one is watchable) and `info` the details/inspectors.
 */
export type DetailMode = 'player' | 'lyrics' | 'video' | 'info';

export interface DetailOpenOptions {
  /**
   * Open straight into this mode (a row menu's "Song info" asks for `info`). Without it the
   * player picks its own default: lyrics when the song has any, else the artwork.
   */
  mode?: DetailMode;
}

interface DetailTarget {
  songId: number;
  albumKey?: string;
  mode?: DetailMode;
  /**
   * Bumped on every open(), so re-opening the song that is already showing with a new mode is
   * still a new request the panel applies, rather than a no-op it cannot tell apart.
   */
  seq: number;
}

let target = $state<DetailTarget | null>(null);
let isOpen = $state(false);
let seq = 0;

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

function open(songId: number, albumKey?: string, options?: DetailOpenOptions): void {
  seq += 1;
  target = { songId, albumKey, mode: options?.mode, seq };
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
