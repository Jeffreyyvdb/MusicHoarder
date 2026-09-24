import { describe, expect, it } from 'vitest';
import { flushSync } from 'svelte';
import {
  CHIP_KEYS,
  CHIP_LABELS,
  countSummary,
  createTrackListView,
  FRIEND_CHIP_KEYS,
  isTrackListSong,
  parseChips,
  serializeChips,
  SORT_KEYS,
  SORT_MENU_LABELS,
  tapActionFor,
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

describe('the sort menu', () => {
  // A phone has no column headers, so the menu is the only way to every sort — including the two
  // no header can reach: Date liked, and Date added once another sort has been chosen.
  it('offers every sort key exactly once, each with a sentence-case label', () => {
    const all: SortKey[] = [
      'added',
      'liked',
      'spotify',
      'title',
      'artist',
      'album',
      'year',
      'size',
      'match',
      'dur'
    ];
    expect([...SORT_KEYS].sort()).toEqual([...all].sort());
    expect(new Set(SORT_KEYS).size).toBe(SORT_KEYS.length);
    for (const k of SORT_KEYS) expect(SORT_MENU_LABELS[k]).toMatch(/^[A-Z][a-z ]+$/);
  });

  it('keeps the direction when the checked key is picked again, and resets it for a new key', () => {
    const cleanup = $effect.root(() => {
      const v = view([song(1), song(2)]);
      v.setSortDir('asc');
      flushSync();
      v.setSortKey('added');
      flushSync();
      expect(v.sortKey).toBe('added');
      expect(v.sortDir).toBe('asc');

      v.setSortKey('artist');
      flushSync();
      expect(v.sortDir).toBe('asc');
      v.setSortKey('liked');
      flushSync();
      expect(v.sortKey).toBe('liked');
      expect(v.sortDir).toBe('desc');
    });
    cleanup();
  });

  // Once another sort is chosen, the default must still be reachable: the old header-only UI lost it.
  it('can return to Date added after another sort was chosen', () => {
    const cleanup = $effect.root(() => {
      const v = view([song(1), song(2)]);
      v.toggleSort('title');
      flushSync();
      v.setSortKey('added');
      flushSync();
      expect(v.sortKey).toBe('added');
      expect(v.sortDir).toBe('desc');
    });
    cleanup();
  });
});

describe('chip vocabulary', () => {
  it('labels every chip, without the app name', () => {
    for (const k of CHIP_KEYS) {
      expect(CHIP_LABELS[k]).toBeTruthy();
      expect(CHIP_LABELS[k]).not.toMatch(/MusicHoarder/);
    }
    expect(CHIP_LABELS['mh-liked']).toBe('Favourites');
    expect(CHIP_LABELS['spotify-liked']).toBe('Spotify liked');
  });

  it('gives accounts that are not the admin only the chips their dataset can answer', () => {
    expect(FRIEND_CHIP_KEYS).toEqual(['mh-liked', 'video', 'lyrics']);
    for (const k of FRIEND_CHIP_KEYS) expect(CHIP_KEYS).toContain(k);
  });
});

describe('isTrackListSong', () => {
  const built = { isBuilt: true };
  it('covers built songs you asked for', () => {
    expect(isTrackListSong(song(1, built))).toBe(true);
  });
  it('covers your own source files still waiting on review, built or not', () => {
    expect(
      isTrackListSong(song(1, { isBuilt: false, originKind: 'Scanned', enrichmentStatus: 2 }))
    ).toBe(true);
    expect(
      isTrackListSong(song(1, { isBuilt: false, originKind: 'Scanned', enrichmentStatus: 3 }))
    ).toBe(false);
    expect(
      isTrackListSong(song(1, { isBuilt: false, originKind: 'Downloaded', enrichmentStatus: 2 }))
    ).toBe(false);
  });
  it('leaves album completion out until a like promotes it', () => {
    expect(isTrackListSong(song(1, { ...built, isAlbumFill: true }))).toBe(false);
    expect(
      isTrackListSong(song(1, { ...built, isAlbumFill: true, likedAtUtc: '2026-01-01T00:00:00Z' }))
    ).toBe(true);
  });
});

describe('tapActionFor', () => {
  // A tap on the song you are listening to must never pause or restart it.
  it('opens Now Playing for the loaded row and plays any other', () => {
    expect(tapActionFor(4, 4)).toBe('open');
    expect(tapActionFor(4, 5)).toBe('play');
    expect(tapActionFor(4, null)).toBe('play');
    expect(tapActionFor(4, undefined)).toBe('play');
  });
});

describe('countSummary', () => {
  it('reads "N things" unfiltered and "N of M" once something narrows it', () => {
    expect(countSummary(64, 64, 'track')).toBe('64 tracks');
    expect(countSummary(1, 1, 'track')).toBe('1 track');
    expect(countSummary(12, 64, 'track')).toBe('12 of 64');
    expect(countSummary(3622, 3622, 'track')).toBe(`${(3622).toLocaleString()} tracks`);
  });
});
