import { untrack } from 'svelte';
import { browser } from '$app/environment';
import { toast } from 'svelte-sonner';
import { coverThumbUrl, fetchRadio, reportSongPlayed, toPlayerSong } from '$lib/api-client';
import {
  convertedStreamUrl,
  createUnplayableFormats,
  needsConversion,
  type UnplayableFormats
} from '$lib/audio-formats';
import {
  canAutoResume,
  readPlaybackSnapshot,
  writePlaybackSnapshot,
  type PlaybackSnapshot
} from '$lib/player-snapshot';
import { previousAction } from '$lib/player-seek';
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
  /**
   * The file's format ('opus', see `formatOf`), when known. Decides whether this browser gets the
   * file as it is or the server's decoded stream of it; unknown means the original is tried first.
   */
  format?: string | null;
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
 * True after the user hides the MiniPlayer ("Hide player" in its menu, in Now Playing's ⋯ menu,
 * or the md+ bar's close). Hiding leaves playback running and keeps `currentSong`/`queue`
 * intact — the lock screen / Control Center controls keep working, and pressing play anywhere
 * (row, panel, OS media keys) clears the flag so the bar comes back with its full state. Only
 * `stop()` tears state down.
 */
let miniPlayerDismissed = $state(false);
/**
 * Whether an AirPlay receiver is in reach. Only Safari reports it (the
 * `webkitplaybacktargetavailabilitychanged` event on the media element), so everywhere else this
 * stays false and Now Playing shows no route button — a button that opens nothing is worse than
 * none.
 */
let airPlayAvailable = $state(false);

let audioEl: HTMLAudioElement | null = null;
/** Whether the loaded source is the server's decoded stream rather than the file as it is. */
let sourceIsConverted = false;
/**
 * Whether the listener wants sound: set by every play intent, cleared by pause. A fallback to the
 * decoded stream keeps it, so a paused song stays paused and a playing one carries on.
 */
let wantsPlayback = false;
/** A format that looks unplayable here, until its decoded stream loads (see `fallBackToConverted`). */
let suspectedFormat: {
  songId: number;
  format: string;
  originalReachable: Promise<boolean>;
} | null = null;
let unplayable: UnplayableFormats | null = null;
/**
 * The second the loaded stream last played at, off `timeupdate`: where a reload resumes, and what
 * tells the loop coming round from a seek (see `cameRoundToStart`). The reactive `currentTime`
 * cannot serve, because it is written by the rAF loop, which does not run while the page is hidden.
 */
let lastPlayedPosition = 0;
/**
 * Whether the loaded source has produced audio — what makes a later `waiting` a stall, and a jump
 * back to its start the loop coming round.
 */
let sourceHasPlayed = false;
/**
 * Retries spent on the loaded track — reloads of its stream, or, once it has ended, asks of the
 * station for what follows — and the second the last reload resumed from.
 */
let trackRetries = 0;
let retriedFrom = 0;
/** Tracks skipped for failing since audio last played. */
let skipsInARow = 0;
/** Set once the player gave up on a stream, so the next Play reloads it instead of poking it. */
let sourceNeedsReload = false;
let recoveryTimer: ReturnType<typeof setTimeout> | null = null;
let stallTimer: ReturnType<typeof setTimeout> | null = null;
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
  // Previous is live whenever a track is loaded: at the head of the queue it restarts the track
  // (see playPrevious), so greying it out there would hide a working control.
  set('previoustrack', queueIndex >= 0 ? () => playPrevious() : null);
  set('nexttrack', canAdvance() ? () => playNext() : null);
  set('seekto', (details) => {
    if (typeof details.seekTime === 'number') seek(details.seekTime);
  });
  // No `seekbackward` / `seekforward`: iOS shows either the track pair or the seek pair, never
  // both, and picks the seek pair whenever it is registered, which puts ±10s buttons where
  // previous/next belong on the lock screen, Control Center and Dynamic Island.
}

function setPlaybackState(state: MediaSessionPlaybackState) {
  const ms = mediaSession();
  if (ms) ms.playbackState = state;
}

// ── Background playback ────────────────────────────────────────────────────
// An installed iOS Home Screen app keeps playing after it leaves the foreground
// only while its audio session is in the long-form `playback` category, the one
// Spotify uses. Under the Audio Session API's default `auto` type WebKit infers
// that category from whatever is audible at each moment, and after ~2s with
// nothing audible (between two tracks, a stream still buffering) it lets the
// category lapse. Declaring `playback` pins it. Claimed on each play intent
// rather than at boot, so opening the app claims nothing; feature-detected, so
// browsers without `navigator.audioSession` skip it.

type AudioSessionNavigator = Navigator & { audioSession?: { type: string } };

function claimPlaybackAudioSession() {
  if (!browser) return;
  const session = (navigator as AudioSessionNavigator).audioSession;
  if (!session || session.type === 'playback') return;
  try {
    session.type = 'playback';
  } catch {
    // Refused by this engine — `auto` still plays, just without the pin.
  }
}

// ── A song never ends ──────────────────────────────────────────────────────
// The pin does not survive the end of a song. When an audio element plays to its end and nothing
// else is playing, WebKit deactivates the audio session (`sessionWillEndPlayback` in
// MediaSessionManagerCocoa.mm, since iOS 17), whatever type the page declared. A foreground app
// gets it back with the next `play()`; an app in the background does not, because iOS will not
// activate a session for an app that is not already playing. So the next song waits, silent,
// until the app is opened again, while the lock screen shows it paused (WebKit bugs 261858 and
// 267606). A pause or a seek deactivates nothing in the background, hence the loop: a song
// reaching its end is, to WebKit, a seek back to its start, and that jump is where this store
// moves on, pausing first so the restarted song is not heard while the next one loads.

/**
 * How close to its start a jump has to land to be the loop coming round. The loop lands on 0; a
 * source still being positioned, or a seek of ours, never counts (see `cameRoundToStart`).
 */
const LOOP_START_WINDOW_S = 0.5;

/**
 * Whether the loop just brought the song back to its start: a jump from further in to its first
 * moments, on a source that has played. `seek` moves `lastPlayedPosition` along with it, so a seek
 * to the start, like Previous restarting the song, is never taken for the end.
 */
function cameRoundToStart(el: HTMLAudioElement): boolean {
  return (
    sourceHasPlayed &&
    el.currentTime < LOOP_START_WINDOW_S &&
    lastPlayedPosition >= LOOP_START_WINDOW_S
  );
}

/** The song played to its end: what an `ended` event would have meant. */
function songFinished(el: HTMLAudioElement) {
  lastPlayedPosition = 0; // where the element is now, so the jump is not seen twice
  el.pause(); // a paused element that has not ended keeps the audio session
  playNext(); // with nothing to follow, the song stays loaded, paused at its start
}

// ── Recovering a stream ────────────────────────────────────────────────────
// The pinned category keeps the audio session; it does not keep the page
// running. Once a hidden page stops being audible, WebKit holds its process
// awake for 10 more seconds (`audibleActivityClearDelay` in WebPageProxy.cpp)
// and then lets iOS suspend it, and a suspended page runs no script — nothing it
// scheduled can start the music again. So a stream that fails (a redeploy
// cutting the connection, Wi-Fi handing over to cellular, the proxy timing out)
// is reloaded from the same second, and a track that will not play is skipped,
// all inside that window. A stream stalled mid-track still counts as playing,
// which is why its watchdog can wait longer before stepping in.

/** Delay before each retry of a failed stream or station; together they leave most of the 10s window. */
const RETRY_DELAYS_MS = [1000, 3000];
/** How far a reloaded stream has to play past where it failed to earn its reloads back. */
const RETRY_BUDGET_RESET_S = 10;
/** Tracks skipped in a row before giving up: by then it is the server that is gone, not a file. */
const MAX_SKIPS_IN_A_ROW = 3;
/** How long a playing stream may wait for data before it is reloaded. */
const STALL_TIMEOUT_MS = 10_000;
/**
 * How late a recovery may fire and still act. Later than this the page was suspended in between,
 * and starting the music whenever the app is next opened — minutes or hours on — would be a
 * surprise, not a recovery; the player gives up instead, paused at the second it reached.
 */
const RECOVERY_LATE_MS = 5000;

function clearRecovery() {
  if (recoveryTimer !== null) clearTimeout(recoveryTimer);
  if (stallTimer !== null) clearTimeout(stallTimer);
  recoveryTimer = null;
  stallTimer = null;
}

/** Run `action` after `delayMs`, unless playback was paused, moved on, or slept through it. */
function afterDelay(delayMs: number, action: () => void): ReturnType<typeof setTimeout> {
  const gen = loadGeneration;
  const due = Date.now() + delayMs;
  return setTimeout(() => {
    if (!wantsPlayback || gen !== loadGeneration) return;
    if (Date.now() - due > RECOVERY_LATE_MS) stopTrying();
    else action();
  }, delayMs);
}

/** Point the element at the stream it already had, at the second it last played. */
function reloadSource(el: HTMLAudioElement, song: PlayerSong) {
  sourceHasPlayed = false;
  sourceNeedsReload = false;
  el.src = sourceIsConverted ? convertedStreamUrl(song.streamUrl) : song.streamUrl;
  el.load();
  // Before metadata arrives this is the start position, as in `fallBackToConverted`.
  if (lastPlayedPosition > 0) el.currentTime = lastPlayedPosition;
}

/**
 * The stream failed, or stopped delivering, while the listener wants sound: reload it from the
 * same second a couple of times, then move on to the next track. Returns false once both are used
 * up, which leaves the failure to be reported.
 */
function recoverStream(): boolean {
  const el = audioEl;
  const song = currentSong;
  if (!el || !song || !wantsPlayback) return false;
  if (trackRetries < RETRY_DELAYS_MS.length) {
    const delay = RETRY_DELAYS_MS[trackRetries];
    trackRetries += 1;
    retriedFrom = lastPlayedPosition;
    if (recoveryTimer !== null) clearTimeout(recoveryTimer);
    recoveryTimer = afterDelay(delay, () => {
      reloadSource(el, song);
      playElement();
    });
    return true;
  }
  if (skipsInARow >= MAX_SKIPS_IN_A_ROW || !canAdvance()) return false;
  skipsInARow += 1;
  toast.error('Skipped a track', { description: `Could not play "${song.title}".` });
  playNext();
  return true;
}

/** Out of retries, or slept through them: report it and leave the song paused where it got to. */
function stopTrying() {
  const song = currentSong;
  clearRecovery();
  wantsPlayback = false;
  sourceNeedsReload = true;
  if (audioEl && !audioEl.paused) audioEl.pause();
  isPlaying = false;
  setPlaybackState('paused');
  if (song) toast.error('Playback failed', { description: `Could not play "${song.title}".` });
}

// ── Formats this browser cannot play ───────────────────────────────────────
// A song whose file this browser cannot play as it is (Ogg Opus in Safari) streams decoded by the
// server instead; see `$lib/audio-formats`. Every other song, and every song in a browser
// that can play it, streams as the original file.

/** `MediaError` codes, spelled out because the global is absent outside a browser. */
const MEDIA_ERR_DECODE = 3;
const MEDIA_ERR_SRC_NOT_SUPPORTED = 4;
/** `HTMLMediaElement.HAVE_METADATA`, for the same reason. */
const HAVE_METADATA = 1;

function unplayableFormats(): UnplayableFormats {
  if (!unplayable) {
    let storage: Storage | null = null;
    try {
      storage = window.localStorage;
    } catch {
      // Storage blocked: the record lasts for this page only.
    }
    unplayable = createUnplayableFormats(storage, navigator.userAgent ?? '');
  }
  return unplayable;
}

/** The URL to load for `song` here: the file as it is, unless this browser cannot play it. */
function streamSourceFor(song: PlayerSong): { url: string; converted: boolean } {
  const el = audioEl;
  const canPlayType =
    el && typeof el.canPlayType === 'function' ? (type: string) => el.canPlayType(type) : null;
  return needsConversion(song.format, canPlayType, unplayableFormats())
    ? { url: convertedStreamUrl(song.streamUrl), converted: true }
    : { url: song.streamUrl, converted: false };
}

/** Point the element at `song`'s source (see `streamSourceFor`) and start loading it. */
function loadSource(el: HTMLAudioElement, song: PlayerSong) {
  const source = streamSourceFor(song);
  sourceIsConverted = source.converted;
  suspectedFormat = null;
  clearRecovery();
  lastPlayedPosition = 0;
  sourceHasPlayed = false;
  sourceNeedsReload = false;
  trackRetries = 0;
  el.src = source.url;
  el.load();
}

/**
 * After the original file failed to load or decode, load the decoded stream instead, from the same
 * second and in the same play state. Returns false when there is nothing to fall back to, which
 * leaves the failure to be reported.
 *
 * A failure before any metadata arrived may be the format rather than this file, and a format
 * this browser cannot play should send the rest of the queue straight to decoded streams. The same
 * error code reports an HTTP failure of the original (a 404, a proxy timeout), which says nothing
 * about the format, and remembering a format wrongly would convert every song in it from then on.
 * So the format is only suspected here, and remembered once the original proves reachable and the
 * decoded stream loads (`confirmSuspectedFormat`).
 */
function fallBackToConverted(): boolean {
  const el = audioEl;
  const song = currentSong;
  if (!el || !song || sourceIsConverted) return false;
  const code = el.error?.code;
  // A network failure is not the format's fault; converting would not help.
  if (code !== MEDIA_ERR_DECODE && code !== MEDIA_ERR_SRC_NOT_SUPPORTED) return false;

  suspectedFormat =
    song.format && !(duration > 0)
      ? {
          songId: song.id,
          format: song.format,
          originalReachable: fetch(song.streamUrl, { headers: { Range: 'bytes=0-0' } }).then(
            (res) => res.ok,
            () => false
          )
        }
      : null;
  const position = currentTime;
  sourceIsConverted = true;
  sourceHasPlayed = false;
  lastPlayedPosition = position;
  el.src = convertedStreamUrl(song.streamUrl);
  el.load();
  if (position > 0) el.currentTime = position;
  if (wantsPlayback) playElement();
  return true;
}

/** The decoded stream of a suspected format loaded: remember the format if its original was reachable. */
function confirmSuspectedFormat() {
  const suspect = suspectedFormat;
  if (!suspect || !sourceIsConverted || currentSong?.id !== suspect.songId) return;
  suspectedFormat = null;
  void suspect.originalReachable.then((reachable) => {
    if (reachable) unplayableFormats().add(suspect.format);
  });
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
  el.loop = true; // a song's end is the jump back to its start (see `songFinished`)
  el.volume = volumeState;
  // `defaultPlaybackRate` is what a new `src` resets `playbackRate` to, so
  // keeping both in sync makes the chosen speed survive track changes.
  el.defaultPlaybackRate = playbackRateState;
  el.playbackRate = playbackRateState;
  el.preservesPitch = true;

  if (typeof window !== 'undefined' && 'WebKitPlaybackTargetAvailabilityEvent' in window) {
    el.setAttribute('x-webkit-airplay', 'allow');
    el.addEventListener('webkitplaybacktargetavailabilitychanged', (event) => {
      airPlayAvailable = (event as Event & { availability?: string }).availability === 'available';
    });
  }

  el.addEventListener('loadedmetadata', () => {
    duration = el.duration;
    updatePositionState();
    confirmSuspectedFormat();
  });
  el.addEventListener('seeking', () => {
    if (cameRoundToStart(el)) songFinished(el);
  });
  el.addEventListener('error', () => {
    stopRaf();
    isPlaying = false;
    if (stallTimer !== null) clearTimeout(stallTimer);
    if (fallBackToConverted()) return;
    if (recoverStream()) return;
    stopTrying();
  });
  el.addEventListener('timeupdate', () => {
    // Before metadata the element reports the position it is resetting from, not a real one.
    if (el.readyState < HAVE_METADATA) return;
    // `seeking` has normally seen the loop come round already; this catches an engine that loops
    // without one, or a position read before the loop's seek was announced.
    if (cameRoundToStart(el)) {
      songFinished(el);
      return;
    }
    lastPlayedPosition = el.currentTime;
    if (trackRetries > 0 && lastPlayedPosition > retriedFrom + RETRY_BUDGET_RESET_S) {
      trackRetries = 0;
    }
  });
  el.addEventListener('playing', () => {
    sourceHasPlayed = true;
    skipsInARow = 0;
    clearRecovery(); // a stall that came back by itself needs no reload
  });
  el.addEventListener('waiting', () => {
    // Waiting on a stream that has played is a stall. One that has not is still loading, and a
    // load that fails reports itself through `error`.
    if (!wantsPlayback || !sourceHasPlayed) return;
    if (stallTimer !== null) clearTimeout(stallTimer);
    stallTimer = afterDelay(STALL_TIMEOUT_MS, () => {
      if (!el.paused && !recoverStream()) stopTrying();
    });
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
  wantsPlayback = true;
  playElement();
}

/** The element half of `attemptPlay`, which leaves a hidden mini player hidden. */
function playElement() {
  claimPlaybackAudioSession();
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

  // The pre-flight turns a missing file into a clear toast while the old song keeps playing, but
  // it is an await between the decision to play and `play()`. In the background that gap is the
  // one place a hand-off can die: once a song finishes nothing is audible, and a slow round trip
  // to the server lets iOS stop treating the app as a player before the next song starts. So the
  // swap is synchronous while the page is hidden (Home Screen, another app, the lock screen), and
  // a missing file is reported by the element's own `error` event instead.
  if (!document.hidden) {
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
  }

  currentSong = song;
  currentTime = 0;
  duration = 0;
  updateMediaMetadata(song);
  refreshActionHandlers(); // queue position may have changed (next/prev availability)
  loadSource(audioEl, song);
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

/** Make `contextQueue` (or just `song`) the queue, positioned at `song`. */
function seedQueue(song: PlayerSong, contextQueue?: PlayerSong[], index?: number) {
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
  skipsInARow = 0;
  maybePrefetchRadio();
}

/**
 * Play a song, optionally seeding the playback queue it belongs to so the
 * player can auto-advance and offer prev/next. Re-clicking the current song
 * toggles play/pause — this is the entry point for controls that show a
 * Play/Pause glyph for that song. Anything labelled Play or Shuffle, and a
 * row tap, uses `startQueue` instead. `index` defaults to the song's position
 * in `contextQueue`.
 */
async function playSong(song: PlayerSong, contextQueue?: PlayerSong[], index?: number) {
  if (!ensureAudioEl() || !audioEl) return;

  seedQueue(song, contextQueue, index);

  if (currentSong?.id === song.id) {
    if (audioEl.paused) {
      resume();
    } else {
      pause();
    }
    return;
  }

  await loadAndPlay(song);
}

/**
 * Play `contextQueue` from `index`, and never pause. The queue is always re-seeded (Play from a
 * list makes that list the queue); if the target song is the one already loaded it keeps playing,
 * or resumes when paused, rather than restarting. This is what every control labelled Play or
 * Shuffle, a row menu's Play, and the phone's row-tap rule call — none of them may toggle.
 */
async function startQueue(contextQueue: PlayerSong[], index = 0) {
  const song = contextQueue[index];
  if (!song || !ensureAudioEl() || !audioEl) return;

  seedQueue(song, contextQueue, index);

  if (currentSong?.id === song.id) {
    // A play intent even when nothing needs to start: after "Hide player", pressing Play on a
    // list that begins with the playing song must bring the bar back. attemptPlay (the other
    // place that clears this) only runs when paused.
    miniPlayerDismissed = false;
    refreshActionHandlers(); // the new queue decides whether Next is live
    if (audioEl.paused) resume();
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
    if (appended) {
      advance();
    } else if (wantsPlayback && canAdvance() && trackRetries < RETRY_DELAYS_MS.length) {
      // The station could not be asked (a redeploy, a dropped connection): ask again inside the
      // window a hidden page has before iOS suspends it, rather than ending on silence.
      if (recoveryTimer !== null) clearTimeout(recoveryTimer);
      recoveryTimer = afterDelay(RETRY_DELAYS_MS[trackRetries++], playNext);
    }
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
    } catch (err) {
      // A failed top-up is not worth a toast: the user asked to play a song, not to run a radio.
      // A refusal ends the station — the anonymous share viewer has no radio to reach at all, and
      // a Next button that stays lit and does nothing is worse than one that goes out. Picking
      // another track revives it. A request that could not be made or answered (a redeploy, a
      // dropped connection, a proxy timeout) is only this attempt, though: ending the station on
      // one of those is what left a shuffled album silent after a single blip. The next track
      // change, or the retry at the end of the queue, asks again.
      if (isRefusal(err)) radioExhausted = true;
      return false;
    } finally {
      radioTopUp = null;
    }
  })();

  return radioTopUp;
}

/** A 4xx answer, which asking again will not change — unless it was a timeout or a rate limit. */
function isRefusal(err: unknown): boolean {
  const status = (err as { status?: unknown } | null)?.status;
  return (
    typeof status === 'number' && status >= 400 && status < 500 && status !== 408 && status !== 429
  );
}

/**
 * Previous, the way every player (and the Android client) does it: a few seconds into a track it
 * restarts the track, otherwise it goes back one item; on the first item it restarts. Reads the
 * element's exact position rather than the 10 Hz mirror, so a press right at the threshold does
 * what the displayed time says. Restarting keeps the play/pause state.
 */
function playPrevious() {
  const position = audioEl?.currentTime ?? currentTime;
  const action = previousAction(position, queueIndex);
  if (action === 'none') return;
  if (action === 'restart') {
    seek(0);
    return;
  }
  queueIndex -= 1;
  void loadAndPlay(queue[queueIndex]);
}

function pause() {
  wantsPlayback = false;
  clearRecovery();
  audioEl?.pause();
  isPlaying = false;
}

function resume() {
  const el = ensureAudioEl();
  const song = currentSong;
  // A stream that failed for good cannot be played again as it is: load it afresh, from the
  // second it reached, with its retries back.
  if (el && song && (sourceNeedsReload || el.error)) {
    clearRecovery(); // this reload replaces any still pending
    trackRetries = 0;
    skipsInARow = 0;
    reloadSource(el, song);
  }
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
    lastPlayedPosition = time;
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

/** Open Safari's AirPlay route picker for the audio element (no-op where it does not exist). */
function showAirPlayPicker() {
  const el = audioEl as (HTMLAudioElement & { webkitShowPlaybackTargetPicker?: () => void }) | null;
  el?.webkitShowPlaybackTargetPicker?.();
}

/** Mute, or restore the pre-mute level (falling back to 0.8 if muted from 0). */
function toggleMute() {
  if (volumeState > 0) setVolume(0);
  else setVolume(lastNonZeroVolume > 0 ? lastNonZeroVolume : 0.8);
}

/**
 * Dismiss the MiniPlayer bar: hide the chrome only. Playback is untouched —
 * Media Session is already fully wired (see `refreshActionHandlers`), so lock
 * screen / Control Center controls keep working with the bar out of the way.
 * `attemptPlay()` clears the flag again on the next play intent (a new track,
 * resume, queue advance), which is how the bar comes back. This is what the
 * bar's close (X) affordance calls — it must never destroy state.
 */
function dismissMiniPlayer() {
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
  wantsPlayback = false;
  clearRecovery();
  sourceIsConverted = false;
  suspectedFormat = null;
  sourceNeedsReload = false;
  skipsInARow = 0;
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
  const autoResume = canAutoResume(snapshot, Date.now());
  wantsPlayback = autoResume;
  loadSource(el, song);
  // Before metadata arrives this sets the default playback start position, which the element
  // seeks to as soon as it can — so the paused bar shows the right second and a later play
  // starts there, without waiting on `loadedmetadata` ourselves.
  el.currentTime = snapshot.position;
  lastPlayedPosition = snapshot.position;
  // No `reportPlay` here: coming back to a track is not another listen of it.

  if (!autoResume) return;
  claimPlaybackAudioSession();
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
  /** True whenever a track is loaded: Previous restarts the first item rather than going dark. */
  get hasPrevious() {
    return currentSong !== null && queueIndex >= 0;
  },
  get airPlayAvailable() {
    return airPlayAvailable;
  },
  get isPanelMounted() {
    return panelMountedCount > 0;
  },
  get isMiniPlayerDismissed() {
    return miniPlayerDismissed;
  },
  playSong,
  startQueue,
  playNext,
  playPrevious,
  pause,
  resume,
  togglePlay,
  seek,
  setVolume,
  setPlaybackRate,
  toggleMute,
  showAirPlayPicker,
  dismissMiniPlayer,
  stop,
  registerPanel
};
