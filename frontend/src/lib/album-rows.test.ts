import { describe, expect, it } from 'vitest';
import { buildDisplayRows } from './album-rows';
import type { AlbumTracklist, ApiSong, CanonicalTrack } from './api-client';

// Minimal factories — only the fields buildDisplayRows reads.
function song(over: Partial<ApiSong>): ApiSong {
  return { id: 0, fileName: 'track.flac', durationSeconds: 180, ...over } as ApiSong;
}

function track(over: Partial<CanonicalTrack>): CanonicalTrack {
  return {
    discNumber: 1,
    trackNumber: 1,
    title: 'Some Track',
    durationMs: 180000,
    musicBrainzRecordingId: null,
    corroboratingProviders: [],
    corroborationCount: 0,
    isContested: false,
    ownedSongId: null,
    ...over
  };
}

function tracklist(tracks: CanonicalTrack[]): AlbumTracklist {
  return {
    artist: 'Verschiedene Interpreten',
    album: 'New Wave',
    year: 2015,
    coverArtUrl: null,
    resolvedTrackCount: tracks.length,
    trackCountContested: false,
    ownedCount: tracks.filter((t) => t.ownedSongId != null).length,
    totalCount: tracks.length,
    sources: [],
    tracks
  };
}

// The invariant the keyed {#each} in AlbumPage depends on: no two rows share a key. A violation is
// exactly the each_key_duplicate crash we're guarding against.
function expectUniqueKeys(rows: { key: string }[]) {
  const keys = rows.map((r) => r.key);
  expect(new Set(keys).size).toBe(keys.length);
}

describe('buildDisplayRows', () => {
  it('owned-only fallback when there is no tracklist yet', () => {
    const rows = buildDisplayRows([song({ id: 1 }), song({ id: 2 })], null);
    expect(rows.map((r) => r.key)).toEqual(['song:1', 'song:2']);
    expectUniqueKeys(rows);
  });

  it('dedupes a repeated song id in the owned-only fallback', () => {
    // A duplicated song row in album.songs must not produce two `song:1` keys.
    const rows = buildDisplayRows([song({ id: 1 }), song({ id: 1 }), song({ id: 2 })], null);
    expect(rows.map((r) => r.key)).toEqual(['song:1', 'song:2']);
    expectUniqueKeys(rows);
  });

  it('keeps missing-row keys unique when a compilation repeats disc+track (the crash case)', () => {
    // Contested/merged canonical tracklist with two entries at disc 1, track 1 — previously both
    // hashed to `miss:1:1` and crashed Svelte. Keys are now the canonical index, so they differ.
    const rows = buildDisplayRows(
      [],
      tracklist([
        track({ discNumber: 1, trackNumber: 1, title: 'Eddie Sun' }),
        track({ discNumber: 1, trackNumber: 1, title: 'Yellow in the Club' })
      ])
    );
    expect(rows).toHaveLength(2);
    expect(rows.every((r) => r.kind === 'missing')).toBe(true);
    expectUniqueKeys(rows);
  });

  it('keeps keys unique when two canonical tracks match the same owned song', () => {
    // Imperfect matching points two canonical tracks at owned song 5 — previously two `song:5` rows.
    const rows = buildDisplayRows(
      [song({ id: 5 })],
      tracklist([
        track({ trackNumber: 1, ownedSongId: 5 }),
        track({ trackNumber: 2, ownedSongId: 5 })
      ])
    );
    expect(rows).toHaveLength(2);
    expectUniqueKeys(rows);
    // The first claims the owned song; the second falls through to a canonical (missing) slot.
    expect(rows[0]).toMatchObject({ kind: 'owned', key: 'song:5' });
    expect(rows[1].kind).toBe('missing');
  });

  it('appends owned bonus tracks not present in the canonical list, without key collisions', () => {
    const rows = buildDisplayRows(
      [song({ id: 5 }), song({ id: 9 })],
      tracklist([track({ trackNumber: 1, ownedSongId: 5 })])
    );
    expect(rows.map((r) => r.key)).toEqual(['song:5', 'song:9']);
    expectUniqueKeys(rows);
  });

  it('produces unique keys across a messy compilation (owned, missing dupes, bonus)', () => {
    const rows = buildDisplayRows(
      [song({ id: 5 }), song({ id: 6 }), song({ id: 7 })],
      tracklist([
        track({ discNumber: 1, trackNumber: 1, ownedSongId: 5 }),
        track({ discNumber: 1, trackNumber: 1, ownedSongId: 5 }), // dup match → missing slot
        track({ discNumber: 1, trackNumber: 2, ownedSongId: null }), // missing
        track({ discNumber: 1, trackNumber: 2, ownedSongId: null }), // dup disc+track → missing
        track({ discNumber: 1, trackNumber: 3, ownedSongId: 6 })
        // song 7 is a bonus track, appended at the tail
      ])
    );
    expectUniqueKeys(rows);
    // All owned songs still appear exactly once.
    const ownedIds = rows.filter((r) => r.kind === 'owned').map((r) => (r as { song: ApiSong }).song.id);
    expect(ownedIds.sort()).toEqual([5, 6, 7]);
  });
});
