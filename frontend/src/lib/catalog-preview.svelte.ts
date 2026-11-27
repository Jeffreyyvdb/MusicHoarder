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
 * The full song is looked up once a clip has played this long, not on the tap: each lookup is a
 * yt-dlp run on the server, and skimming a list would start one per row.
 */
export const WARM_AFTER_SECONDS = 2;
let warmed: string | null = null;

/** Bumped by every load and stop, so a late play() rejection from an earlier one changes nothing. */
let generation = 0;

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
  // A released element can still fire late events; only the current one may change the state.
  const on = (type: string, handler: () => void) =>
    el.addEventListener(type, () => {
      if (el === audio) handler();
    });
  on('playing', () => (state = 'playing'));
  on('waiting', () => {
    if (state === 'playing') state = 'loading';
  });
  on('pause', () => {
    // The event arrives a task late: a track started since then has the element playing again.
    if (el.paused && (state === 'playing' || state === 'loading')) state = 'paused';
  });
  on('ended', () => {
    state = 'paused';
    position = 0;
  });
  on('error', () => {
    if (track) state = 'error';
  });
  on('timeupdate', () => {
    position = el.currentTime;
    duration = Number.isFinite(el.duration) ? el.duration : 0;
    if (
      track &&
      source === 'clip' &&
      warmed !== track.deezerId &&
      el.currentTime >= WARM_AFTER_SECONDS
    ) {
      warmed = track.deezerId;
      warmFull(track);
    }
  });
  audio = el;

  return el;
}

/** play(), whose rejection counts only while nothing newer has happened and it is not an interruption. */
function startPlaying(el: HTMLAudioElement): void {
  const mine = generation;
  el.play().catch((err: unknown) => {
    if (mine !== generation || !track) return;
    if (err instanceof DOMException && err.name === 'AbortError') return;
    state = 'error';
  });
}

function load(el: HTMLAudioElement, url: string): void {
  generation++;
  reset();
  state = 'loading';
  el.src = url;
  startPlaying(el);
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
  full = resolved.has(next.deezerId) ? { status: 'ready' } : { status: 'resolving' };
  load(el, catalogPreviewUrl(next.deezerId));
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
  generation++;
  state = 'loading';
  startPlaying(audio);
}

/** Switch the current track from its clip to the whole song, from the start. */
function playFull(): void {
  if (!audio || !track || full.status !== 'ready') return;
  source = 'full';
  load(audio, catalogFullUrl(track.deezerId));
}

function stop(): void {
  generation++;
  warmed = null;
  audio?.pause();
  track = null;
  state = 'idle';
  source = 'clip';
  reset();
}

/**
 * The main player started a song: end the preview so the two never overlap. The command palette
 * calls this from an effect on `localPlayback.isPlaying`; a module-level effect has no owner here.
 */
function yieldToMainPlayer(): void {
  if (state !== 'idle') stop();
}

/** The palette closed: stop and let go of the element. */
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
