/**
 * Registers MusicHoarder's WebMCP tools for the signed-in account, and takes them away again.
 * The `(app)` layout calls {@link installWebMcp} once per account; in a browser without WebMCP it
 * does nothing at all.
 *
 * Which tools an account gets follows what the server would let it do (`isAdmin`, `can`), never
 * the role name — the same rule as the nav. The server still enforces every one of them.
 */

import { goto } from '$app/navigation';
import {
  fetchEnrichmentDetail,
  fetchReviewQueue,
  submitManualReview,
  toPlayerSong
} from '#lib/api-client.ts';
import { can, isAdmin, isDemo } from '#lib/auth/capabilities.ts';
import type { SessionUser } from '#lib/auth/session-types.ts';
import {
  inboxCounts,
  refreshInboxNameCounts,
  refreshInboxQueueCounts
} from '#lib/stores/nav-badges.svelte.ts';
import { playerStore } from '#lib/stores/player.svelte.ts';
import { playlistsStore } from '#lib/stores/playlists.svelte.ts';
import { songsStore } from '#lib/stores/songs.svelte.ts';
import { shuffle } from '#lib/utils.ts';
import { createInboxTools } from './admin-tools.ts';
import { TAG_REVIEW_DECIDED, type TagReviewDecidedDetail } from './events.ts';
import { createListenTools, type LibraryView, type PlayerPort } from './listen-tools.ts';
import { findModelContext, registerTools, type ModelContextTool } from './model-context.ts';

async function library(): Promise<LibraryView> {
  if (!songsStore.hasLoaded) await songsStore.loadSongs({ silent: true });
  if (!songsStore.hasLoaded)
    throw new Error(songsStore.error ?? 'The library could not be loaded.');
  return {
    songs: songsStore.builtSongs,
    songsById: songsStore.songsById,
    albums: songsStore.albums,
    artists: songsStore.leadArtistGroups
  };
}

async function playlists() {
  if (!playlistsStore.hasLoaded) await playlistsStore.load({ silent: true });
  if (!playlistsStore.hasLoaded) {
    throw new Error(playlistsStore.error ?? 'The playlists could not be loaded.');
  }
  return playlistsStore.playlists;
}

const player: PlayerPort = {
  get currentSong() {
    return playerStore.currentSong;
  },
  get isPlaying() {
    return playerStore.isPlaying;
  },
  get currentTime() {
    return playerStore.currentTime;
  },
  get duration() {
    return playerStore.duration;
  },
  get hasNext() {
    return playerStore.hasNext;
  },
  get upNext() {
    return playerStore.upNext;
  },
  startQueue: (songs, index) => playerStore.startQueue(songs, index),
  pause: () => playerStore.pause(),
  resume: () => playerStore.resume(),
  next: () => playerStore.playNext(),
  previous: () => playerStore.playPrevious(),
  seek: (seconds) => playerStore.seek(seconds)
};

/** Poll, not an effect: this runs outside any component, and only for the length of one call. */
function waitUntilPlaying(timeoutMs: number): Promise<boolean> {
  const deadline = Date.now() + timeoutMs;
  return new Promise((resolve) => {
    const check = () => {
      if (playerStore.isPlaying) resolve(true);
      else if (Date.now() >= deadline) resolve(false);
      else setTimeout(check, 100);
    };
    check();
  });
}

async function setLiked(songId: number, liked: boolean): Promise<boolean> {
  const song = songsStore.songsById.get(songId);
  if (song && Boolean(song.likedAtUtc) !== liked) await songsStore.toggleLike(songId);
  return Boolean(songsStore.songsById.get(songId)?.likedAtUtc);
}

/** The tools this account gets. Exported for the tests. */
export function toolsFor(user: SessionUser): ModelContextTool[] {
  const demo = isDemo(user);
  const tools = createListenTools({
    library,
    player,
    playlists: {
      list: playlists,
      create: (name, songIds) => playlistsStore.create(name, songIds),
      addSongs: (id, songIds) => playlistsStore.addSongs(id, songIds)
    },
    setLiked,
    toPlayerSong,
    navigate: (path) => goto(path),
    waitUntilPlaying,
    shuffle,
    canLike: !demo && can(user, 'TrackListening'),
    canEditPlaylists: !demo
  });

  // The Inbox is admin's, and demo's to look at; a member has none.
  if (isAdmin(user) || demo) {
    tools.push(
      ...createInboxTools({
        counts: async (includeNames) => {
          await Promise.all([
            library(),
            refreshInboxQueueCounts(true),
            includeNames ? refreshInboxNameCounts(true) : Promise.resolve()
          ]);
          return inboxCounts();
        },
        reviewQueue: () => fetchReviewQueue('needsreview'),
        enrichmentDetail: fetchEnrichmentDetail,
        submitReview: submitManualReview,
        afterDecision: (songId) => {
          window.dispatchEvent(
            new CustomEvent<TagReviewDecidedDetail>(TAG_REVIEW_DECIDED, { detail: { songId } })
          );
          void songsStore.loadSongs({ silent: true });
          void refreshInboxQueueCounts(true);
        },
        isAdmin: isAdmin(user)
      })
    );
  }
  return tools;
}

/**
 * Register the account's tools. Returns the cleanup, which unregisters them all (sign-out, an
 * account switch, leaving the app shell).
 */
export function installWebMcp(user: SessionUser | null | undefined): () => void {
  const context = findModelContext();
  if (!context || !user) return () => {};
  const controller = new AbortController();
  void registerTools(context, toolsFor(user), controller.signal);
  return () => controller.abort();
}
