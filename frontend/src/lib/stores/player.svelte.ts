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

async function playSong(song: PlayerSong) {
  if (!audioEl) return;

  if (currentSong?.id === song.id) {
    if (audioEl.paused) {
      void audioEl
        .play()
        .then(() => (isPlaying = true))
        .catch(() => (isPlaying = false));
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
  void audioEl
    .play()
    .then(() => (isPlaying = true))
    .catch(() => (isPlaying = false));
}

function pause() {
  audioEl?.pause();
  isPlaying = false;
}

function resume() {
  void audioEl
    ?.play()
    .then(() => (isPlaying = true))
    .catch(() => (isPlaying = false));
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
    audioEl.src = '';
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

export function attachAudioElement(el: HTMLAudioElement): () => void {
  audioEl = el;

  const onLoadedMetadata = () => (duration = el.duration);
  const onEnded = () => {
    stopRaf();
    isPlaying = false;
  };
  const onError = () => {
    stopRaf();
    isPlaying = false;
    const song = currentSong;
    if (song) {
      toast.error('Playback failed', { description: `Could not play "${song.title}".` });
    }
  };
  const onPlay = () => {
    isPlaying = true;
    startRaf();
  };
  const onPause = () => {
    stopRaf();
    isPlaying = false;
  };

  el.addEventListener('loadedmetadata', onLoadedMetadata);
  el.addEventListener('ended', onEnded);
  el.addEventListener('error', onError);
  el.addEventListener('play', onPlay);
  el.addEventListener('pause', onPause);

  return () => {
    stopRaf();
    el.removeEventListener('loadedmetadata', onLoadedMetadata);
    el.removeEventListener('ended', onEnded);
    el.removeEventListener('error', onError);
    el.removeEventListener('play', onPlay);
    el.removeEventListener('pause', onPause);
    if (audioEl === el) audioEl = null;
  };
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
