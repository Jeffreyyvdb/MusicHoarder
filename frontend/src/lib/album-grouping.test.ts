import { describe, expect, it } from 'vitest';
import {
  buildAlbumsFromSongs,
  mergeAlbumsByName,
  songAddedTime,
  songLikedTime,
  sortAlbums,
  type ApiSong
} from './api-client';

// Minimal ApiSong factory — only the fields buildAlbumsFromSongs reads.
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

describe('buildAlbumsFromSongs', () => {
  it('splits one album name across destination folders (mirrors the player)', () => {
    // Same artist + album name, but two different release folders (Navidrome shows two albums).
    const songs = [
      song({ id: 1, trackNumber: 1, destinationPath: '/dest/Kanye West/2010 - MBDTF/01 - Dark Fantasy.flac' }),
      song({ id: 2, trackNumber: 2, destinationPath: '/dest/Kanye West/2010 - MBDTF/02 - Gorgeous.flac' }),
      song({ id: 3, trackNumber: 1, destinationPath: "/dest/Kanye West/2013 - MBDTF/01 - Mama's Boy.flac" })
    ];

    const albums = buildAlbumsFromSongs(songs);

    expect(albums).toHaveLength(2);
    const counts = albums.map((a) => a.trackCount).sort();
    expect(counts).toEqual([1, 2]);
    // Keys are the destination folder directories, not the (shared) album name.
    expect(new Set(albums.map((a) => a.key)).size).toBe(2);
  });

  it('keeps a multi-disc album (same folder) as one card', () => {
    const songs = [
      song({ id: 1, trackNumber: 1, destinationPath: '/dest/A/2000 - X/1-01 - a.flac' }),
      song({ id: 2, trackNumber: 1, destinationPath: '/dest/A/2000 - X/2-01 - b.flac' })
    ];

    const albums = buildAlbumsFromSongs(songs);

    expect(albums).toHaveLength(1);
    expect(albums[0].trackCount).toBe(2);
  });

  it('falls back to artist::album name grouping when songs are not built', () => {
    const songs = [
      song({ id: 1, destinationPath: null }),
      song({ id: 2, destinationPath: undefined })
    ];

    const albums = buildAlbumsFromSongs(songs);

    expect(albums).toHaveLength(1);
    expect(albums[0].key).toBe('kanye west::my beautiful dark twisted fantasy');
  });
});

describe('mergeAlbumsByName', () => {
  const split = [
    song({ id: 1, trackNumber: 2, destinationPath: '/dest/Kanye West/2010 - MBDTF/02 - Gorgeous.flac' }),
    song({ id: 2, trackNumber: 1, destinationPath: '/dest/Kanye West/2010 - MBDTF/01 - Dark Fantasy.flac' }),
    song({ id: 3, trackNumber: 1, destinationPath: "/dest/Kanye West/2013 - MBDTF/01 - Mama's Boy.flac" })
  ];

  it('folds one album name split across destination folders into a single card', () => {
    const merged = mergeAlbumsByName(buildAlbumsFromSongs(split));

    expect(merged).toHaveLength(1);
    expect(merged[0].trackCount).toBe(3);
    // The biggest folder wins the representative key, so existing ?album= links still resolve.
    expect(merged[0].key).toBe('/dest/Kanye West/2010 - MBDTF');
    // ...and the folder that lost is still resolvable through folderKeys.
    expect(merged[0].folderKeys).toEqual([
      '/dest/Kanye West/2010 - MBDTF',
      '/dest/Kanye West/2013 - MBDTF'
    ]);
    expect(merged[0].songs.map((s) => s.id)).toEqual([2, 3, 1]); // re-sorted by track number
  });

  it('leaves distinct albums alone', () => {
    const albums = buildAlbumsFromSongs([
      song({ id: 1, album: 'Graduation', destinationPath: '/dest/Kanye West/2007 - Graduation/01.flac' }),
      song({ id: 2, album: '808s', destinationPath: '/dest/Kanye West/2008 - 808s/01.flac' })
    ]);

    expect(mergeAlbumsByName(albums)).toHaveLength(2);
  });
});

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
  const albums = buildAlbumsFromSongs([
    song({
      id: 1,
      artist: 'Zappa',
      albumArtist: 'Zappa',
      album: 'Hot Rats',
      year: 1969,
      playCount: 10,
      acquiredAtUtc: '2020-01-01T00:00:00Z',
      destinationPath: '/dest/Zappa/1969 - Hot Rats/01.flac'
    }),
    song({
      id: 2,
      artist: 'Aphex Twin',
      albumArtist: 'Aphex Twin',
      album: 'Drukqs',
      year: 2001,
      playCount: 1,
      acquiredAtUtc: '2026-01-01T00:00:00Z',
      destinationPath: '/dest/Aphex Twin/2001 - Drukqs/01.flac'
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

  it('breaks ties alphabetically instead of leaving grouping order', () => {
    const tied = buildAlbumsFromSongs([
      song({ id: 1, artist: 'B', albumArtist: 'B', album: 'B album', destinationPath: '/dest/b/01.flac' }),
      song({ id: 2, artist: 'A', albumArtist: 'A', album: 'A album', destinationPath: '/dest/a/01.flac' })
    ]);

    // No play counts, no acquisition stamps — every comparator ties, so artist order decides.
    expect(sortAlbums(tied, 'played').map((a) => a.artist)).toEqual(['A', 'B']);
    expect(sortAlbums(tied, 'recent').map((a) => a.artist)).toEqual(['A', 'B']);
  });
});
