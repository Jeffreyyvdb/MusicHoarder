import { describe, expect, it } from 'vitest';
import {
  hydrateAlbums,
  songAddedTime,
  songLikedTime,
  sortAlbums,
  type AlbumSummary,
  type AlbumSummaryDto,
  type ApiSong
} from './api-client';

/*
 * What is left of album grouping on this side.
 *
 * The grouping itself — folder keys, the name merge, the year election, the added-date rule — moved
 * to `GET /api/albums`, and is pinned by `AlbumProjectionTests` in the API suite. It used to live
 * here in full AND in a second full copy in the Android client, which is how one added-date rule
 * came to need fixing twice. What stays here is what depends on client state: the per-song stamps
 * the track lists sort on, the grid's own ordering, and the join that turns server cards back into
 * the song rows this app holds.
 */

// Minimal ApiSong factory — only the fields these helpers read.
function song(over: Partial<ApiSong>): ApiSong {
  return {
    id: 0,
    fileName: 'track.flac',
    artist: 'Kanye West',
    albumArtist: 'Kanye West',
    album: 'My Beautiful Dark Twisted Fantasy',
    ...over
  } as ApiSong;
}

/** Minimal album card as the API sends it. */
function card(over: Partial<AlbumSummaryDto> = {}): AlbumSummaryDto {
  const artist = over.artist ?? 'Kanye West';
  const title = over.title ?? 'MBDTF';
  return {
    key: `${artist.toLowerCase()}::${title.toLowerCase()}`,
    folderKeys: [],
    nameKey: `${artist.toLowerCase()}::${title.toLowerCase()}`,
    title,
    artist,
    year: null,
    trackCount: 0,
    durationSeconds: 0,
    byteSize: 0,
    genre: null,
    label: null,
    catalogNumber: null,
    upc: null,
    releaseDate: null,
    musicBrainzReleaseId: null,
    coverSongId: null,
    addedAtUtc: null,
    playCount: 0,
    trackIds: [],
    ...over
  };
}

describe('songAddedTime', () => {
  it('prefers the immutable acquisition stamp over pipeline timestamps', () => {
    const t = songAddedTime(
      song({
        acquiredAtUtc: '2020-01-01T00:00:00Z',
        indexedAtUtc: '2026-07-01T00:00:00Z',
        libraryBuiltAtUtc: '2026-07-02T00:00:00Z'
      })
    );

    expect(t).toBe(Date.parse('2020-01-01T00:00:00Z'));
  });

  it('falls back to the oldest churn-prone stamp for rows predating the column', () => {
    // A re-tag or re-index would have bumped only one of the two; the older one is the better guess.
    const t = songAddedTime(
      song({ indexedAtUtc: '2026-07-01T00:00:00Z', libraryBuiltAtUtc: '2022-05-05T00:00:00Z' })
    );

    expect(t).toBe(Date.parse('2022-05-05T00:00:00Z'));
  });

  it('prefers an earlier Spotify save date over the download date', () => {
    // A years-old liked song the wishlist downloader only got round to fetching today: without this
    // it would sit at the top of "recently added" next to things actually just acquired.
    const t = songAddedTime(
      song({
        acquiredAtUtc: '2026-07-26T15:44:00Z',
        spotifyAddedAtUtc: '2023-03-21T00:00:00Z'
      })
    );

    expect(t).toBe(Date.parse('2023-03-21T00:00:00Z'));
  });

  it('keeps the acquisition date when the Spotify save came later', () => {
    // Ripped in 2019, saved on Spotify in 2024 — the save date must not push it forward.
    const t = songAddedTime(
      song({
        acquiredAtUtc: '2019-01-01T00:00:00Z',
        spotifyAddedAtUtc: '2024-06-01T00:00:00Z'
      })
    );

    expect(t).toBe(Date.parse('2019-01-01T00:00:00Z'));
  });

  it('uses the Spotify save date for rows with no acquisition stamp at all', () => {
    const t = songAddedTime(song({ spotifyAddedAtUtc: '2023-03-21T00:00:00Z' }));

    expect(t).toBe(Date.parse('2023-03-21T00:00:00Z'));
  });
});

describe('songLikedTime', () => {
  it('is 0 for a song that was never liked, whatever its Spotify save date', () => {
    expect(songLikedTime(song({ spotifyAddedAtUtc: '2023-03-21T00:00:00Z' }))).toBe(0);
  });

  it('prefers the Spotify save date over the bulk auto-like sweep stamp', () => {
    // The sweep stamps every song it matches with one shared `now`; without this the whole batch
    // ties and "newest liked first" collapses into the tie-break order.
    const sweptAt = '2026-07-12T21:52:00Z';
    const a = song({ likedAtUtc: sweptAt, spotifyAddedAtUtc: '2022-09-09T00:00:00Z' });
    const b = song({ likedAtUtc: sweptAt, spotifyAddedAtUtc: '2023-08-04T00:00:00Z' });

    expect(songLikedTime(a)).toBe(Date.parse('2022-09-09T00:00:00Z'));
    expect(songLikedTime(b)).toBe(Date.parse('2023-08-04T00:00:00Z'));
    expect(songLikedTime(b)).toBeGreaterThan(songLikedTime(a));
  });

  it('keeps a heart clicked here when it predates the Spotify save', () => {
    const t = songLikedTime(
      song({ likedAtUtc: '2026-01-05T00:00:00Z', spotifyAddedAtUtc: '2026-06-01T00:00:00Z' })
    );

    expect(t).toBe(Date.parse('2026-01-05T00:00:00Z'));
  });

  it('falls back to the local like time when there is no Spotify save date', () => {
    const t = songLikedTime(song({ likedAtUtc: '2026-01-05T00:00:00Z' }));

    expect(t).toBe(Date.parse('2026-01-05T00:00:00Z'));
  });
});

describe('sortAlbums', () => {
  // Cards as the API sends them — the grid orders what it is given, it no longer builds it.
  const albums = hydrated([
    card({
      artist: 'Zappa',
      title: 'Hot Rats',
      year: 1969,
      playCount: 10,
      addedAtUtc: '2020-01-01T00:00:00Z'
    }),
    card({
      artist: 'Aphex Twin',
      title: 'Drukqs',
      year: 2001,
      playCount: 1,
      addedAtUtc: '2026-01-01T00:00:00Z'
    })
  ]);
  const titles = (key: Parameters<typeof sortAlbums>[1]) =>
    sortAlbums(albums, key).map((a) => a.title);

  it('orders by each key', () => {
    expect(titles('recent')).toEqual(['Drukqs', 'Hot Rats']);
    expect(titles('artist')).toEqual(['Drukqs', 'Hot Rats']);
    expect(titles('title')).toEqual(['Drukqs', 'Hot Rats']);
    expect(titles('year')).toEqual(['Drukqs', 'Hot Rats']);
    expect(titles('played')).toEqual(['Hot Rats', 'Drukqs']);
  });

  it('breaks ties alphabetically instead of leaving the order it was handed', () => {
    const tied = hydrated([
      card({ artist: 'B', title: 'B album' }),
      card({ artist: 'A', title: 'A album' })
    ]);

    // No play counts, no added dates — every comparator ties, so artist order decides.
    expect(sortAlbums(tied, 'played').map((a) => a.artist)).toEqual(['A', 'B']);
    expect(sortAlbums(tied, 'recent').map((a) => a.artist)).toEqual(['A', 'B']);
  });
});

describe('hydrateAlbums', () => {
  it('resolves track ids to the very song objects the store holds', () => {
    // Identity, not equality: the store mutates a row in place on a heart tap, and the album views
    // only see that because they hold the same object. A copy here would break every overlay.
    const one = song({ id: 1, title: 'Gorgeous' });
    const two = song({ id: 2, title: 'Power' });
    const byId = new Map([one, two].map((s) => [s.id, s]));

    const album = hydrateAlbums([card({ trackIds: [2, 1] })], byId)[0];

    expect(album.songs).toHaveLength(2);
    expect(album.songs[0]).toBe(two);
    expect(album.songs[1]).toBe(one);
  });

  it('drops a track the songs list does not have rather than emitting a hole', () => {
    // The two fetches can straddle a library change; the next refresh reconciles.
    const byId = new Map([[1, song({ id: 1 })]]);

    expect(hydrateAlbums([card({ trackIds: [1, 99] })], byId)[0].songs.map((s) => s.id)).toEqual([1]);
  });

  it('turns the cover track id into a cover URL, and no artwork into null', () => {
    expect(hydrateAlbums([card({ coverSongId: 7 })], new Map())[0].coverUrl).toContain('7');
    expect(hydrateAlbums([card()], new Map())[0].coverUrl).toBeNull();
  });
});

function hydrated(cards: AlbumSummaryDto[]): AlbumSummary[] {
  return hydrateAlbums(cards, new Map());
}
