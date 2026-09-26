import { toast } from 'svelte-sonner';
import { toPlayerSong, type ChatShare } from '$lib/api-client';
import {
  fetchSharePayload,
  reportSharePlay,
  shareCoverUrl,
  shareStreamUrl
} from '$lib/share-client';
import { playerStore, type PlayerSong } from '$lib/stores/player.svelte';
import { songsStore } from '$lib/stores/songs.svelte';

/**
 * Plays a song or album someone sent in a chat, right here in the app's player.
 *
 * It streams through the share link the message carries — the one thing every recipient can play,
 * whatever they can otherwise see of the sender's library — exactly as the share page does, and
 * reports the play the way the share page does, so the sender's Share links page counts it. The
 * sender's own copy (a song you sent) plays from your library instead, like any library track.
 */
export async function playChatShare(share: ChatShare): Promise<void> {
  if (share.ownedByViewer && share.scope === 'Song') {
    const song = songsStore.songsById.get(share.songId);
    if (song) {
      const track = toPlayerSong(song, share.artist ?? '');
      await playerStore.playSong(track, [track], 0);
      return;
    }
  }

  const token = share.token;
  if (!token) {
    toast.error('This link was turned off, so it can no longer be played.');
    return;
  }

  try {
    const payload = await fetchSharePayload(fetch, token);
    const albumArtist = payload.album.artist ?? share.artist ?? '';
    const queue: PlayerSong[] = payload.tracks.map((t) => ({
      id: t.id,
      title: t.title,
      artist: t.artist ?? albumArtist,
      streamUrl: shareStreamUrl(token, t.id),
      coverUrl: t.hasCoverArt ? shareCoverUrl(token, t.id) : null,
      album: payload.album.title ?? null
    }));
    const index = Math.max(0, queue.findIndex((t) => t.id === payload.sharedSongId));
    const start = queue[index];
    if (!start) return;
    await playerStore.playSong(start, queue, index);
    reportSharePlay(token, start.id);
  } catch {
    toast.error('Could not play this — the link may have been turned off.');
  }
}

/** Plays a song of the viewer's own library (a Spotify link they already have). */
export async function playLibrarySong(songId: number): Promise<void> {
  const song = songsStore.songsById.get(songId);
  if (!song) {
    toast.error('That song is not in your library any more.');
    return;
  }
  const track = toPlayerSong(song, '');
  await playerStore.playSong(track, [track], 0);
}
