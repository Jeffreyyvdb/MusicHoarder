import { untrack } from 'svelte';
import { browser } from '$app/environment';
import { toast } from 'svelte-sonner';
import { coverThumbUrl, fetchRadio, reportSongPlayed, toPlayerSong } from '$lib/api-client';
import {
  canAutoResume,
  readPlaybackSnapshot,
  writePlaybackSnapshot,
  type PlaybackSnapshot
} from '$lib/player-snapshot';
import { songsStore } from '$lib/stores/songs.svelte';
import { artistOf } from '$lib/track-list-view.svelte';

export interface PlayerSong {
  id: number;
  title: string;
  artist: string;
  streamUrl: string;
  /** Album-art URL (or null to fall back to the tinted Cover placeholder). */
  coverUrl?: string | null;
  /** Album name, surfaced on the OS Media Session tile (or null/omitted). */
  album?: string | null;
}

let currentSong = $state<PlayerSong | null>(null);
let isPlaying = $state(false);
let currentTime = $state(0);
let duration = $state(0);
let volumeState = $state(1);
/**
 * Playback speed (1 = normal). Pitch is preserved (`preservesPitch`), so
 * slowing a song down keeps it singable — this exists for practising along
 * with tracks, not chipmunk mode. Session-scoped and sticky across tracks;
 * a reload returns to 1× so nobody is left wondering why everything drags.
 */
let playbackRateState = $state(1);
/**
 * Ordered playback context the current song was started from (an album's
 * tracks, a review list, etc.). `queueIndex` points at `currentSong` within it.
 * When a song ends we advance to `queue[queueIndex + 1]`; reaching the end no
 * longer stops playback — the radio appends more (see `topUpRadio`).
 */
let queue = $state<PlayerSong[]>([]);
let queueIndex = $state(-1);

/**
 * The track this station was built from: the last song the user *chose*, not
 * whatever the radio happens to be playing now. Anchoring it keeps a station
 * coherent — reseeding from each appended track lets it wander off in a few
 * hops until it has nothing to do with what was picked.
 */
let radioSeedId = $state<number | null>(null);
/** True once the server has no unplayed neighbour left; stops us asking again. */
let radioExhausted = $state(false);
/** The in-flight top-up, so a prefetch and an `ended` cannot both ask. */
let radioTopUp: Promise<boolean> | null = null;

/** Tracks fetched per top-up. Enough to outlive a few skips without a stall. */
const RADIO_BATCH = 20;
/** Remaining tracks at which the next batch is fetched, so the gap is inaudible. */
const RADIO_PREFETCH_AT = 2;
/** Ids sent as already-heard. Matches the server's own cap on the parameter. */
const RADIO_EXCLUDE_CAP = 400;
/**
 * Set to true while the in-page TrackPanel is mounted with its own waveform
 * player. The global MiniPlayer hides itself when this is true to avoid
 * stacking two bottom-anchored controls.
 */
let panelMountedCount = $state(0);
/**
 * True after the user dismisses the MiniPlayer bar with its close (X) control.
 * Dismissal pauses playback and hides the bar but keeps `currentSong`/`queue`
 * intact — pressing play anywhere (row, panel, OS media keys) clears the flag
 * so the bar comes back with its full state. Only `stop()` tears state down.
 */
let miniPlayerDismissed = $state(false);

let audioEl: HTMLAudioElement | null = null;
/** Pre-mute level, restored on unmute so toggling mute is non-destructive. */
let lastNonZeroVolume = 1;
let loadGeneration = 0;
let rafHandle: number | null = null;
let lastTimeWrite = 0;

/**
 * Minimum gap between `currentTime` state writes while playing. The RAF loop
 * still runs every frame, but committing the reactive value at ~10 Hz instead
 * of ~60 Hz keeps the progress UI smooth while avoiding a per-frame re-render
 * storm (the MiniPlayer slider forces a full-document reflow on each write, so
 * at 60 Hz it saturates the main thread and starves audio playback).
 */
const TIME_WRITE_INTERVAL_MS = 100;

/**
 * How often the playing position is written to the reload snapshot. The unload hook writes
 * the exact second a reload happens at; this is the safety net for a tab that dies without
 * one (a crash, a killed process), where losing a few seconds is fine and losing the queue
 * is not.
 */
const POSITION_PERSIST_INTERVAL_MS = 5000;
let lastPositionPersist = 0;

function startRaf() {
  if (rafHandle !== null) return;
  lastTimeWrite = 0;
  const tick = (now: number) => {
    if (audioEl && now - lastTimeWrite >= TIME_WRITE_INTERVAL_MS) {
      lastTimeWrite = now;
      currentTime = audioEl.currentTime;
    }
    if (now - lastPositionPersist >= POSITION_PERSIST_INTERVAL_MS) {
      lastPositionPersist = now;
      persistPlayback();
    }
    rafHandle = requestAnimationFrame(tick);
  };
  rafHandle = requestAnimationFrame(tick);
}

function stopRaf() {
  if (rafHandle !== null) {
    cancelAnimationFrame(rafHandle);
    rafHandle = null;
  }
}

// ── OS Media Session integration ───────────────────────────────────────────
// Feed `navigator.mediaSession` so the OS "Now Playing" surfaces (macOS Control
// Center, lock screens, Bluetooth/car displays, hardware media keys) show the
// current song and drive the in-app queue. All entry points are feature-detected
// so SSR and browsers without support (or partial support) are silent no-ops —
// each `setActionHandler` is also try/caught since older WebKit throws on
// actions it doesn't recognise.

function mediaSession(): MediaSession | null {
  if (!browser || !('mediaSession' in navigator)) return null;
  return navigator.mediaSession;
}

function updateMediaMetadata(song: PlayerSong) {
  const ms = mediaSession();
  if (!ms) return;
  // Size our own cover endpoint to the 512px WebP bucket; external URLs (Spotify
  // CDN) pass through unchanged. Omit `artwork` entirely when there's no cover.
  const art = coverThumbUrl(song.coverUrl, 512);
  ms.metadata = new MediaMetadata({
    title: song.title,
    artist: song.artist,
    album: song.album ?? '',
    artwork: art ? [{ src: art, sizes: '512x512', type: 'image/webp' }] : []
  });
}

function updatePositionState() {
  const ms = mediaSession();
  if (!ms?.setPositionState) return;
  if (!Number.isFinite(duration) || duration <= 0) return;
  ms.setPositionState({
    duration,
    position: Math.min(Math.max(0, currentTime), duration),
    playbackRate: playbackRateState
  });
}

/** (Re)register action handlers, nulling prev/next at the queue ends so the OS greys them out. */
function refreshActionHandlers() {
  const ms = mediaSession();
  if (!ms) return;
  const set = (action: MediaSessionAction, handler: MediaSessionActionHandler | null) => {
    try {
      ms.setActionHandler(action, handler);
    } catch {
      // Action unsupported by this browser — ignore.
    }
  };
  set('play', () => resume());
  set('pause', () => pause());
  set('previoustrack', queueIndex > 0 ? () => playPrevious() : null);
  set('nexttrack', canAdvance() ? () => playNext() : null);
  set('seekto', (details) => {
    if (typeof details.seekTime === 'number') seek(details.seekTime);
  });
  set('seekbackward', (details) => seek(currentTime - (details.seekOffset ?? 10)));
  set('seekforward', (details) => seek(currentTime + (details.seekOffset ?? 10)));
}

function setPlaybackState(state: MediaSessionPlaybackState) {
  const ms = mediaSession();
  if (ms) ms.playbackState = state;
}

/**
 * Own the audio element imperatively rather than rendering it in a component.
 * A DOM-rendered `<audio>` is subject to Svelte's reconciliation: re-renders
 * that touch its subtree (e.g. closing the in-page TrackPanel) recreate/
 * re-initialize it, which makes the player reload the stream from byte 0 —
 * audible as a re-buffer/stutter mid-playback. An element from `new Audio()`
 * never enters the rendered tree, so no re-render can disturb playback.
 */
function ensureAudioEl(): HTMLAudioElement | null {
  if (!browser) return null;
  if (audioEl) return audioEl;

  const el = new Audio();
  el.preload = 'metadata';
  el.volume = volumeState;
  // `defaultPlaybackRate` is what a new `src` resets `playbackRate` to, so
  // keeping both in sync makes the chosen speed survive track changes.
  el.defaultPlaybackRate = playbackRateState;
  el.playbackRate = playbackRateState;
  el.preservesPitch = true;

  el.addEventListener('loadedmetadata', () => {
    duration = el.duration;
    updatePositionState();
  });
  el.addEventListener('ended', () => {
    stopRaf();
    isPlaying = false;
    if (Number.isFinite(el.duration)) currentTime = el.duration; // land the bar at 100%
    playNext(); // no-op when the current song is the last in the queue
  });
  el.addEventListener('error', () => {
    stopRaf();
    isPlaying = false;
    const song = currentSong;
    if (song) {
      toast.error('Playback failed', { description: `Could not play "${song.title}".` });
    }
  });
  el.addEventListener('play', () => {
    isPlaying = true;
    setPlaybackState('playing');
    startRaf();
  });
  el.addEventListener('pause', () => {
    stopRaf();
    isPlaying = false;
    setPlaybackState('paused');
    // The rAF stops here, so commit the exact paused position (the throttled
    // loop may have last written it up to TIME_WRITE_INTERVAL_MS ago).
    currentTime = el.currentTime;
  });

  audioEl = el;
  // Register once on the session-owned element; handlers re-evaluate queue
  // position each time they fire, and refreshActionHandlers() re-runs on load.
  refreshActionHandlers();
  return el;
}

/**
 * Start/resume playback on the store-owned element. Surfaces an autoplay block
 * (the one failure the media `error` event does NOT cover); genuine media/
 * network failures still flow through the `error` listener, and `AbortError`
 * (a newer load/pause superseding this play) is intentionally ignored.
 */
function attemptPlay() {
  miniPlayerDismissed = false; // any play intent brings the mini player back
  void audioEl
    ?.play()
    .then(() => (isPlaying = true))
    .catch((err: unknown) => {
      isPlaying = false;
      if (err instanceof DOMException && err.name === 'NotAllowedError') {
        toast.error('Playback blocked', {
          description: 'Your browser blocked autoplay — press play to start.'
        });
      }
    });
}

/** Load a fresh song onto the element and start playback (no queue changes). */
async function loadAndPlay(song: PlayerSong) {
  if (!ensureAudioEl() || !audioEl) return;

  const gen = ++loadGeneration;

  try {
    const res = await fetch(song.streamUrl, { headers: { Range: 'bytes=0-0' } });
    if (!res.ok) {
      toast.error('Unable to play track', {
        description: 'The audio file could not be found on the server.'
      });
      return;
    }
  } catch {
    toast.error('Unable to play track', { description: 'Could not connect to the server.' });
    return;
  }

  if (gen !== loadGeneration) return;

  currentSong = song;
  currentTime = 0;
  duration = 0;
  updateMediaMetadata(song);
  refreshActionHandlers(); // queue position may have changed (next/prev availability)
  audioEl.src = song.streamUrl;
  audioEl.load();
  attemptPlay();
  reportPlay(song.id);
}

/**
 * Fire-and-forget play reporting (feeds the overview's last-played / discover
 * shelves). Failures are expected for demo sessions (write-blocked) and
 * anonymous share playback — never let them disturb playback.
 */
function reportPlay(songId: number) {
  songsStore.notePlayed(songId);
  void reportSongPlayed(songId).catch(() => {});
}

/**
 * Play a song, optionally seeding the playback queue it belongs to so the
 * player can auto-advance and offer prev/next. Re-clicking the current song
 * toggles play/pause. `index` defaults to the song's position in `contextQueue`.
 */
async function playSong(song: PlayerSong, contextQueue?: PlayerSong[], index?: number) {
  if (!ensureAudioEl() || !audioEl) return;

  if (contextQueue && contextQueue.length > 0) {
    queue = contextQueue;
    queueIndex = index ?? contextQueue.findIndex((s) => s.id === song.id);
  } else {
    queue = [song];
    queueIndex = 0;
  }

  // A deliberate pick re-seeds the station and revives an exhausted one: the user has just said
  // what they want to hear next, which is exactly the question the radio answers.
  radioSeedId = song.id;
  radioExhausted = false;
  maybePrefetchRadio();

  if (currentSong?.id === song.id) {
    if (audioEl.paused) {
      attemptPlay();
    } else {
      audioEl.pause();
      isPlaying = false;
    }
    return;
  }

  await loadAndPlay(song);
}

function playNext() {
  if (queueIndex < 0) return;
  if (queueIndex < queue.length - 1) {
    advance();
    return;
  }
  // At the tail. This is the path a one-track album takes: nothing follows it in the queue, so the
  // station is what keeps the music going instead of the bar going silent.
  void topUpRadio().then((appended) => {
    if (appended) advance();
  });
}

function advance() {
  queueIndex += 1;
  void loadAndPlay(queue[queueIndex]);
  maybePrefetchRadio();
}

/** True while there is either a queued track ahead or a station able to supply one. */
function canAdvance(): boolean {
  if (queueIndex < 0) return false;
  return queueIndex < queue.length - 1 || (radioSeedId !== null && !radioExhausted);
}

/** Fetch the next batch before the queue actually runs out, so no gap is heard. */
function maybePrefetchRadio() {
  if (queue.length - 1 - queueIndex <= RADIO_PREFETCH_AT) void topUpRadio();
}

/**
 * Append the station's next tracks to the queue, resolving ids against the rows the library
 * already holds.
 *
 * The ranking itself is deliberately not here: it lives in `RadioRanker` on the server so the
 * Android client plays the same station. This end only joins ids and appends.
 *
 * @returns whether anything was appended.
 */
async function topUpRadio(): Promise<boolean> {
  if (radioSeedId === null || radioExhausted) return false;
  if (radioTopUp) return radioTopUp;

  const seed = radioSeedId;
  radioTopUp = (async () => {
    try {
      const heard = queue.map((s) => s.id);
      const ids = await fetchRadio(seed, heard.slice(-RADIO_EXCLUDE_CAP), RADIO_BATCH);
      // The user may have picked something else while this was in flight; those ids are for a
      // station nobody is listening to any more.
      if (radioSeedId !== seed) return false;

      const queued = new Set(heard);
      const rows = songsStore.songsById;
      const additions: PlayerSong[] = [];
      for (const id of ids) {
        if (queued.has(id)) continue;
        const row = rows.get(id);
        if (!row) continue; // not in this account's library view — skip rather than guess a URL
        additions.push(toPlayerSong(row, artistOf(row)));
        queued.add(id);
      }

      if (additions.length === 0) {
        // An empty library view means the rows have not arrived yet (a restored queue can run
        // dry seconds after a reload), not that the station has nothing left — asking again
        // later is right; calling it exhausted would silence it until the next deliberate pick.
        if (rows.size > 0) radioExhausted = true;
        return false;
      }

      queue = [...queue, ...additions];
      refreshActionHandlers(); // a next track exists now, so the OS control lights up
      return true;
    } catch {
      // A failed top-up is not worth a toast: the user asked to play a song, not to run a radio.
      // It does end the station though, rather than re-asking on every `ended` — the anonymous
      // share viewer has no radio to reach at all, and a Next button that stays lit and does
      // nothing is worse than one that goes out. Picking another track revives it.
      radioExhausted = true;
      return false;
    } finally {
      radioTopUp = null;
    }
  })();

  return radioTopUp;
}

function playPrevious() {
  if (queueIndex <= 0) return;
  queueIndex -= 1;
  void loadAndPlay(queue[queueIndex]);
}

function pause() {
  audioEl?.pause();
  isPlaying = false;
}

function resume() {
  ensureAudioEl();
  attemptPlay();
}

function togglePlay() {
  if (isPlaying) pause();
  else resume();
}

function seek(time: number) {
  if (audioEl) {
    audioEl.currentTime = time;
    currentTime = time;
    updatePositionState();
    persistPlayback(); // a seek while paused is the one position change the rAF loop never sees
  }
}

function setVolume(vol: number) {
  const clamped = Math.max(0, Math.min(1, vol));
  if (audioEl) audioEl.volume = clamped;
  volumeState = clamped;
  if (clamped > 0) lastNonZeroVolume = clamped;
}

function setPlaybackRate(rate: number) {
  const clamped = Math.max(0.25, Math.min(2, rate));
  playbackRateState = clamped;
  if (audioEl) {
    audioEl.defaultPlaybackRate = clamped;
    audioEl.playbackRate = clamped;
  }
  updatePositionState();
}

/** Mute, or restore the pre-mute level (falling back to 0.8 if muted from 0). */
function toggleMute() {
  if (volumeState > 0) setVolume(0);
  else setVolume(lastNonZeroVolume > 0 ? lastNonZeroVolume : 0.8);
}

/**
 * Dismiss the MiniPlayer bar: pause playback and hide the chrome, keeping the
 * current song and queue so play resumes exactly where the user left off. This
 * is what the bar's close (X) affordance calls — it must never destroy state.
 */
function dismissMiniPlayer() {
  pause();
  miniPlayerDismissed = true;
}

function stop() {
  if (audioEl) {
    audioEl.pause();
    // Detach the source without assigning '' (an empty string resolves to the
    // page URL and fires a spurious `error` event / "Playback failed" toast).
    audioEl.removeAttribute('src');
    audioEl.load();
  }
  currentSong = null;
  isPlaying = false;
  currentTime = 0;
  duration = 0;
  queue = [];
  queueIndex = -1;
  radioSeedId = null;
  radioExhausted = false;
  miniPlayerDismissed = false;
  const ms = mediaSession();
  if (ms) {
    ms.metadata = null;
    ms.playbackState = 'none';
  }
}

/**
 * Mark the in-page/global detail panel as mounted so the MiniPlayer hides while
 * it's open. Callers register from an `$effect` (SongDetailHost), so the
 * increment/decrement are `untrack`ed: `panelMountedCount += 1` reads the state,
 * and without untrack that read becomes a dependency of the caller's effect —
 * the subsequent write then re-fires it forever (effect_update_depth_exceeded).
 * Untracking the read keeps writes notifying subscribers (the MiniPlayer) while
 * making this safe to call from any reactive context.
 */
function registerPanel(): () => void {
  untrack(() => (panelMountedCount += 1));
  return () => {
    untrack(() => (panelMountedCount = Math.max(0, panelMountedCount - 1)));
  };
}

// ── Surviving a reload ─────────────────────────────────────────────────────
// A reload destroys the document and the audio element with it, so the store
// keeps a per-tab snapshot (queue, index, position, volume, playing) in
// sessionStorage and puts it back on boot. Two writers: a Svelte effect that
// fires on any change to the state the snapshot carries (a new song, a queue
// top-up, pause, volume, dismiss, stop) and a position writer — the rAF loop
// every few seconds plus the unload hook for the exact second a reload hits.
// Position is deliberately NOT tracked by the effect: `currentTime` commits at
// ~10 Hz while playing and serialising the queue that often is pointless work.

/** The account the snapshot is written for; null until the app layout opts in. */
let persistUserId: string | null = null;
let persistenceStarted = false;

function playbackStorage(): Storage | null {
  if (!browser) return null;
  try {
    return window.sessionStorage;
  } catch {
    return null; // storage access itself can throw under strict privacy settings
  }
}

/**
 * Compose the snapshot from live state. Reads the reactive fields directly so
 * the persistence effect below tracks exactly the set it should; the position
 * comes off the element (a plain DOM read) rather than the reactive mirror.
 */
function composePlaybackSnapshot(): PlaybackSnapshot | null {
  if (!persistUserId || !currentSong || queueIndex < 0) return null;
  return {
    v: 1,
    userId: persistUserId,
    queue: $state.snapshot(queue),
    queueIndex,
    position: audioEl?.currentTime ?? 0,
    wasPlaying: isPlaying,
    volume: volumeState,
    radioSeedId,
    radioExhausted,
    miniPlayerDismissed,
    savedAt: Date.now()
  };
}

/** Write the snapshot now (or clear it when nothing is loaded). No-op until persistence is on. */
function persistPlayback() {
  if (!persistUserId) return;
  writePlaybackSnapshot(playbackStorage(), untrack(composePlaybackSnapshot));
}

function startPersistence() {
  if (persistenceStarted || !browser) return;
  persistenceStarted = true;

  $effect.root(() => {
    $effect(() => {
      const snapshot = composePlaybackSnapshot(); // tracked reads
      untrack(() => writePlaybackSnapshot(playbackStorage(), snapshot));
    });
  });

  // `pagehide` is the reload/close moment; `visibilitychange` covers mobile
  // browsers that discard a background tab without ever firing it.
  window.addEventListener('pagehide', persistPlayback);
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'hidden') persistPlayback();
  });
}

/**
 * Put a snapshot written by this account back onto the store and, when the
 * browser allows a fresh document to start audio, resume where it stopped.
 * Live state wins: a song already loaded (a soft navigation into the app from
 * the share page, say) is never replaced by a stored one.
 */
function restorePlayback(userId: string) {
  const storage = playbackStorage();
  const snapshot = readPlaybackSnapshot(storage);
  if (!snapshot) return;
  if (snapshot.userId !== userId) {
    writePlaybackSnapshot(storage, null); // another account's queue — never inherit it
    return;
  }
  if (untrack(() => currentSong) !== null) return;
  const el = ensureAudioEl();
  if (!el) return;

  const song = snapshot.queue[snapshot.queueIndex];
  queue = snapshot.queue;
  queueIndex = snapshot.queueIndex;
  radioSeedId = snapshot.radioSeedId;
  radioExhausted = snapshot.radioExhausted;
  miniPlayerDismissed = snapshot.miniPlayerDismissed;
  setVolume(snapshot.volume);

  loadGeneration += 1; // supersede any play that was somehow already in flight
  currentSong = song;
  currentTime = snapshot.position;
  duration = 0;
  updateMediaMetadata(song);
  refreshActionHandlers();
  el.src = song.streamUrl;
  el.load();
  // Before metadata arrives this sets the default playback start position, which the element
  // seeks to as soon as it can — so the paused bar shows the right second and a later play
  // starts there, without waiting on `loadedmetadata` ourselves.
  el.currentTime = snapshot.position;
  // No `reportPlay` here: coming back to a track is not another listen of it.

  if (!canAutoResume(snapshot, Date.now())) return;
  void el
    .play()
    .then(() => (isPlaying = true))
    .catch((err: unknown) => {
      isPlaying = false;
      if (err instanceof DOMException && err.name === 'NotAllowedError') {
        // The browser wants a click before a fresh document may make sound; the toast's
        // action is exactly that click, so playback continues from the same second.
        toast('Playback paused by the reload', {
          description: 'Your browser needs a click before audio can continue.',
          action: { label: 'Resume', onClick: () => resume() }
        });
      }
    });
}

/**
 * Warm up the store-owned audio element for the session. Safe to call multiple
 * times and on the server (no-op until `browser`). Call once from the app
 * layout so `ended`/`error` are wired even before the first play.
 *
 * Passing the signed-in account's id turns on the reload snapshot for that
 * account: the last one is restored (if it was written by the same account)
 * and every change from here on is written back. Callers outside the app
 * shell (the anonymous share page) leave it off — its stream URLs carry a
 * share token and belong to nobody's library.
 */
export function initPlayer(userId?: string): void {
  ensureAudioEl();
  if (!userId || !browser || persistUserId === userId) return;
  persistUserId = userId;
  restorePlayback(userId);
  startPersistence();
}

export const playerStore = {
  get currentSong() {
    return currentSong;
  },
  get isPlaying() {
    return isPlaying;
  },
  get currentTime() {
    return currentTime;
  },
  get duration() {
    return duration;
  },
  get volume() {
    return volumeState;
  },
  get playbackRate() {
    return playbackRateState;
  },
  get hasNext() {
    return canAdvance();
  },
  get hasPrevious() {
    return queueIndex > 0;
  },
  get isPanelMounted() {
    return panelMountedCount > 0;
  },
  get isMiniPlayerDismissed() {
    return miniPlayerDismissed;
  },
  playSong,
  playNext,
  playPrevious,
  pause,
  resume,
  togglePlay,
  seek,
  setVolume,
  setPlaybackRate,
  toggleMute,
  dismissMiniPlayer,
  stop,
  registerPanel
};
