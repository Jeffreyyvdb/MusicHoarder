import { coverUrlForSong, getSongCoverUrl } from '$lib/api-client';
import { songsStore } from '$lib/stores/songs.svelte';

/**
 * Artwork for a row that only knows a song id (Inbox Duplicates and AI flagged, the AI quality
 * list), so the track looks as it does in Tracks rather than as an initials tile. The loaded
 * library knows whether the file has art, which saves a request that would only 404; before it
 * has loaded, the cover endpoint is asked anyway — it 404s for a file with none, and Cover falls
 * back to the initials tile.
 */
export function coverUrlForSongId(songId: number): string | null {
  const song = songsStore.songsById.get(songId);
  return song ? coverUrlForSong(song) : getSongCoverUrl(songId);
}
