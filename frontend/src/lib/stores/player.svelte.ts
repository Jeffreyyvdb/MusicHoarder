import { browser } from '$app/environment';
import { toast } from 'svelte-sonner';

export interface PlayerSong {
  id: number;
  title: string;
  artist: string;
  streamUrl: string;
}

let currentSong = $state<PlayerSong | null>(null);
let isPlaying = $state(false);
let currentTime = $state(0);
let duration = $state(0);
let volumeState = $state(1);
let detailsRequestId = $state(0);
/**
 * Set to true while the in-page TrackPanel is mounted with its own waveform
 * player. The global MiniPlayer hides itself when this is true to avoid
 * stacking two bottom-anchored controls.
 */
let panelMountedCount = $state(0);

let audioEl: HTMLAudioElement | null = null;
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

  el.addEventListener('loadedmetadata', () => (duration = el.duration));
  el.addEventListener('ended', () => {
    stopRaf();
    isPlaying = false;
    if (Number.isFinite(el.duration)) currentTime = el.duration; // land the bar at 100%
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
    startRaf();
  });
  el.addEventListener('pause', () => {
    stopRaf();
    isPlaying = false;
    // The rAF stops here, so commit the exact paused position (the throttled
    // loop may have last written it up to TIME_WRITE_INTERVAL_MS ago).
    currentTime = el.currentTime;
  });

  audioEl = el;
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

async function playSong(song: PlayerSong) {
  if (!ensureAudioEl() || !audioEl) return;

  if (currentSong?.id === song.id) {
    if (audioEl.paused) {
      attemptPlay();
    } else {
      audioEl.pause();
      isPlaying = false;
    }
    return;
  }

  try {
    const controller = new AbortController();
    const res = await fetch(song.streamUrl, { signal: controller.signal });
    if (!res.ok) {
      let description = 'The audio file could not be found on the server.';
      try {
        const data = (await res.json()) as { message?: string };
        if (data.message) description = data.message;
      } catch {
        // ignore JSON parse errors
      }
      toast.error('Unable to play track', { description });
      return;
    }
    controller.abort();
  } catch (err: unknown) {
    if (err instanceof DOMException && err.name === 'AbortError') {
      // expected — we aborted the pre-check after confirming availability
    } else {
      toast.error('Unable to play track', { description: 'Could not connect to the server.' });
      return;
    }
  }

  currentSong = song;
  currentTime = 0;
  duration = 0;
  audioEl.src = song.streamUrl;
  audioEl.load();
  attemptPlay();
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
  }
}

function setVolume(vol: number) {
  if (audioEl) audioEl.volume = vol;
  volumeState = vol;
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
}

function requestShowDetails() {
  detailsRequestId += 1;
}

function registerPanel(): () => void {
  panelMountedCount += 1;
  return () => {
    panelMountedCount = Math.max(0, panelMountedCount - 1);
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
  get detailsRequestId() {
    return detailsRequestId;
  },
  get isPanelMounted() {
    return panelMountedCount > 0;
  },
  playSong,
  pause,
  resume,
  togglePlay,
  seek,
  setVolume,
  stop,
  requestShowDetails,
  registerPanel
};
