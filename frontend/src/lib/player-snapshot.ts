import type { PlayerSong } from '$lib/stores/player.svelte';

/**
 * What the audio player remembers across a page reload.
 *
 * A reload tears the document down, and the `<audio>` element with it, so the music cannot
 * literally keep going. What it can do is come back where it was: same queue, same track, same
 * second, same volume, and — when the browser lets a fresh document start audio — playing again
 * before the first paint has settled. The store writes this to `sessionStorage` (per tab, so two
 * tabs do not fight over one entry and a new tab starts silent) and reads it back on boot.
 *
 * Kept as a plain module with the storage passed in, so the shape and its validation are
 * testable without a browser or the rune-backed store.
 */
export interface PlaybackSnapshot {
  v: typeof SNAPSHOT_VERSION;
  /** The account that wrote it. A different account signing in must never inherit a queue. */
  userId: string;
  queue: PlayerSong[];
  queueIndex: number;
  /** Seconds into the current track. */
  position: number;
  wasPlaying: boolean;
  volume: number;
  radioSeedId: number | null;
  radioExhausted: boolean;
  miniPlayerDismissed: boolean;
  /** Epoch milliseconds; see {@link canAutoResume}. */
  savedAt: number;
}

export const PLAYBACK_SNAPSHOT_KEY = 'mh:playback';
const SNAPSHOT_VERSION = 1;

/**
 * How stale a "was playing" snapshot may be and still start playback on its own. A reload
 * writes and reads within a second or two; anything older is a tab the browser restored after
 * a restart or a crash, where music starting unasked would be a surprise — that case comes back
 * paused at its position instead.
 */
export const AUTO_RESUME_WINDOW_MS = 60_000;

type StorageLike = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>;

export function readPlaybackSnapshot(storage: StorageLike | null): PlaybackSnapshot | null {
  if (!storage) return null;
  try {
    return parsePlaybackSnapshot(storage.getItem(PLAYBACK_SNAPSHOT_KEY));
  } catch {
    return null; // storage disabled / private mode
  }
}

/** Write the snapshot, or remove the entry when there is nothing to come back to. */
export function writePlaybackSnapshot(
  storage: StorageLike | null,
  snapshot: PlaybackSnapshot | null
): void {
  if (!storage) return;
  try {
    if (snapshot) storage.setItem(PLAYBACK_SNAPSHOT_KEY, JSON.stringify(snapshot));
    else storage.removeItem(PLAYBACK_SNAPSHOT_KEY);
  } catch {
    // best-effort: a full or disabled store just means the next reload starts silent
  }
}

export function clearPlaybackSnapshot(storage: StorageLike | null): void {
  writePlaybackSnapshot(storage, null);
}

/** True when a restored snapshot should start playing without waiting for a click. */
export function canAutoResume(snapshot: PlaybackSnapshot, now: number): boolean {
  if (!snapshot.wasPlaying || snapshot.miniPlayerDismissed) return false;
  const age = now - snapshot.savedAt;
  return age >= 0 && age <= AUTO_RESUME_WINDOW_MS;
}

/**
 * Parse and validate a stored value. Anything that is not exactly the shape this version writes
 * yields `null` — an older or hand-edited entry is worth nothing, not a half-restored queue.
 */
export function parsePlaybackSnapshot(raw: string | null): PlaybackSnapshot | null {
  if (!raw) return null;
  let value: unknown;
  try {
    value = JSON.parse(raw);
  } catch {
    return null;
  }
  if (!isRecord(value) || value.v !== SNAPSHOT_VERSION) return null;
  if (typeof value.userId !== 'string' || value.userId.length === 0) return null;
  if (!Array.isArray(value.queue) || value.queue.length === 0) return null;
  const queue: PlayerSong[] = [];
  for (const entry of value.queue) {
    const song = parseSong(entry);
    if (!song) return null;
    queue.push(song);
  }
  const queueIndex = value.queueIndex;
  if (!Number.isInteger(queueIndex) || (queueIndex as number) < 0) return null;
  if ((queueIndex as number) >= queue.length) return null;
  if (!isFiniteNonNegative(value.position)) return null;
  if (!isFiniteNonNegative(value.volume) || value.volume > 1) return null;
  if (typeof value.wasPlaying !== 'boolean') return null;
  if (typeof value.radioExhausted !== 'boolean') return null;
  if (typeof value.miniPlayerDismissed !== 'boolean') return null;
  if (value.radioSeedId !== null && !Number.isFinite(value.radioSeedId)) return null;
  if (!Number.isFinite(value.savedAt)) return null;

  return {
    v: SNAPSHOT_VERSION,
    userId: value.userId,
    queue,
    queueIndex: queueIndex as number,
    position: value.position,
    wasPlaying: value.wasPlaying,
    volume: value.volume,
    radioSeedId: value.radioSeedId as number | null,
    radioExhausted: value.radioExhausted,
    miniPlayerDismissed: value.miniPlayerDismissed,
    savedAt: value.savedAt as number
  };
}

function parseSong(entry: unknown): PlayerSong | null {
  if (!isRecord(entry)) return null;
  if (!Number.isFinite(entry.id)) return null;
  if (typeof entry.title !== 'string' || typeof entry.artist !== 'string') return null;
  // Stream URLs are same-origin paths (`/api/mh/songs/{id}/stream`); a stored absolute URL is
  // not something this app ever wrote, so it is not something it should play.
  if (typeof entry.streamUrl !== 'string' || !entry.streamUrl.startsWith('/')) return null;
  if (!isOptionalString(entry.coverUrl) || !isOptionalString(entry.album)) return null;
  return {
    id: entry.id as number,
    title: entry.title,
    artist: entry.artist,
    streamUrl: entry.streamUrl,
    coverUrl: entry.coverUrl as string | null | undefined,
    album: entry.album as string | null | undefined
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isOptionalString(value: unknown): boolean {
  return value === undefined || value === null || typeof value === 'string';
}

function isFiniteNonNegative(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0;
}
