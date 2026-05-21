/**
 * Orthogonal "Organize by" filter applied to the library on top of section filters.
 *
 * The `/artists` and `/years` index grids link into the existing library at
 * `/app?artist=<name>` / `/app?year=<n>` (`?year=unknown` for the no-year bucket).
 * `/app` parses the URL with `parseBrowseFilter` and narrows `fetchSongs()` with
 * `applyBrowseFilter` before its usual section-filter + album grouping.
 */
import { UNKNOWN_GROUP, type ApiSong } from '$lib/api-client';

const UNKNOWN_ARTIST = 'Unknown Artist';
const UNKNOWN_YEAR_PARAM = UNKNOWN_GROUP.toLowerCase(); // "unknown"

export interface BrowseFilter {
  /** Match on `(albumArtist ?? artist ?? "Unknown Artist")`, case-insensitive. */
  artist?: string;
  /** Match on `song.year`. Use the sentinel below for the no-year bucket. */
  year?: number;
  /** True when the year param was the "unknown" bucket (songs with no year). */
  yearUnknown?: boolean;
}

function songArtist(song: ApiSong): string {
  return (song.albumArtist ?? song.artist ?? UNKNOWN_ARTIST).trim() || UNKNOWN_ARTIST;
}

export function parseBrowseFilter(params: URLSearchParams): BrowseFilter | null {
  const artist = params.get('artist');
  if (artist != null && artist.trim().length > 0) {
    return { artist: artist.trim() };
  }
  const year = params.get('year');
  if (year != null && year.trim().length > 0) {
    if (year.trim().toLowerCase() === UNKNOWN_YEAR_PARAM) {
      return { yearUnknown: true };
    }
    const parsed = Number.parseInt(year, 10);
    if (Number.isFinite(parsed)) return { year: parsed };
  }
  return null;
}

export function applyBrowseFilter(songs: ApiSong[], filter: BrowseFilter | null): ApiSong[] {
  if (!filter) return songs;
  if (filter.artist != null) {
    const target = filter.artist.toLowerCase();
    return songs.filter((s) => songArtist(s).toLowerCase() === target);
  }
  if (filter.yearUnknown) {
    return songs.filter((s) => typeof s.year !== 'number' || !Number.isFinite(s.year));
  }
  if (filter.year != null) {
    return songs.filter((s) => s.year === filter.year);
  }
  return songs;
}

export function browseFilterLabel(filter: BrowseFilter): string {
  if (filter.artist != null) return filter.artist;
  if (filter.yearUnknown) return UNKNOWN_GROUP;
  if (filter.year != null) return String(filter.year);
  return '';
}

/** The href that clears the active browse filter, returning to its index grid. */
export function browseFilterClearHref(filter: BrowseFilter): string {
  return filter.artist != null ? '/artists' : '/years';
}

export function browseFilterKind(filter: BrowseFilter): 'artist' | 'year' {
  return filter.artist != null ? 'artist' : 'year';
}
