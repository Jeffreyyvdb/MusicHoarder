import { describe, expect, it } from 'vitest';
import type { AcquireReleasesResult, ArtistRelease } from './api-client';
import {
  acquirableReleases,
  acquireSummary,
  availableFilters,
  catalogArtistCaption,
  discographySummary,
  filterReleases,
  followSummary,
  popularSongs,
  releaseCaption,
  releaseState,
  releaseStatusLabel,
  toggleReleaseKind
} from './artist-discography';

function release(overrides: Partial<ArtistRelease> = {}): ArtistRelease {
  return {
    key: 'album:x',
    kind: 'album',
    title: 'X',
    releaseDate: '2024-03-01',
    coverUrl: null,
    explicit: false,
    editionIds: ['1'],
    upcoming: false,
    ownedTracks: 0,
    totalTracks: null,
    libraryAlbum: null,
    queuedTracks: 0,
    downloadedTracks: 0,
    failedTracks: 0,
    ...overrides
  };
}

describe('releaseState', () => {
  it('is missing when you have none of it', () => {
    expect(releaseState(release())).toBe('missing');
  });

  it('is complete when every track of the tracklist is owned', () => {
    expect(releaseState(release({ ownedTracks: 12, totalTracks: 12 }))).toBe('complete');
  });

  it('is partial when some of a known tracklist is owned', () => {
    expect(releaseState(release({ ownedTracks: 3, totalTracks: 12 }))).toBe('partial');
  });

  it('counts an owned release with no known tracklist as complete', () => {
    expect(releaseState(release({ ownedTracks: 3, totalTracks: null }))).toBe('complete');
  });

  it('is queued while tracks are on their way, and upcoming before release', () => {
    expect(releaseState(release({ ownedTracks: 3, totalTracks: 12, queuedTracks: 9 }))).toBe(
      'queued'
    );
    expect(releaseState(release({ upcoming: true }))).toBe('upcoming');
  });
});

describe('labels', () => {
  it('writes the caption as year · kind', () => {
    expect(releaseCaption(release({ kind: 'ep' }))).toBe('2024 · EP');
    expect(releaseCaption(release({ releaseDate: null, kind: 'single' }))).toBe('Single');
  });

  it('says what you have of a release', () => {
    expect(releaseStatusLabel(release({ ownedTracks: 3, totalTracks: 12 }))).toBe('3 of 12 tracks');
    expect(releaseStatusLabel(release({ ownedTracks: 12, totalTracks: 12 }))).toBe(
      'In your library'
    );
    expect(releaseStatusLabel(release({ queuedTracks: 1 }))).toBe('Downloading 1 track');
    expect(releaseStatusLabel(release())).toBeNull();
    expect(releaseStatusLabel(release({ failedTracks: 2 }))).toBe('2 not found');
  });

  it('summarizes the discography, leaving out what is not out yet', () => {
    expect(
      discographySummary([release({ ownedTracks: 1 }), release(), release({ upcoming: true })])
    ).toBe('2 releases · 1 in your library');
  });
});

describe('filters', () => {
  const all = [
    release({ key: 'a', kind: 'album' }),
    release({ key: 's', kind: 'single', ownedTracks: 1 }),
    release({ key: 'e', kind: 'ep' })
  ];

  it('groups singles and EPs together, like Spotify', () => {
    expect(filterReleases(all, 'singles').map((r) => r.key)).toEqual(['s', 'e']);
    expect(filterReleases(all, 'albums').map((r) => r.key)).toEqual(['a']);
  });

  it('offers only the chips that have releases', () => {
    expect(availableFilters(all)).toEqual(['all', 'albums', 'singles']);
  });

  it('can hide what you already have', () => {
    expect(filterReleases(all, 'all', true).map((r) => r.key)).toEqual(['a', 'e']);
  });

  it('gets only what is missing and not already on its way', () => {
    const list = [
      release({ key: 'missing' }),
      release({ key: 'partial', ownedTracks: 1, totalTracks: 5 }),
      release({ key: 'queued', queuedTracks: 3 }),
      release({ key: 'have', ownedTracks: 5, totalTracks: 5 }),
      release({ key: 'soon', upcoming: true })
    ];
    expect(acquirableReleases(list).map((r) => r.key)).toEqual(['missing', 'partial']);
  });
});

describe('acquireSummary', () => {
  const result = (overrides: Partial<AcquireReleasesResult>): AcquireReleasesResult => ({
    queued: 0,
    capped: 0,
    skippedReleases: 0,
    jobStarted: true,
    results: [],
    ...overrides
  });

  it('says what was queued and what was skipped', () => {
    expect(
      acquireSummary(
        result({
          queued: 9,
          results: [
            {
              key: 'a',
              title: 'A',
              queued: 9,
              requeued: 0,
              alreadyOwned: 3,
              alreadyQueued: 0,
              capped: 0,
              error: null
            }
          ]
        })
      )
    ).toBe('Queued 9 tracks · 3 already in your library');
  });

  it('explains a zero', () => {
    expect(
      acquireSummary(
        result({
          results: [
            {
              key: 'a',
              title: 'A',
              queued: 0,
              requeued: 0,
              alreadyOwned: 4,
              alreadyQueued: 0,
              capped: 0,
              error: null
            }
          ]
        })
      )
    ).toBe('Nothing new to download');
  });

  it('mentions the request limit and unreadable releases', () => {
    expect(
      acquireSummary(
        result({
          queued: 500,
          capped: 20,
          results: [
            {
              key: 'a',
              title: 'A',
              queued: 500,
              requeued: 0,
              alreadyOwned: 0,
              alreadyQueued: 0,
              capped: 20,
              error: null
            },
            {
              key: 'b',
              title: null,
              queued: 0,
              requeued: 0,
              alreadyOwned: 0,
              alreadyQueued: 0,
              capped: 0,
              error: 'x'
            }
          ]
        })
      )
    ).toBe(
      'Queued 500 tracks · 20 left for another time (request limit) · 1 release could not be read'
    );
  });
});

describe('followSummary', () => {
  it('lists the kinds in a fixed order', () => {
    expect(followSummary(['ep', 'album'], true)).toBe('Albums and EPs · downloads automatically');
    expect(followSummary(['single', 'album', 'ep'], false)).toBe(
      'Albums, EPs and Singles · notify only'
    );
  });
});

describe('popularSongs', () => {
  it('ranks by your plays, then likes, then recency', () => {
    const stats: Record<string, { plays: number; lastPlayed: number; liked: boolean }> = {
      a: { plays: 1, lastPlayed: 0, liked: false },
      b: { plays: 9, lastPlayed: 0, liked: false },
      c: { plays: 1, lastPlayed: 0, liked: true },
      d: { plays: 0, lastPlayed: 5, liked: false }
    };
    expect(popularSongs(['a', 'b', 'c', 'd'], (s) => stats[s], 3)).toEqual(['b', 'c', 'a']);
  });
});

describe('shared follow helpers', () => {
  it('switches a kind on or off, and refuses to leave none', () => {
    expect(toggleReleaseKind(['album'], 'ep', true)).toEqual(['album', 'ep']);
    expect(toggleReleaseKind(['album', 'ep'], 'ep', false)).toEqual(['album']);
    expect(toggleReleaseKind(['album'], 'album', false)).toBeNull();
  });

  it('writes a catalog artist the same way everywhere', () => {
    expect(catalogArtistCaption({ fans: 5_237_894, albumCount: 39 })).toBe(
      '5.2M fans · 39 releases'
    );
    expect(catalogArtistCaption({ fans: 120, albumCount: 0 })).toBe('120 fans');
  });
});
