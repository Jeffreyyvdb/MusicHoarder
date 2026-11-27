import { catalogFullUrl, catalogPreviewUrl, resolveCatalogFullSong } from '#lib/api-client.ts';
import { isNativeShell } from '#lib/native/shell.ts';
import { localPlayback } from '#lib/stores/player.svelte.ts';

// Listening to a search result before adding it. This is deliberately not the main player: it has
// no queue, no play counts and no Connect session, so it owns one bare audio element and borrows
// only the main player's plain local pause (never a play intent, which would claim the session).

export type PreviewState = 'idle' | 'loading' | 'playing' | 'paused' | 'error';
export type PreviewSource = 'clip' | 'full';

/** Whether the whole song can be played: the YouTube lookup is a warm-up that may still fail. */
export type FullSongState =
  | { status: 'resolving' }
  | { status: 'ready' }
  | { status: 'failed'; reason: string };

export interface PreviewTrack {
  deezerId: string;
  title: string;
  artist: string;
}

let audio: HTMLAudioElement | null = null;

let state = $state<PreviewState>('idle');
let source = $state<PreviewSource>('clip');
let track = $state.raw<PreviewTrack | null>(null);
let full = $state<FullSongState>({ status: 'resolving' });
let position = $state(0);
let duration = $state(0);

/** Tracks whose YouTube lookup succeeded, so replaying one does not wait again. */
const resolved = new Set<string>();

/**
 * In the app shell the web view shares the app's audio session with the native player, and page
 * audio there took the session from it. Previews are simply not offered.
 */
export function previewAvailable(): boolean {
  return !isNativeShell();
}

function reset(): void {
  position = 0;
  duration = 0;
}

function failureReason(err: unknown): string {
  const code = err instanceof Error && 'code' in err ? (err as { code: unknown }).code : null;
  if (code === 'youtube_unavailable') return 'The full song is unavailable right now.';
  if (code === 'not_found') return 'No full version found.';
  return err instanceof Error && err.message ? err.message : 'The full song could not be found.';
}

function ensureAudio(): HTMLAudioElement {
  if (audio) return audio;
  const el = new Audio();
  el.preload = 'none';
  el.addEventListener('playing', () => (state = 'playing'));
  el.addEventListener('waiting', () => {
    if (state === 'playing') state = 'loading';
  });
  el.addEventListener('pause', () => {
    // The event arrives a task late: a track started since then has the element playing again.
    if (el.paused && (state === 'playing' || state === 'loading')) state = 'paused';
  });
  el.addEventListener('ended', () => {
    state = 'paused';
    position = 0;
  });
  el.addEventListener('error', () => {
    if (track) state = 'error';
  });
  el.addEventListener('timeupdate', () => {
    position = el.currentTime;
    duration = Number.isFinite(el.duration) ? el.duration : 0;
  });
  audio = el;

  return el;
}

function load(el: HTMLAudioElement, url: string): void {
  reset();
  state = 'loading';
  el.src = url;
  el.play().catch((err: unknown) => {
    // A newer load interrupts this one: that is not a failure.
    if (err instanceof DOMException && err.name === 'AbortError') return;
    state = 'error';
  });
}

function warmFull(forTrack: PreviewTrack): void {
  if (resolved.has(forTrack.deezerId)) {
    full = { status: 'ready' };
    return;
  }
  full = { status: 'resolving' };
  resolveCatalogFullSong(forTrack.deezerId).then(
    () => {
      resolved.add(forTrack.deezerId);
      if (track?.deezerId === forTrack.deezerId) full = { status: 'ready' };
    },
    (err: unknown) => {
      if (track?.deezerId === forTrack.deezerId)
        full = { status: 'failed', reason: failureReason(err) };
    }
  );
}

/** Start a track's clip, or pause/resume it when it is already the current one. */
function toggle(next: PreviewTrack): void {
  if (!previewAvailable()) return;
  if (track?.deezerId === next.deezerId) {
    togglePause();
    return;
  }
  const el = ensureAudio();
  if (localPlayback.playing() || localPlayback.loading()) localPlayback.pause();
  track = next;
  source = 'clip';
  load(el, catalogPreviewUrl(next.deezerId));
  warmFull(next);
}

function togglePause(): void {
  if (!audio || !track) return;
  if (state === 'playing' || state === 'loading') {
    audio.pause();
    return;
  }
  if (localPlayback.playing() || localPlayback.loading()) localPlayback.pause();
  if (state === 'error') {
    load(
      audio,
      source === 'full' ? catalogFullUrl(track.deezerId) : catalogPreviewUrl(track.deezerId)
    );
    return;
  }
  state = 'loading';
  audio.play().catch(() => (state = 'error'));
}

/** Switch the current track from its clip to the whole song, from the start. */
function playFull(): void {
  if (!audio || !track || full.status !== 'ready') return;
  source = 'full';
  load(audio, catalogFullUrl(track.deezerId));
}

function stop(): void {
  audio?.pause();
  track = null;
  state = 'idle';
  source = 'clip';
  reset();
}

/**
 * The main player started a song: end the preview so the two never overlap. The page calls this
 * from an effect on `localPlayback.isPlaying`, because module-level effects would outlive it.
 */
function yieldToMainPlayer(): void {
  if (state !== 'idle') stop();
}

/** Leave the page: stop and let go of the element. */
function release(): void {
  stop();
  if (audio) {
    audio.removeAttribute('src');
    audio.load();
    audio = null;
  }
}

export const catalogPreview = {
  get state() {
    return state;
  },
  get source() {
    return source;
  },
  get track() {
    return track;
  },
  get full() {
    return full;
  },
  get position() {
    return position;
  },
  get duration() {
    return duration;
  },
  toggle,
  togglePause,
  playFull,
  stop,
  yieldToMainPlayer,
  release
};
