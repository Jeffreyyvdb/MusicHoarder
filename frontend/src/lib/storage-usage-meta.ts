/**
 * Labels and swatches for the storage breakdown, keyed by the API's string keys. Shared by the
 * sidebar's segmented bar and the breakdown dialog, so a category is the same colour in both.
 * Unknown keys fall back to the "other" entry: a bucket the server adds later renders instead of
 * breaking an older client.
 */
import type { StorageCategoryKey, StorageOriginKey } from '$lib/api-client';

export type StorageCategoryMeta = { label: string; description: string; color: string };
export type StorageOriginMeta = { label: string; color: string };

const CATEGORY_META: Record<StorageCategoryKey, StorageCategoryMeta> = {
  library: { label: 'Library', description: 'Built tracks in the library output folder', color: 'bg-chart-1' },
  source: { label: 'Local files', description: 'Your own files in the source folder', color: 'bg-chart-2' },
  staging: { label: 'Download staging', description: 'Downloads not yet released after building', color: 'bg-chart-4' },
  synced: { label: 'Synced', description: 'Received from another MusicHoarder instance', color: 'bg-chart-3' },
  soulseekStaging: { label: 'Soulseek staging', description: "slskd's completed-downloads folder", color: 'bg-orange-500' },
  videos: { label: 'Music videos', description: 'Clips and their thumbnails', color: 'bg-chart-5' },
  covers: { label: 'Album covers', description: 'Cover images in the library folders', color: 'bg-sky-500' },
  thumbnailCache: { label: 'Thumbnail cache', description: 'Resized covers; safe to delete, regenerates', color: 'bg-teal-500' },
  playlists: { label: 'Playlists', description: 'Exported m3u8 files', color: 'bg-violet-500' },
  untracked: { label: 'Untracked audio', description: 'Audio files no library track points at', color: 'bg-rose-500' },
  temp: { label: 'Temporary', description: 'In-flight uploads and hidden folders', color: 'bg-muted-foreground/60' },
  other: { label: 'Other files', description: 'Everything else in the managed folders', color: 'bg-muted-foreground/40' }
};

const ORIGIN_META: Record<StorageOriginKey, StorageOriginMeta> = {
  spotifyLiked: { label: 'Spotify Liked Songs', color: 'bg-chart-1' },
  spotifyPlaylist: { label: 'Spotify playlists', color: 'bg-chart-2' },
  deezerPlaylist: { label: 'Deezer playlists', color: 'bg-chart-3' },
  directUrl: { label: 'Added from a URL', color: 'bg-chart-4' },
  albumCompletion: { label: 'Album completion', color: 'bg-chart-5' },
  otherDownload: { label: 'Other downloads', color: 'bg-orange-500' },
  synced: { label: 'Synced from another instance', color: 'bg-sky-500' },
  local: { label: 'Local files', color: 'bg-teal-500' }
};

const ROOT_LABELS: Record<string, string> = {
  source: 'the source folder',
  destination: 'the library output folder',
  download: 'the downloads folder',
  videos: 'the videos folder',
  synced: 'the synced folder',
  coverCache: 'the thumbnail cache',
  slskd: 'the Soulseek downloads folder'
};

export function categoryMeta(key: string): StorageCategoryMeta {
  return (CATEGORY_META as Record<string, StorageCategoryMeta>)[key] ?? CATEGORY_META.other;
}

export function originMeta(key: string): StorageOriginMeta {
  return (ORIGIN_META as Record<string, StorageOriginMeta>)[key] ?? { label: key, color: 'bg-muted-foreground/40' };
}

export function rootLabel(key: string): string {
  return ROOT_LABELS[key] ?? key;
}
