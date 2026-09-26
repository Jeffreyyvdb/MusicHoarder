import type { LibraryPlaylist, PlaylistSourceType } from '$lib/api-client';

// Wording for playlists, shared by the Playlists page, a playlist's page and the Add to Playlist
// sheet so all three describe a playlist the same way. Android's data/Playlists.kt says the same.

/** Where a synced playlist comes from, as a short label ("Spotify", "YouTube"). */
export function sourceLabel(type: PlaylistSourceType): string {
  switch (type) {
    case 'spotifyLiked':
    case 'spotifyPlaylist':
      return 'Spotify';
    case 'deezer':
      return 'Deezer';
    case 'youtube':
      return 'YouTube';
  }
}

/** "1 track", "42 tracks" — the app's one word for them, as the Tracks tab and album pages say. */
export function trackCountLabel(count: number): string {
  return `${count.toLocaleString()} ${count === 1 ? 'track' : 'tracks'}`;
}

/**
 * The line under a playlist's name in a list: where it comes from (for a synced one) and how many
 * tracks it plays — "Spotify · 42 tracks", "12 tracks", "YouTube · Empty".
 */
export function playlistSubtitle(playlist: Pick<LibraryPlaylist, 'source' | 'songIds'>): string {
  const count = playlist.songIds.length === 0 ? 'Empty' : trackCountLabel(playlist.songIds.length);
  return playlist.source ? `${sourceLabel(playlist.source.type)} · ${count}` : count;
}

/** What a synced playlist is still waiting for: "8 not in your library yet", or null. */
export function missingLabel(
  playlist: Pick<LibraryPlaylist, 'source' | 'missingCount'>
): string | null {
  if (!playlist.source || playlist.missingCount <= 0) return null;
  return `${playlist.missingCount.toLocaleString()} not in your library yet`;
}

/** The kind of playlist, for its page's subtitle ("Playlist", "Spotify playlist", …). */
export function playlistKindLabel(playlist: Pick<LibraryPlaylist, 'source'>): string {
  if (!playlist.source) return 'Playlist';
  if (playlist.source.type === 'spotifyLiked') return 'Spotify Liked Songs';
  return `${sourceLabel(playlist.source.type)} playlist`;
}
