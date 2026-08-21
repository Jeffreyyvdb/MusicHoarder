import { describe, expect, it } from 'vitest';
import { flushSync } from 'svelte';
import {
  createTrackListView,
  parseChips,
  serializeChips,
  type ChipKey,
  type SortKey
} from './track-list-view.svelte';
import type { ApiSong } from './api-client';

/**
 * The filter/sort state used to live inside TrackList.svelte, where none of this was reachable from
 * a test. Two behaviours are worth pinning:
 *
 *  • the coupling between the Spotify Liked chip and the sort key — it is the one filter that
 *    reorders the list, and turning it off has to restore the list's own default rather than strand
 *    the user on a sort key no column header can reach;
 *  • `countFor`, which is what makes plain AND safe to put in front of a user. Every chip advertises
 *    what pressing it would leave, so a combination with no overlap reads 0 *before* the click.
 */
function song(id: number, over: Partial<ApiSong> = {}): ApiSong {
  return {
    id,
    fileName: `track-${id}.flac`,
    title: `Track ${id}`,
    artist: 'Artist',
    album: 'Album',
    durationSeconds: 100,
    fileSizeBytes: 1000,
    ...over
  } as ApiSong;
}

const spotifySong = (id: number) => song(id, { spotifyLikedAtUtc: '2025-01-01T00:00:00Z' });
const localSong = (id: number) => song(id, { originKind: 'Scanned' });
const addedSong = (id: number) => song(id, { originKind: 'Downloaded', originSource: 'DirectUrl' });

/**
 * Chips are owned by the caller (in the app, by the URL), so the harness holds them and feeds them
 * back — exactly the loop LibraryV2 closes through `?f=`.
 */
function view(songs: ApiSong[], initialSortKey?: SortKey) {
  let chips = $state<ChipKey[]>([]);
  return createTrackListView({
    songs: () => songs,
    searchQuery: () => '',
    chips: () => chips,
    onChipsChange: (next) => {
      chips = next;
    },
    ...(initialSortKey ? { initialSortKey } : {})
  });
}

describe('chip serialisation', () => {
  it('round-trips through the ?f= param', () => {
    expect(parseChips(serializeChips(['lyrics', 'local']))).toEqual(['local', 'lyrics']);
  });

  // An old link, or a chip dropped in a later version, must narrow less rather than error out.
  it('drops unknown keys instead of failing', () => {
    expect(parseChips('local,not-a-chip,lyrics')).toEqual(['local', 'lyrics']);
    expect(parseChips(null)).toEqual([]);
    expect(parseChips('')).toEqual([]);
  });

  // Two URLs selecting the same chips must be the same string, or the back button sees false steps.
  it('normalises order and duplicates', () => {
    expect(parseChips('lyrics,local,lyrics')).toEqual(['local', 'lyrics']);
    expect(serializeChips(['lyrics', 'local'])).toBe(serializeChips(['local', 'lyrics']));
  });
});

describe('createTrackListView', () => {
  it('sorts by the Spotify save date while the Spotify Liked chip is on', () => {
    const cleanup = $effect.root(() => {
      const v = view([song(1), spotifySong(2)]);
      expect(v.sortKey).toBe('added');

      v.toggleChip('spotify-liked');
      flushSync();
      expect(v.isChipActive('spotify-liked')).toBe(true);
      expect(v.sortKey).toBe('spotify');
      expect(v.sortDir).toBe('desc');
      expect(v.sorted.map((s) => s.id)).toEqual([2]);
    });
    cleanup();
  });

  it('restores the list default sort when the Spotify Liked chip is turned off', () => {
    const cleanup = $effect.root(() => {
      const v = view([song(1), spotifySong(2)], 'liked');
      v.toggleChip('spotify-liked');
      flushSync();
      v.toggleChip('spotify-liked');
      flushSync();
      expect(v.isChipActive('spotify-liked')).toBe(false);
      expect(v.sortKey).toBe('liked');
    });
    cleanup();
  });

  // Pressing an unrelated chip must not disturb a sort the user chose.
  it('leaves the sort alone for chips other than Spotify Liked', () => {
    const cleanup = $effect.root(() => {
      const v = view([song(1), localSong(2)]);
      v.toggleSort('title');
      flushSync();
      v.toggleChip('local');
      flushSync();
      expect(v.sortKey).toBe('title');
      expect(v.sortDir).toBe('asc');
    });
    cleanup();
  });

  it('requires every active chip to match', () => {
    const cleanup = $effect.root(() => {
      const both = song(3, { originKind: 'Scanned', likedAtUtc: '2026-01-01T00:00:00Z' });
      const v = view([localSong(1), song(2, { likedAtUtc: '2026-01-01T00:00:00Z' }), both]);

      v.toggleChip('local');
      flushSync();
      expect(v.sorted.map((s) => s.id).sort()).toEqual([1, 3]);

      v.toggleChip('mh-liked');
      flushSync();
      expect(v.sorted.map((s) => s.id)).toEqual([3]);
    });
    cleanup();
  });

  it('advertises what pressing a chip would leave, including a dead end', () => {
    const cleanup = $effect.root(() => {
      const v = view([localSong(1), localSong(2), addedSong(3)]);
      expect(v.countFor('local')).toBe(2);
      expect(v.countFor('added')).toBe(1);

      v.toggleChip('local');
      flushSync();
      // Scanned and downloaded are exclusive, so this pair can never overlap. The 0 has to be
      // visible before the click, which is the whole reason the count excludes the chip itself.
      expect(v.countFor('added')).toBe(0);
      // An active chip reports the current result count.
      expect(v.countFor('local')).toBe(2);
    });
    cleanup();
  });

  it('clears every chip and restores the sort in one call', () => {
    const cleanup = $effect.root(() => {
      const v = view([song(1), spotifySong(2)], 'liked');
      v.toggleChip('lyrics');
      flushSync();
      v.toggleChip('spotify-liked');
      flushSync();
      expect(v.hasFilters).toBe(true);

      v.clearFilters();
      flushSync();
      expect(v.hasFilters).toBe(false);
      expect(v.chips).toEqual([]);
      expect(v.sortKey).toBe('liked');
    });
    cleanup();
  });

  it('toggles direction on a repeat sort click and defaults strings ascending', () => {
    const cleanup = $effect.root(() => {
      const v = view([song(1), song(2)]);
      v.toggleSort('title');
      flushSync();
      expect(v.sortKey).toBe('title');
      expect(v.sortDir).toBe('asc');

      v.toggleSort('title');
      flushSync();
      expect(v.sortDir).toBe('desc');

      v.toggleSort('size');
      flushSync();
      expect(v.sortDir).toBe('desc');
    });
    cleanup();
  });

  it('reports stats over the filtered set, not the whole list', () => {
    const cleanup = $effect.root(() => {
      const v = view([song(1), spotifySong(2)]);
      expect(v.stats.count).toBe(2);
      expect(v.stats.totalBytes).toBe(2000);

      v.toggleChip('spotify-liked');
      flushSync();
      expect(v.stats.count).toBe(1);
      expect(v.stats.totalSec).toBe(100);
    });
    cleanup();
  });
});
