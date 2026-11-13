import { describe, expect, it } from 'vitest';
import {
  hydrateAlbums,
  seededOrder,
  sortAlbumsByLastPlayed,
  sortSongsByRecentlyLiked,
  type AlbumSummary,
  type AlbumSummaryDto,
  type ApiSong
} from './api-client';

/*
 * LibraryOverviewV2's shelves used to call their key function from inside the sort comparator —
 * hashing a key or parsing a date on every one of a comparator's ~n·log n calls, rather than once
 * per item. Each test here pins the decorate-once replacement against the naive comparator it
 * replaced, on a fixture with ties and missing dates, so the shelves cannot silently reorder.
 */

function song(over: Partial<ApiSong>): ApiSong {
  return { id: 0, fileName: 'track.flac', artist: 'Artist', album: 'Album', ...over } as ApiSong;
}

/** Minimal album card as the API sends it — see album-grouping.test.ts for the fuller version. */
function card(over: Partial<AlbumSummaryDto> = {}): AlbumSummaryDto {
  const artist = over.artist ?? 'Artist';
  const title = over.title ?? 'Album';
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

/** An album card hydrated against real song rows, so `.songs` (and lastPlayedAtUtc) is populated. */
function albumWithSongs(title: string, lastPlayedTimes: (string | null)[]): AlbumSummary {
  const songs = lastPlayedTimes.map((t, i) => song({ id: i, lastPlayedAtUtc: t }));
  const byId = new Map(songs.map((s) => [s.id, s]));
  const dto = card({ title, trackIds: songs.map((s) => s.id) });
  return hydrateAlbums([dto], byId)[0];
}

describe('seededOrder', () => {
  const hash = (s: string): number => {
    let h = 5381;
    for (let i = 0; i < s.length; i++) h = ((h << 5) + h + s.charCodeAt(i)) >>> 0;
    return h;
  };

  it('matches hashing the key from inside the comparator, the slow way this replaced', () => {
    const items = ['zeta', 'alpha', 'kappa', 'beta', 'gamma', 'delta', 'epsilon', 'alpha-2'];
    const seed = 'mount-seed-1';
    const old = [...items].sort((a, b) => hash(a + seed) - hash(b + seed));

    expect(seededOrder(items, (s) => s, seed)).toEqual(old);
  });

  it('keeps the handed-in order for items that hash equal under the same seed', () => {
    // Array.prototype.sort is stable: a tie in the hash must not reorder the items.
    const items = ['same-key', 'same-key', 'same-key'];
    expect(seededOrder(items, (s) => s, 'seed')).toEqual(items);
  });

  it('is stable across calls with the same items and seed (what keeps a shelf put across a live refresh)', () => {
    const items = ['one', 'two', 'three', 'four', 'five'];
    const first = seededOrder(items, (s) => s, 'seed-a');
    const second = seededOrder(items, (s) => s, 'seed-a');
    expect(second).toEqual(first);
  });

  it('a different seed can reorder the same items (what makes a shelf differ per visit)', () => {
    // Enough items, and seeds different enough (not just a trailing character apart — the hash
    // shifts every item's key by the same amount then, which preserves relative order) that two
    // seeds landing on the exact same permutation by chance is astronomically unlikely.
    const items = Array.from({ length: 40 }, (_, i) => `item-${i}`);
    const a = seededOrder(items, (s) => s, 'p4x7-mount-seed');
    const b = seededOrder(items, (s) => s, '9qz2-another-seed');
    expect(b).not.toEqual(a);
  });
});

describe('sortAlbumsByLastPlayed', () => {
  it('matches calling the key function from inside the comparator, on a mix of ties and never-played albums', () => {
    const albums = [
      albumWithSongs('Never played', [null, null]),
      albumWithSongs('Played once', ['2024-01-01T00:00:00Z']),
      albumWithSongs('Played twice, most recent wins', [
        '2023-01-01T00:00:00Z',
        '2026-06-01T00:00:00Z'
      ]),
      albumWithSongs('Tied with "Played once"', ['2024-01-01T00:00:00Z']),
      albumWithSongs('Missing timestamp only', [null])
    ];

    function albumLastPlayedOld(a: AlbumSummary): number {
      let max = 0;
      for (const s of a.songs) {
        const t = s.lastPlayedAtUtc ? Date.parse(s.lastPlayedAtUtc) : 0;
        if (t > max) max = t;
      }
      return max;
    }
    const old = albums
      .filter((a) => albumLastPlayedOld(a) > 0)
      .sort((a, b) => albumLastPlayedOld(b) - albumLastPlayedOld(a));

    expect(sortAlbumsByLastPlayed(albums)).toEqual(old);
    expect(sortAlbumsByLastPlayed(albums).map((a) => a.title)).toEqual([
      'Played twice, most recent wins',
      'Played once',
      'Tied with "Played once"'
    ]);
  });
});

describe('sortSongsByRecentlyLiked', () => {
  it('matches parsing likedAtUtc from inside the comparator, ties included', () => {
    const songs = [
      song({ id: 1, likedAtUtc: '2024-01-01T00:00:00Z' }),
      song({ id: 2, likedAtUtc: '2026-01-01T00:00:00Z' }),
      song({ id: 3, likedAtUtc: '2024-01-01T00:00:00Z' }), // ties with id 1
      song({ id: 4, likedAtUtc: '2025-06-15T00:00:00Z' })
    ];
    const old = [...songs].sort(
      (a, b) => Date.parse(b.likedAtUtc ?? '') - Date.parse(a.likedAtUtc ?? '')
    );

    expect(sortSongsByRecentlyLiked(songs)).toEqual(old);
    expect(sortSongsByRecentlyLiked(songs).map((s) => s.id)).toEqual([2, 4, 1, 3]);
  });
});
