/**
 * The requests that load the library on a cold start, written once.
 *
 * `songsStore.loadSongs` asks for these two (through `fetchSongsIfChanged` and
 * `fetchAlbumsIfChanged({}, …)`), with no `If-None-Match` the first time. The server preloads
 * exactly these URLs with the (app) document (`#lib/server/library-preload`), and a browser only
 * hands a preload to a later `fetch()` whose URL is the same. Were the two to drift apart, the
 * preload would be a second, wasted download of the whole library — so both read these constants,
 * and `library-preload.test.ts` loads the real store to check what it asks for.
 */

/** The same-origin proxy every backend call goes through (`src/routes/api/mh/[...path]`). */
export const API_PREFIX = '/api/mh';

/** The song list, relative to {@link API_PREFIX}. */
export const LIBRARY_SONGS_PATH = '/songs?includeDeleted=false';

/** The album cards, relative to {@link API_PREFIX}; the store asks with no filters. */
export const LIBRARY_ALBUMS_PATH = '/api/albums';
