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

import { playerStore } from '#lib/stores/player.svelte.ts';
import { songsStore } from '#lib/stores/songs.svelte.ts';

/**
 * What the middle of the player shows. `player` is the artwork, `lyrics` the karaoke view,
 * `video` the music video (only when one is watchable), `info` the details/inspectors and `queue`
 * what plays next (only while a queue is loaded on this device).
 */
export type DetailMode = 'player' | 'lyrics' | 'video' | 'info' | 'queue';

export interface DetailOpenOptions {
  /**
   * Open straight into this mode (a row menu's "Song info" asks for `info`). Without it the
   * player opens on the artwork.
   */
  mode?: DetailMode;
  /**
   * Plays the list the song was opened from (a playlist, the Tracks list), starting at that song,
   * as the list's own tap would. Without it, Play on a browsed song plays the song's album.
   */
  play?: () => void;
}

interface DetailTarget {
  songId: number;
  albumKey?: string;
  mode?: DetailMode;
  play?: () => void;
  /**
   * Bumped on every open(), so re-opening the song that is already showing with a new mode is
   * still a new request the panel applies, rather than a no-op it cannot tell apart.
   */
  seq: number;
}

let target = $state<DetailTarget | null>(null);
let isOpen = $state(false);
// Whether the overlay is fully over the app: set by SongDetailHost once its rise has finished, and
// cleared once its fall has. What hides behind it (the tab bar, the mini player) follows this, not
// `isOpen`, so that work stays out of the slide's frames.
let covering = $state(false);
let seq = 0;

// The panel's own album set: every song, including unbuilt ones, and per destination folder rather
// than merged by name. Wider than the library grid on purpose — the panel opens from the MiniPlayer
// and from Inbox rows, so a track still waiting on review has to resolve to an album too.
const albums = $derived(songsStore.detailAlbums);

const resolved = $derived.by(() => {
  if (!target) return null;
  const { songId, albumKey } = target;
  const album = albumKey
    ? (albums.find((a) => a.key === albumKey) ??
      albums.find((a) => a.songs.some((s) => s.id === songId)))
    : albums.find((a) => a.songs.some((s) => s.id === songId));
  if (!album) return null;
  const index = album.songs.findIndex((s) => s.id === songId);
  if (index < 0) return null;
  return { album, song: album.songs[index], index };
});

function open(songId: number, albumKey?: string, options?: DetailOpenOptions): void {
  seq += 1;
  target = { songId, albumKey, mode: options?.mode, play: options?.play, seq };
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
  get covering() {
    return covering;
  },
  setCovering(value: boolean) {
    covering = value;
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
