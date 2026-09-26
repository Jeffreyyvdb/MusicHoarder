import { getSongCoverUrl, type ApiSong } from '$lib/api-client';

/**
 * "Send to…": which song or album the people picker is sending, if any. One sheet for the whole app
 * (SendToSheet, mounted by the (app) layout), opened from a song's ⋯ menu, an album's ⋯ and Now
 * Playing's ⋯ — so the menus only name the thing, and none of them mounts a sheet inside itself.
 */

export type SendToItem = {
  songId: number;
  scope: 'song' | 'album';
  /** The song or album title, for the sheet's header card. */
  title: string;
  subtitle?: string | null;
  coverUrl?: string | null;
  /** Opened from inside Now Playing (z-60): the sheet lifts above it. */
  nested?: boolean;
};

let item = $state<SendToItem | null>(null);
let open = $state(false);

export const sendTo = {
  get item(): SendToItem | null {
    return item;
  },
  get open(): boolean {
    return open;
  },
  set open(value: boolean) {
    open = value;
  },
  show(next: SendToItem): void {
    item = next;
    open = true;
  }
};

/** The Send to… item for one song. */
export function sendToSong(song: ApiSong, opts: { nested?: boolean } = {}): SendToItem {
  return {
    songId: song.id,
    scope: 'song',
    title: (song.title ?? song.fileName).trim() || song.fileName,
    subtitle: song.artist ?? song.albumArtist ?? null,
    coverUrl: song.hasCoverArt ? getSongCoverUrl(song.id, 120) : null,
    nested: opts.nested
  };
}

/** The Send to… item for an album, carried by one of its tracks (the link covers the whole album). */
export function sendToAlbum(first: ApiSong, title: string, artist: string | null | undefined): SendToItem {
  return {
    songId: first.id,
    scope: 'album',
    title,
    subtitle: artist ?? null,
    coverUrl: first.hasCoverArt ? getSongCoverUrl(first.id, 120) : null
  };
}
