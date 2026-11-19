/**
 * What pressing a media toggle (Lyrics, Video) in Now Playing should do for the song on screen:
 * `ready` opens the mode, `offer` asks whether to fetch or generate the media first, `none` leaves
 * the button disabled because nothing here can produce it.
 */
export type MediaAvailability = 'ready' | 'offer' | 'none';

export function lyricsAvailability(i: {
  hasLyrics: boolean;
  /** An AI run is in progress; Lyrics mode shows its progress. */
  busy: boolean;
  instrumental: boolean;
  canRecheck: boolean;
  canEnhance: boolean;
}): MediaAvailability {
  if (i.hasLyrics || i.busy) return 'ready';
  if (i.instrumental || (!i.canRecheck && !i.canEnhance)) return 'none';
  return 'offer';
}

export function videoAvailability(i: {
  playable: boolean;
  /** The server has answered for this song; before that nothing is known. */
  settled: boolean;
  canManage: boolean;
}): MediaAvailability {
  if (i.playable) return 'ready';
  if (!i.settled || !i.canManage) return 'none';
  return 'offer';
}
