/**
 * Pure section-filter helpers used by Gallery + AppSidebar counts.
 *
 * The MusicHoarder backend does not expose dedicated /recent /duplicates
 * /missing-metadata /queue endpoints, so these are derived client-side
 * from the same `fetchSongs()` result the rest of /library reads.
 */
import { buildAlbumsFromSongs, mapEnrichmentStatus, sortAlbumsByRecency, type ApiSong } from '$lib/api-client';

export type SectionId = 'lib' | 'recent' | 'dupes' | 'missing' | 'queue';

export const RECENT_ALBUM_CAP = 50;

/**
 * Songs belonging to the N most-recently-added albums (newest first by build/index time).
 * Capped by album count — callers re-group these songs into exactly those N albums.
 */
export function filterRecent(songs: ApiSong[], cap = RECENT_ALBUM_CAP): ApiSong[] {
  return sortAlbumsByRecency(buildAlbumsFromSongs(songs))
    .slice(0, cap)
    .flatMap((a) => a.songs);
}

/**
 * Songs that share a fingerprint OR share (albumArtist, title, ~duration) with
 * at least one other song. Returns the full set of cluster members.
 */
export function filterDuplicates(songs: ApiSong[]): ApiSong[] {
  const byFp = new Map<string, ApiSong[]>();
  const byTitle = new Map<string, ApiSong[]>();
  for (const s of songs) {
    const fp = s.fingerprint?.trim();
    if (fp) {
      const list = byFp.get(fp) ?? [];
      list.push(s);
      byFp.set(fp, list);
    }
    const artist = (s.albumArtist ?? s.artist ?? '').trim().toLowerCase();
    const title = (s.title ?? s.fileName).trim().toLowerCase();
    const dur = s.durationSeconds ? Math.round(s.durationSeconds) : 0;
    const key = `${artist}::${title}::${dur}`;
    const list = byTitle.get(key) ?? [];
    list.push(s);
    byTitle.set(key, list);
  }
  const out = new Map<number, ApiSong>();
  for (const list of byFp.values()) {
    if (list.length > 1) for (const s of list) out.set(s.id, s);
  }
  for (const list of byTitle.values()) {
    if (list.length > 1) for (const s of list) out.set(s.id, s);
  }
  return Array.from(out.values());
}

/**
 * Songs that need a human's attention — NeedsReview or Failed. Pending /
 * Processing songs are excluded because they belong in the processing strip,
 * not the "missing" bucket.
 */
export function filterMissingMetadata(songs: ApiSong[]): ApiSong[] {
  return songs.filter((s) => {
    const n = mapEnrichmentStatus(s.enrichmentStatus);
    return n === 'needsreview' || n === 'failed';
  });
}

/** Queue section renders only the processing strip — return an empty list. */
export function filterQueue(_songs: ApiSong[]): ApiSong[] {
  return [];
}

export function applySectionFilter(songs: ApiSong[], section: SectionId): ApiSong[] {
  switch (section) {
    case 'recent':
      return filterRecent(songs);
    case 'dupes':
      return filterDuplicates(songs);
    case 'missing':
      return filterMissingMetadata(songs);
    case 'queue':
      return filterQueue(songs);
    case 'lib':
    default:
      return songs;
  }
}

export const SECTION_LABELS: Record<SectionId, { title: string; subtitle: (n: number) => string }> = {
  lib: {
    title: 'Library',
    subtitle: (n) => `${n.toLocaleString()} album${n === 1 ? '' : 's'} · all destinations indexed`
  },
  recent: {
    title: 'Recently Added',
    subtitle: (n) =>
      n === 0 ? 'No recent imports' : `${n.toLocaleString()} album${n === 1 ? '' : 's'} from the latest imports`
  },
  dupes: {
    title: 'Duplicates',
    subtitle: (n) =>
      n === 0 ? 'No duplicates detected' : `${n.toLocaleString()} potential duplicate${n === 1 ? '' : 's'} — review before merging`
  },
  missing: {
    title: 'Missing metadata',
    subtitle: (n) =>
      n === 0 ? 'Every track has a confirmed match' : `${n.toLocaleString()} album${n === 1 ? '' : 's'} need manual review`
  },
  queue: {
    title: 'Import Queue',
    subtitle: () => 'Files awaiting enrichment — they land in the library once they finish writing.'
  }
};

export function isSectionId(value: string | null | undefined): value is SectionId {
  return value === 'lib' || value === 'recent' || value === 'dupes' || value === 'missing' || value === 'queue';
}
