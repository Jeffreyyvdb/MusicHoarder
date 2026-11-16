import { toast } from 'svelte-sonner';
import { toPlayerSong, type ApiSong } from '#lib/api-client.ts';
import { playerStore } from '#lib/stores/player.svelte.ts';
import { songDetail } from '#lib/stores/song-detail.svelte.ts';
import { artistOf, titleOf } from '#lib/track-list-view.svelte.ts';

/**
 * The one way a list adds a song to the queue — a row's swipe, its menu's Play next and Add to
 * queue — so every surface says the same thing afterwards. One toast at a time (each add replaces
 * the last), short, with a way into Now Playing's Queue to see where the song went.
 */

const TOAST_ID = 'mh-queue-add';

/** Lucide's list-plus, inline: the swipe panel's icon (an action builds its DOM by hand). */
export const ADD_TO_QUEUE_ICON =
  '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M11 12H3"/><path d="M16 6H3"/><path d="M16 18H3"/><path d="M18 9v6"/><path d="M21 12h-6"/></svg>';

/** Lucide's list-minus, inline: the queue rows' swipe-to-remove. */
export const REMOVE_FROM_QUEUE_ICON =
  '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M11 12H3"/><path d="M16 6H3"/><path d="M16 18H3"/><path d="M21 12h-6"/></svg>';

/** Open Now Playing on its Queue. */
export function openQueue(): void {
  const id = playerStore.currentSong?.id;
  if (id != null) songDetail.open(id, undefined, { mode: 'queue' });
}

/** Add `song` to the queue (`last`) or straight after the current song (`next`), and say so. */
export function queueSong(song: ApiSong, where: 'next' | 'last' = 'last'): void {
  const outcome = playerStore.addToQueue(toPlayerSong(song, artistOf(song)), where);
  if (outcome === 'queued') {
    toast(where === 'next' ? 'Playing next' : 'Added to queue', {
      id: TOAST_ID,
      description: titleOf(song),
      duration: 2500,
      action: { label: 'Open', onClick: openQueue }
    });
  } else if (outcome === 'elsewhere') {
    toast('Playing on another device', {
      id: TOAST_ID,
      description: 'Its queue is on that device. Play here to queue songs on this one.'
    });
  }
}
