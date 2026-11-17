import { sortAlbumsByLastPlayed, type AlbumSummary, type ApiSong } from '#lib/api-client.ts';

/** Where an album card leads. */
export function albumHref(album: Pick<AlbumSummary, 'key'>): string {
  return `/library?album=${encodeURIComponent(album.key)}`;
}

/**
 * The "Last played" shelf: the albums played most recently, newest first. The Overview and the
 * app shell's Last played widget both read it here, so they cannot disagree about the order.
 * `lastPlayedOf` is the store's play stats, because a row's own stamp goes stale at the first play.
 */
export function lastPlayedAlbums(
  albums: readonly AlbumSummary[],
  lastPlayedOf: (song: ApiSong) => string | null | undefined,
  limit: number
): AlbumSummary[] {
  return sortAlbumsByLastPlayed(albums, lastPlayedOf).slice(0, limit);
}
