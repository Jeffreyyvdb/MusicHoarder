import {
  albumKeyForSong,
  hasMusicVideo,
  isAddedByLink,
  isLocalFile,
  isSpotifyLiked,
  songAddedTime,
  songLikedTime,
  spotifyAddedTime,
  type ApiSong
} from '$lib/api-client';
import { isAnyUnreleasedSong } from '$lib/release-status';

export type SortKey =
  | 'added'
  | 'liked'
  | 'spotify'
  | 'title'
  | 'artist'
  | 'album'
  | 'year'
  | 'size'
  | 'match'
  | 'dur';

const STRING_KEYS: SortKey[] = ['title', 'artist', 'album'];

export const SORT_LABELS: Record<SortKey, string> = {
  added: 'added',
  liked: 'liked',
  spotify: 'Spotify save date',
  title: 'title',
  artist: 'artist',
  album: 'album',
  year: 'year',
  size: 'size',
  match: 'match',
  dur: 'duration'
};

const UNKNOWN_ARTIST = 'Unknown Artist';

/** Album-artist-preferred, matching buildArtistGroups / the `?artist=` browse filter. */
export function artistOf(s: ApiSong): string {
  return (s.albumArtist ?? s.artist ?? '').trim() || UNKNOWN_ARTIST;
}
export function titleOf(s: ApiSong): string {
  return (s.title ?? s.fileName).trim() || s.fileName;
}
export function hasLyrics(s: ApiSong): boolean {
  return Boolean(s.hasSyncedLyrics || s.hasPlainLyrics || s.lrclibId);
}
/** Stored match confidence, or null when the pipeline never recorded one — never invented. */
export function matchValue(s: ApiSong): number | null {
  if (typeof s.matchConfidence !== 'number') return null;
  return Math.max(0, Math.min(1, s.matchConfidence));
}

/**
 * The filter chips a track list offers.
 *
 * They replaced a set of one-off booleans (`lyricsOnly`, `spotifyOnly`) and the routes /my-music,
 * /liked and the separate "Unreleased" toggle. One keyed vocabulary is what lets the row render
 * itself from a loop, serialise to `?f=`, and report a per-chip count without each chip growing its
 * own field on this module.
 *
 * Two axes are deliberately kept apart even though they read alike: `local` is *where the file came
 * from* (a scan of your source share), while `unreleased` is *what the recording is* (a tracker-
 * confirmed leak, snippet, demo or stem). A local file is very often a released one.
 */
export type ChipKey =
  | 'spotify-liked'
  | 'mh-liked'
  | 'local'
  | 'added'
  | 'video'
  | 'lyrics'
  | 'unreleased';

/** Display order, which is also the order the chip row renders in. */
export const CHIP_KEYS: readonly ChipKey[] = [
  'spotify-liked',
  'mh-liked',
  'local',
  'added',
  'video',
  'lyrics',
  'unreleased'
];

export const CHIP_PREDICATES: Record<ChipKey, (s: ApiSong) => boolean> = {
  'spotify-liked': isSpotifyLiked,
  'mh-liked': (s) => Boolean(s.likedAtUtc),
  local: isLocalFile,
  added: isAddedByLink,
  video: hasMusicVideo,
  lyrics: hasLyrics,
  unreleased: isAnyUnreleasedSong
};

const CHIP_KEY_SET = new Set<string>(CHIP_KEYS);

/** Narrows an unknown string to a chip key — used when reading `?f=` off the URL. */
export function isChipKey(value: string): value is ChipKey {
  return CHIP_KEY_SET.has(value);
}

/**
 * Parse the `?f=` param. Unknown keys are dropped rather than rejected, so an old link (or a chip
 * removed in a later version) degrades to a narrower filter instead of an error page.
 */
export function parseChips(raw: string | null): ChipKey[] {
  if (!raw) return [];
  const seen = new Set<ChipKey>();
  for (const part of raw.split(',')) {
    const key = part.trim();
    if (isChipKey(key)) seen.add(key);
  }
  // Canonical order, so two URLs selecting the same chips serialise identically.
  return CHIP_KEYS.filter((k) => seen.has(k));
}

/** Serialise for `?f=`; empty means the param should be removed entirely. */
export function serializeChips(keys: readonly ChipKey[]): string {
  return CHIP_KEYS.filter((k) => keys.includes(k)).join(',');
}

/** Every active chip must match. See `countFor` for why a dead-end combination is still reachable. */
function applyChips(songs: ApiSong[], keys: readonly ChipKey[]): ApiSong[] {
  let r = songs;
  for (const key of keys) r = r.filter(CHIP_PREDICATES[key]);
  return r;
}

export type TrackListView = ReturnType<typeof createTrackListView>;

/**
 * Filter + sort state for a track list, owned outside the component that renders
 * the rows.
 *
 * The page toolbar and the list are two separate elements of the layout — the
 * toolbar carries the chips and the "X of Y" summary, the list carries the rows
 * and the sortable column headers — but they read and write one set of filters.
 * Keeping that state here (rather than as `$bindable` props) is what lets the
 * toolbar read *derived* values like `sorted.length` and `stats`, and what keeps
 * `toggleSpotifyOnly`'s sort coupling in the same module as `initialSortKey`.
 *
 * Deliberately store-free: this is view state, not playback. Callers own the
 * player side effects (Play / Shuffle), which keeps the module unit-testable
 * without a SvelteKit runtime.
 */
export function createTrackListView(opts: {
  songs: () => ApiSong[];
  searchQuery: () => string;
  /**
   * Active chips. Read-only here on purpose: the URL owns them, so this view derives from `?f=`
   * rather than holding a second copy that could drift from the address bar.
   */
  chips?: () => readonly ChipKey[];
  /** Called with the next chip set when one is pressed. The caller writes it to the URL. */
  onChipsChange?: (next: ChipKey[]) => void;
  initialSortKey?: SortKey;
}) {
  const initialSortKey = opts.initialSortKey ?? 'added';

  let sortKey = $state<SortKey>(initialSortKey);
  let sortDir = $state<'asc' | 'desc'>('desc');

  const songs = $derived(opts.songs());
  const searchQuery = $derived(opts.searchQuery());
  const chips = $derived(opts.chips?.() ?? []);

  /** Search applied, chips not yet — the base every per-chip count is measured against. */
  const searched = $derived.by(() => {
    const q = searchQuery.trim().toLowerCase();
    if (!q) return songs;
    return songs.filter(
      (s) =>
        titleOf(s).toLowerCase().includes(q) ||
        artistOf(s).toLowerCase().includes(q) ||
        (s.album ?? '').toLowerCase().includes(q)
    );
  });

  const filtered = $derived(applyChips(searched, chips));

  const sorted = $derived.by(() => {
    const r = [...filtered];
    // null = "not known for this track" (only Match today); those always sort last.
    const pick = (s: ApiSong): string | number | null => {
      switch (sortKey) {
        case 'title':
          return titleOf(s).toLowerCase();
        case 'artist':
          return artistOf(s).toLowerCase();
        case 'album':
          return (s.album ?? '').toLowerCase();
        case 'year':
          return s.year ?? 0;
        case 'size':
          return s.fileSizeBytes ?? 0;
        case 'dur':
          return s.durationSeconds ?? 0;
        case 'match':
          return matchValue(s);
        case 'liked':
          return songLikedTime(s);
        case 'spotify':
          return spotifyAddedTime(s);
        case 'added':
        default:
          return songAddedTime(s);
      }
    };
    r.sort((a, b) => {
      const av = pick(a);
      const bv = pick(b);
      if (av == null || bv == null) return av == null ? (bv == null ? 0 : 1) : -1;
      if (typeof av === 'string' && typeof bv === 'string') {
        const c = av.localeCompare(bv);
        return sortDir === 'asc' ? c : -c;
      }
      return sortDir === 'asc' ? (av as number) - (bv as number) : (bv as number) - (av as number);
    });
    return r;
  });

  const stats = $derived.by(() => ({
    count: filtered.length,
    totalSec: filtered.reduce((n, s) => n + (s.durationSeconds ?? 0), 0),
    totalBytes: filtered.reduce((n, s) => n + (s.fileSizeBytes ?? 0), 0)
  }));

  return {
    get songs() {
      return songs;
    },
    get searchQuery() {
      return searchQuery;
    },
    get sortKey() {
      return sortKey;
    },
    get sortDir() {
      return sortDir;
    },
    get chips() {
      return chips;
    },
    get filtered() {
      return filtered;
    },
    get sorted() {
      return sorted;
    },
    get stats() {
      return stats;
    },
    get albumCount() {
      return new Set(songs.map((s) => albumKeyForSong(s))).size;
    },
    get hasFilters() {
      return chips.length > 0;
    },

    /**
     * How many rows this chip would leave, measured against the list already narrowed by the search
     * box and every *other* active chip.
     *
     * That framing is what makes plain AND safe to expose: an inactive chip's count is exactly what
     * you get by pressing it, and a combination with no overlap (Local files + Manually added — one
     * is scanned, the other downloaded) reads 0 before you press it rather than after. An active
     * chip's count is the current result count, since excluding itself and re-applying is a no-op.
     */
    countFor(key: ChipKey): number {
      const others = chips.filter((k) => k !== key);
      return applyChips(searched, others).filter(CHIP_PREDICATES[key]).length;
    },

    isChipActive(key: ChipKey): boolean {
      return chips.includes(key);
    },

    /**
     * The Spotify Liked chip carries its own order: newest save first, matching how the tracks appear
     * in Spotify itself. Releasing it restores the list's normal ordering rather than leaving the user
     * on a sort key nothing else can reach.
     */
    toggleChip(key: ChipKey) {
      const next = chips.includes(key)
        ? chips.filter((k) => k !== key)
        : CHIP_KEYS.filter((k) => k === key || chips.includes(k));
      syncSpotifySort(next);
      opts.onChipsChange?.(next);
    },

    toggleSort(k: SortKey) {
      if (sortKey === k) {
        sortDir = sortDir === 'asc' ? 'desc' : 'asc';
      } else {
        sortKey = k;
        sortDir = STRING_KEYS.includes(k) ? 'asc' : 'desc';
      }
    },

    /** Drops every chip, restoring the default sort if Spotify Liked was one of them. */
    clearFilters() {
      syncSpotifySort([]);
      opts.onChipsChange?.([]);
    }
  };

  /** Only fires on a transition, so an unrelated chip press never disturbs a chosen sort. */
  function syncSpotifySort(next: readonly ChipKey[]) {
    const wanted = next.includes('spotify-liked');
    if (wanted === chips.includes('spotify-liked')) return;
    sortKey = wanted ? 'spotify' : initialSortKey;
    sortDir = 'desc';
  }
}
