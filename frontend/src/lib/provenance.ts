import {
  Download,
  HardDrive,
  Heart,
  Link,
  ListMusic,
  ListPlus,
  ListVideo,
  RefreshCw
} from '@lucide/svelte';
import type { ProvenanceGroup, ProvenanceReason, ProvenanceSeed } from '$lib/api-client';

/** One glyph per reason a track is in the library — the album header's row and the sheet's rows. */
export const PROVENANCE_ICON: Record<ProvenanceReason, typeof HardDrive> = {
  LocalFile: HardDrive,
  SpotifyLiked: Heart,
  SpotifyPlaylist: ListMusic,
  DeezerPlaylist: ListMusic,
  YouTubePlaylist: ListVideo,
  Link: Link,
  Synced: RefreshCw,
  Downloaded: Download,
  AlbumFill: ListPlus
};

/**
 * A seed's second line: its own reason, the album it sits on when that is not the one being filled
 * in (the explanation already names that one), and whether it has been deleted since.
 */
export function seedSublabel(seed: ProvenanceSeed, fillAlbum: string | null | undefined): string {
  const parts = [seed.label];
  if (seed.album && seed.album.toLowerCase() !== (fillAlbum ?? '').toLowerCase()) {
    parts.push(`on “${seed.album}”`);
  }
  if (seed.isDeleted) parts.push('since deleted');
  return parts.join(' · ');
}

/**
 * The groups worth a section of their own. One whose every track is already named under an album
 * fill's "Started from" says nothing new — the seed row carries the same reason — so it is dropped
 * rather than listing the track twice.
 */
export function visibleProvenanceGroups(groups: ProvenanceGroup[]): ProvenanceGroup[] {
  const seeds = new Set(groups.flatMap((g) => g.fill?.seeds.map((s) => s.songId) ?? []));
  return groups.filter(
    (g) => g.reason === 'AlbumFill' || !g.tracks.every((t) => seeds.has(t.songId))
  );
}
