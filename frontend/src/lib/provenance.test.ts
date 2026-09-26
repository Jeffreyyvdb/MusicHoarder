import { describe, expect, it } from 'vitest';
import type { ProvenanceGroup, ProvenanceSeed } from './api-client';
import { seedSublabel, visibleProvenanceGroups } from './provenance';

const seed = (over: Partial<ProvenanceSeed> = {}): ProvenanceSeed => ({
  songId: 2,
  title: 'Ni Juana la Cubana',
  album: '20 Éxitos Bailables',
  reason: 'SpotifyPlaylist',
  label: 'From Spotify playlist “Cumbia Party”',
  atLabel: 'Added to the playlist',
  isDeleted: false,
  ...over
});

const group = (over: Partial<ProvenanceGroup>): ProvenanceGroup => ({
  reason: 'SpotifyPlaylist',
  label: 'From Spotify playlist “Cumbia Party”',
  explanation: '',
  tracks: [],
  ...over
});

describe('seedSublabel', () => {
  it('is just the reason when the seed sits on the album being filled in', () => {
    expect(seedSublabel(seed(), '20 éxitos bailables')).toBe(
      'From Spotify playlist “Cumbia Party”'
    );
  });

  it('names another album, and says when the seed is gone', () => {
    expect(
      seedSublabel(seed({ album: 'Lloré Lloré', isDeleted: true }), '20 Éxitos Bailables')
    ).toBe('From Spotify playlist “Cumbia Party” · on “Lloré Lloré” · since deleted');
  });
});

describe('visibleProvenanceGroups', () => {
  const fill = group({
    reason: 'AlbumFill',
    label: 'Filled in by album completion',
    tracks: [{ songId: 1, title: 'Fiesta', atLabel: 'Arrived' }],
    fill: { seeds: [seed()], seedCount: 1 }
  });

  it('drops a group that only repeats a fill’s seeds', () => {
    const playlist = group({ tracks: [{ songId: 2, title: 'Ni Juana la Cubana', atLabel: 'x' }] });
    expect(visibleProvenanceGroups([playlist, fill])).toEqual([fill]);
  });

  it('keeps a group with any track that is not a seed', () => {
    const playlist = group({
      tracks: [
        { songId: 2, title: 'Ni Juana la Cubana', atLabel: 'x' },
        { songId: 3, title: 'Esto Se Acabó', atLabel: 'x' }
      ]
    });
    expect(visibleProvenanceGroups([playlist, fill])).toEqual([playlist, fill]);
  });

  it('keeps everything when nothing was filled in', () => {
    const local = group({
      reason: 'LocalFile',
      tracks: [{ songId: 9, title: 'A', atLabel: 'Found' }]
    });
    expect(visibleProvenanceGroups([local])).toEqual([local]);
  });
});
