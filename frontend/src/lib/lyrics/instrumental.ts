import type { ApiSong } from '#lib/api-client.ts';

/**
 * LRCLIB's instrumental verdict, which ends the search for lyrics. A song the AI heard no singing in is
 * `isInstrumental` too but keeps its own lyrics status, and stays open to a lyrics re-check or another
 * transcription in case the AI was wrong.
 */
export function isConfirmedInstrumental(
  song: Pick<ApiSong, 'isInstrumental' | 'lyricsStatus'>
): boolean {
  return song.isInstrumental === true && song.lyricsStatus === 'Instrumental';
}
