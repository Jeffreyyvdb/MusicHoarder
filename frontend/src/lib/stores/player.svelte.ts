import { untrack } from 'svelte';
import { browser } from '$app/environment';
import { toast } from 'svelte-sonner';
import { coverThumbUrl } from '$lib/api-client';

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
 * Ordered playback context the current song was started from (an album's
 * tracks, a review list, etc.). `queueIndex` points at `currentSong` within it.
 * When a song ends we advance to `queue[queueIndex + 1]`; there is no wrap, so
 * playback simply stops after the last track.
 */
let queue = $state<PlayerSong[]>([]);
let queueIndex = $state(-1);
/**
 * Set to true while the in-page TrackPanel is mounted with its own waveform
 * player. The global MiniPlayer hides itself when this is true to avoid
 * stacking two bottom-anchored controls.
 */
let panelMountedCount = $state(0);

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

function startRaf() {
  if (rafHandle !== null) return;
  lastTimeWrite = 0;
  const tick = (now: number) => {
    if (audioEl && now - lastTimeWrite >= TIME_WRITE_INTERVAL_MS) {
      lastTimeWrite = now;
      currentTime = audioEl.currentTime;
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
    playbackRate: 1
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
  set('nexttrack', queueIndex >= 0 && queueIndex < queue.length - 1 ? () => playNext() : null);
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
  if (queueIndex < 0 || queueIndex >= queue.length - 1) return;
  queueIndex += 1;
  void loadAndPlay(queue[queueIndex]);
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
  }
}

function setVolume(vol: number) {
  const clamped = Math.max(0, Math.min(1, vol));
  if (audioEl) audioEl.volume = clamped;
  volumeState = clamped;
  if (clamped > 0) lastNonZeroVolume = clamped;
}

/** Mute, or restore the pre-mute level (falling back to 0.8 if muted from 0). */
function toggleMute() {
  if (volumeState > 0) setVolume(0);
  else setVolume(lastNonZeroVolume > 0 ? lastNonZeroVolume : 0.8);
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

/**
 * Warm up the store-owned audio element for the session. Safe to call multiple
 * times and on the server (no-op until `browser`). Call once from the app
 * layout so `ended`/`error` are wired even before the first play.
 */
export function initPlayer(): void {
  ensureAudioEl();
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
  get hasNext() {
    return queueIndex >= 0 && queueIndex < queue.length - 1;
  },
  get hasPrevious() {
    return queueIndex > 0;
  },
  get isPanelMounted() {
    return panelMountedCount > 0;
  },
  playSong,
  playNext,
  playPrevious,
  pause,
  resume,
  togglePlay,
  seek,
  setVolume,
  toggleMute,
  stop,
  registerPanel
};
