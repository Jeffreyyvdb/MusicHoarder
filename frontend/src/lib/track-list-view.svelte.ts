import {
  albumKeyForSong,
  isSpotifySourced,
  songAddedTime,
  songLikedTime,
  spotifyAddedTime,
  type ApiSong
} from '$lib/api-client';

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
  initialSortKey?: SortKey;
}) {
  const initialSortKey = opts.initialSortKey ?? 'added';

  let sortKey = $state<SortKey>(initialSortKey);
  let sortDir = $state<'asc' | 'desc'>('desc');
  let lyricsOnly = $state(false);
  let spotifyOnly = $state(false);

  const songs = $derived(opts.songs());
  const searchQuery = $derived(opts.searchQuery());

  const filtered = $derived.by(() => {
    let r = songs;
    const q = searchQuery.trim().toLowerCase();
    if (q) {
      r = r.filter(
        (s) =>
          titleOf(s).toLowerCase().includes(q) ||
          artistOf(s).toLowerCase().includes(q) ||
          (s.album ?? '').toLowerCase().includes(q)
      );
    }
    if (lyricsOnly) r = r.filter(hasLyrics);
    if (spotifyOnly) r = r.filter(isSpotifySourced);
    return r;
  });

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
    get lyricsOnly() {
      return lyricsOnly;
    },
    set lyricsOnly(v: boolean) {
      lyricsOnly = v;
    },
    get spotifyOnly() {
      return spotifyOnly;
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
      return lyricsOnly || spotifyOnly;
    },
    get spotifyCount() {
      return songs.filter(isSpotifySourced).length;
    },

    /**
     * The Spotify filter carries its own order: newest save first, matching how the tracks appear in
     * Spotify itself. Turning it off restores the list's normal ordering rather than leaving the user
     * on a sort key nothing else can reach.
     */
    toggleSpotifyOnly() {
      spotifyOnly = !spotifyOnly;
      sortKey = spotifyOnly ? 'spotify' : initialSortKey;
      sortDir = 'desc';
    },

    toggleSort(k: SortKey) {
      if (sortKey === k) {
        sortDir = sortDir === 'asc' ? 'desc' : 'asc';
      } else {
        sortKey = k;
        sortDir = STRING_KEYS.includes(k) ? 'asc' : 'desc';
      }
    },

    /** Goes through toggleSpotifyOnly so the sort restoration above still fires. */
    clearFilters() {
      lyricsOnly = false;
      if (spotifyOnly) this.toggleSpotifyOnly();
    }
  };
}
