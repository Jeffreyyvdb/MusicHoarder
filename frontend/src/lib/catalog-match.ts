import type { ApiSong, CatalogTrack } from '#lib/api-client.ts';
import { normalizeForSearch } from '#lib/search/match.ts';

const SEPARATOR = '\u0000';

function keyOf(title: string, artist: string): string | null {
  const t = normalizeForSearch(title);
  const a = normalizeForSearch(artist);
  return t && a ? `${t}${SEPARATOR}${a}` : null;
}

type OwnedRow = Pick<ApiSong, 'title' | 'artist' | 'artists' | 'sharedByUserId'>;

/**
 * Folded title + lead artist of every song this account owns. A catalog result names one artist,
 * while a row may credit several in `artist`, so each row is indexed under its whole credit and
 * under its first discrete credit. A song shared by someone else is not in the account's library.
 */
export function buildOwnedIndex(songs: readonly OwnedRow[]): ReadonlySet<string> {
  const keys = new Set<string>();
  for (const song of songs) {
    if (song.sharedByUserId || !song.title) continue;
    const credits = [song.artist, song.artists?.split(';')[0]];
    for (const credit of credits) {
      const key = credit ? keyOf(song.title, credit) : null;
      if (key) keys.add(key);
    }
  }
  return keys;
}

export function isInLibrary(
  index: ReadonlySet<string>,
  track: Pick<CatalogTrack, 'title' | 'artist'>
): boolean {
  const key = keyOf(track.title, track.artist);
  return key !== null && index.has(key);
}
