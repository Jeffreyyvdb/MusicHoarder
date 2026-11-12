import type { PlayerSong } from '#lib/stores/player.svelte.ts';

/**
 * What the audio player remembers across a page reload.
 *
 * A reload tears the document down, and the `<audio>` element with it, so the music cannot
 * literally keep going. What it can do is come back where it was: same queue, same track, same
 * second, same volume, and — when the browser lets a fresh document start audio — playing again
 * before the first paint has settled. The store writes this to `sessionStorage` (per tab, so two
 * tabs do not fight over one entry and a new tab starts silent) and reads it back on boot.
 *
 * It is kept as two entries. The queue can be the whole library — two million characters for
 * 10,000 songs — and it changes when someone picks something or the station appends, while the
 * position changes for as long as a song plays. Written as one value, every position write
 * serialised and stored the whole queue again: a tenth of a second of main thread on a phone,
 * every five seconds. So the queue has an entry of its own, written when the queue is a different
 * one, and everything else is a small entry that names the queue it belongs to.
 *
 * Kept as a plain module with the storage passed in, so the shape and its validation are
 * testable without a browser or the rune-backed store.
 */
export interface PlaybackSnapshot {
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

/** Everything but the queue, and which stored queue it describes. */
export const PLAYBACK_SNAPSHOT_KEY = 'mh:playback';
/** The queue, under the id the other entry names. */
export const PLAYBACK_QUEUE_KEY = 'mh:playback:queue';
const SNAPSHOT_VERSION = 2;
/**
 * The version that kept the queue inside the one entry. Still read, so the reload that brings this
 * version of the app in (the app reloads itself onto a new build) restores what the last one
 * wrote; never written. Can go once no open tab can still hold one.
 */
const WHOLE_SNAPSHOT_VERSION = 1;

/**
 * How stale a "was playing" snapshot may be and still start playback on its own. A reload
 * writes and reads within a second or two; anything older is a tab the browser restored after
 * a restart or a crash, where music starting unasked would be a surprise — that case comes back
 * paused at its position instead.
 */
export const AUTO_RESUME_WINDOW_MS = 60_000;

type StorageLike = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>;

export interface PlaybackSnapshotStore {
  /** The snapshot a reload left behind, or null when there is none worth trusting. */
  read(): PlaybackSnapshot | null;
  /**
   * Whether writing a snapshot of `queue` would write the queue itself — the expensive part, which
   * a caller may want to keep away from a tap. False once that very array is the one stored.
   */
  needsQueueWrite(queue: PlayerSong[]): boolean;
  /**
   * Write the snapshot, or remove it when there is nothing to come back to. The queue is written
   * only when it is not the array already stored: a queue is replaced, never edited in place, so
   * the same array is the same queue.
   */
  write(snapshot: PlaybackSnapshot | null): void;
}

export function createPlaybackSnapshotStore(storage: StorageLike | null): PlaybackSnapshotStore {
  /** The array the queue entry holds, and the id it is stored under. */
  let stored: { queue: PlayerSong[]; length: number; id: string } | null = null;
  /** A queue the storage would not take (too large for its quota): not worth serialising again. */
  let refused: PlayerSong[] | null = null;

  const holds = (queue: PlayerSong[]) =>
    stored !== null && stored.queue === queue && stored.length === queue.length;

  function clear() {
    stored = null;
    try {
      storage?.removeItem(PLAYBACK_SNAPSHOT_KEY);
      storage?.removeItem(PLAYBACK_QUEUE_KEY);
    } catch {
      // storage disabled: there was nothing to remove
    }
  }

  return {
    read() {
      if (!storage) return null;
      try {
        const raw = storage.getItem(PLAYBACK_SNAPSHOT_KEY);
        const state = parseSnapshotState(raw);
        // Not remembered as stored: the next write turns a whole snapshot into the two entries.
        if (!state) return parseWholeSnapshot(raw);
        const queue = parseSnapshotQueue(storage.getItem(PLAYBACK_QUEUE_KEY));
        // The two are only worth anything together: a queue entry from another write would put
        // the index on some other song.
        if (!queue || queue.id !== state.queueId || queue.songs.length !== state.queueLength) {
          return null;
        }
        if (state.snapshot.queueIndex >= queue.songs.length) return null;
        stored = { queue: queue.songs, length: queue.songs.length, id: queue.id };
        return { ...state.snapshot, queue: queue.songs };
      } catch {
        return null; // storage disabled / private mode
      }
    },

    needsQueueWrite(queue) {
      return storage !== null && !holds(queue) && queue !== refused;
    },

    write(snapshot) {
      if (!storage) return;
      if (!snapshot) {
        refused = null;
        clear();
        return;
      }
      const { queue, ...state } = snapshot;
      if (queue === refused) return;
      try {
        let entry = holds(queue) ? stored : null;
        if (!entry) {
          stored = null;
          // The small entry is removed before the queue is replaced: the two never disagree, and
          // a whole snapshot from the version before does not have to fit beside its successor.
          storage.removeItem(PLAYBACK_SNAPSHOT_KEY);
          entry = { queue, length: queue.length, id: newQueueId() };
          storage.setItem(PLAYBACK_QUEUE_KEY, JSON.stringify({ id: entry.id, songs: queue }));
          stored = entry;
          refused = null;
        }
        storage.setItem(
          PLAYBACK_SNAPSHOT_KEY,
          JSON.stringify({
            v: SNAPSHOT_VERSION,
            queueId: entry.id,
            queueLength: entry.length,
            ...state
          })
        );
      } catch {
        // Best-effort: a full or disabled store just means the next reload starts silent. Nothing
        // is left behind for it to take for this queue, and a queue that did not fit is not
        // serialised again on every write that follows.
        refused = queue;
        clear();
      }
    }
  };
}

/** An id no earlier write in this tab can have used: the entries outlive the document. */
function newQueueId(): string {
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

/** True when a restored snapshot should start playing without waiting for a click. */
export function canAutoResume(snapshot: PlaybackSnapshot, now: number): boolean {
  if (!snapshot.wasPlaying || snapshot.miniPlayerDismissed) return false;
  const age = now - snapshot.savedAt;
  return age >= 0 && age <= AUTO_RESUME_WINDOW_MS;
}

/** The small entry, parsed: the snapshot less its queue, and the queue it was written for. */
interface SnapshotState {
  queueId: string;
  queueLength: number;
  snapshot: Omit<PlaybackSnapshot, 'queue'>;
}

/**
 * Parse and validate the small entry. Anything that is not exactly the shape this version writes
 * yields `null` — an older or hand-edited entry is worth nothing, not a half-restored queue.
 */
export function parseSnapshotState(raw: string | null): SnapshotState | null {
  const value = parseRecord(raw);
  if (!value || value.v !== SNAPSHOT_VERSION) return null;
  if (typeof value.queueId !== 'string' || value.queueId.length === 0) return null;
  if (!Number.isInteger(value.queueLength) || (value.queueLength as number) < 1) return null;
  const snapshot = parseFields(value);
  if (!snapshot) return null;
  return { queueId: value.queueId, queueLength: value.queueLength as number, snapshot };
}

/** Parse and validate the queue entry: every song has to be one this app could have written. */
export function parseSnapshotQueue(raw: string | null): { id: string; songs: PlayerSong[] } | null {
  const value = parseRecord(raw);
  if (!value || typeof value.id !== 'string' || value.id.length === 0) return null;
  const songs = parseSongs(value.songs);
  return songs ? { id: value.id, songs } : null;
}

/** Parse and validate a snapshot from the version that kept the queue in the one entry. */
export function parseWholeSnapshot(raw: string | null): PlaybackSnapshot | null {
  const value = parseRecord(raw);
  if (!value || value.v !== WHOLE_SNAPSHOT_VERSION) return null;
  const queue = parseSongs(value.queue);
  const fields = parseFields(value);
  if (!queue || !fields || fields.queueIndex >= queue.length) return null;
  return { ...fields, queue };
}

function parseRecord(raw: string | null): Record<string, unknown> | null {
  if (!raw) return null;
  try {
    const value: unknown = JSON.parse(raw);
    return isRecord(value) ? value : null;
  } catch {
    return null;
  }
}

/** Every field but the queue, which both versions carry alike. */
function parseFields(value: Record<string, unknown>): Omit<PlaybackSnapshot, 'queue'> | null {
  if (typeof value.userId !== 'string' || value.userId.length === 0) return null;
  const queueIndex = value.queueIndex;
  if (!Number.isInteger(queueIndex) || (queueIndex as number) < 0) return null;
  if (!isFiniteNonNegative(value.position)) return null;
  if (!isFiniteNonNegative(value.volume) || value.volume > 1) return null;
  if (typeof value.wasPlaying !== 'boolean') return null;
  if (typeof value.radioExhausted !== 'boolean') return null;
  if (typeof value.miniPlayerDismissed !== 'boolean') return null;
  if (value.radioSeedId !== null && !Number.isFinite(value.radioSeedId)) return null;
  if (!Number.isFinite(value.savedAt)) return null;

  return {
    userId: value.userId,
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

function parseSongs(value: unknown): PlayerSong[] | null {
  if (!Array.isArray(value) || value.length === 0) return null;
  const songs: PlayerSong[] = [];
  for (const entry of value) {
    const song = parseSong(entry);
    if (!song) return null;
    songs.push(song);
  }
  return songs;
}

function parseSong(entry: unknown): PlayerSong | null {
  if (!isRecord(entry)) return null;
  if (!Number.isFinite(entry.id)) return null;
  if (typeof entry.title !== 'string' || typeof entry.artist !== 'string') return null;
  // Stream URLs are same-origin paths (`/api/mh/songs/{id}/stream`); a stored absolute URL is
  // not something this app ever wrote, so it is not something it should play.
  if (typeof entry.streamUrl !== 'string' || !entry.streamUrl.startsWith('/')) return null;
  if (!isOptionalString(entry.coverUrl) || !isOptionalString(entry.album)) return null;
  // Absent in snapshots written before songs carried it; the player then tries the original.
  if (!isOptionalString(entry.format)) return null;
  return {
    id: entry.id as number,
    title: entry.title,
    artist: entry.artist,
    streamUrl: entry.streamUrl,
    coverUrl: entry.coverUrl as string | null | undefined,
    album: entry.album as string | null | undefined,
    format: entry.format as string | null | undefined,
    // A reload before the library's rows arrived keeps a picked-up session's stand-ins as such,
    // so they still become their rows.
    ...(entry.standIn === true ? { standIn: true } : {})
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
