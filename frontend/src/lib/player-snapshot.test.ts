import { describe, expect, it, vi } from 'vitest';
import {
  AUTO_RESUME_WINDOW_MS,
  PLAYBACK_QUEUE_KEY,
  PLAYBACK_SNAPSHOT_KEY,
  canAutoResume,
  createPlaybackSnapshotStore,
  parseSnapshotQueue,
  parseSnapshotState,
  parseWholeSnapshot,
  type PlaybackSnapshot
} from './player-snapshot';

/**
 * The reload snapshot is the one piece of player state that outlives the document, so what it
 * accepts back is worth pinning: a stored value is trusted only when it is exactly the shape
 * this version writes, the account that wrote it is carried along, and "was playing" only
 * restarts audio when the write is fresh enough to have been a reload rather than a restored tab.
 *
 * It is two entries — the queue, and everything else — so that the position can be written while
 * a song plays without serialising a library-sized queue each time. What that adds to pin: the
 * queue is written once per queue, and the two entries are only ever believed together.
 */

function song(id: number) {
  return {
    id,
    title: `Track ${id}`,
    artist: 'Artist',
    streamUrl: `/api/mh/songs/${id}/stream`,
    coverUrl: null,
    album: 'Album',
    format: 'opus'
  };
}

function snapshot(over: Partial<PlaybackSnapshot> = {}): PlaybackSnapshot {
  return {
    userId: 'user-a',
    queue: [song(1), song(2), song(3)],
    queueIndex: 1,
    position: 42.5,
    wasPlaying: true,
    volume: 0.8,
    radioSeedId: 1,
    radioExhausted: false,
    miniPlayerDismissed: false,
    savedAt: 1_000_000,
    ...over
  };
}

class MemoryStorage {
  readonly map = new Map<string, string>();
  /** Every key written, in order: what tells a position write from a queue write. */
  readonly writes: string[] = [];
  getItem(key: string) {
    return this.map.get(key) ?? null;
  }
  setItem(key: string, value: string) {
    this.writes.push(key);
    this.map.set(key, value);
  }
  removeItem(key: string) {
    this.map.delete(key);
  }
  /** What a reload does to the tab's storage: nothing. A fresh store has to make sense of it. */
  reloaded() {
    return createPlaybackSnapshotStore(this);
  }
}

/** A storage that takes values up to `limit` characters, like one with a quota. */
class SmallStorage extends MemoryStorage {
  constructor(private readonly limit: number) {
    super();
  }
  override setItem(key: string, value: string) {
    this.writes.push(key);
    if (value.length > this.limit) throw new DOMException('full', 'QuotaExceededError');
    this.map.set(key, value);
  }
}

describe('playback snapshot round-trip', () => {
  it('reads back exactly what was written', () => {
    const storage = new MemoryStorage();
    const written = snapshot();
    createPlaybackSnapshotStore(storage).write(written);
    expect(storage.reloaded().read()).toEqual(written);
  });

  it('reads back a library-sized queue, in its order', () => {
    const storage = new MemoryStorage();
    // Shuffled, as a queue is: the order is the snapshot's to keep, not the library's.
    const queue = Array.from({ length: 10_000 }, (_, i) => song((i * 7919) % 10_000));
    const written = snapshot({ queue, queueIndex: 9_999, radioSeedId: 4242 });
    createPlaybackSnapshotStore(storage).write(written);
    expect(storage.reloaded().read()).toEqual(written);
  });

  it('writing null removes both entries', () => {
    const storage = new MemoryStorage();
    const store = createPlaybackSnapshotStore(storage);
    store.write(snapshot());
    expect(storage.map.size).toBe(2);
    store.write(null);
    expect(storage.map.size).toBe(0);
    expect(storage.reloaded().read()).toBeNull();
  });

  it('is a no-op without storage and swallows a throwing store', () => {
    const none = createPlaybackSnapshotStore(null);
    expect(none.read()).toBeNull();
    expect(() => none.write(snapshot())).not.toThrow();
    expect(none.needsQueueWrite(snapshot().queue)).toBe(false);

    const broken = createPlaybackSnapshotStore({
      getItem: () => {
        throw new Error('disabled');
      },
      setItem: () => {
        throw new Error('disabled');
      },
      removeItem: () => {
        throw new Error('disabled');
      }
    });
    expect(broken.read()).toBeNull();
    expect(() => broken.write(snapshot())).not.toThrow();
    expect(() => broken.write(null)).not.toThrow();
  });

  it("keeps the owning account so the store can refuse another account's queue", () => {
    const storage = new MemoryStorage();
    createPlaybackSnapshotStore(storage).write(snapshot({ userId: 'user-b' }));
    expect(storage.reloaded().read()?.userId).toBe('user-b');
  });
});

describe('the queue is written once per queue', () => {
  it('a position, a pause or a volume change writes only the small entry', () => {
    const storage = new MemoryStorage();
    const store = createPlaybackSnapshotStore(storage);
    const queue = snapshot().queue;

    expect(store.needsQueueWrite(queue)).toBe(true);
    store.write(snapshot({ queue }));
    expect(storage.writes).toEqual([PLAYBACK_QUEUE_KEY, PLAYBACK_SNAPSHOT_KEY]);
    expect(store.needsQueueWrite(queue)).toBe(false);

    storage.writes.length = 0;
    store.write(snapshot({ queue, position: 47.5, savedAt: 1_005_000 }));
    store.write(snapshot({ queue, position: 52.5, wasPlaying: false, volume: 0.3 }));
    store.write(snapshot({ queue, queueIndex: 2, position: 0 }));
    expect(storage.writes).toEqual([
      PLAYBACK_SNAPSHOT_KEY,
      PLAYBACK_SNAPSHOT_KEY,
      PLAYBACK_SNAPSHOT_KEY
    ]);
    // The small entry really is small: nothing of the queue is in it.
    expect(storage.getItem(PLAYBACK_SNAPSHOT_KEY)).not.toContain('Track');
    expect(storage.reloaded().read()).toEqual(snapshot({ queue, queueIndex: 2, position: 0 }));
  });

  it('a different queue is written, even one with the same songs', () => {
    const storage = new MemoryStorage();
    const store = createPlaybackSnapshotStore(storage);
    const first = snapshot().queue;
    store.write(snapshot({ queue: first }));
    storage.writes.length = 0;

    // The station appended: a new array, as every change to the queue is.
    const longer = [...first, song(4)];
    expect(store.needsQueueWrite(longer)).toBe(true);
    store.write(snapshot({ queue: longer, queueIndex: 3 }));
    expect(storage.writes).toEqual([PLAYBACK_QUEUE_KEY, PLAYBACK_SNAPSHOT_KEY]);
    expect(storage.reloaded().read()).toEqual(snapshot({ queue: longer, queueIndex: 3 }));
  });

  it('is not written again after a reload: the queue read back is the one stored', () => {
    const storage = new MemoryStorage();
    createPlaybackSnapshotStore(storage).write(snapshot());

    const store = storage.reloaded();
    const restored = store.read()!;
    storage.writes.length = 0;
    expect(store.needsQueueWrite(restored.queue)).toBe(false);
    store.write({ ...restored, position: 60 });
    expect(storage.writes).toEqual([PLAYBACK_SNAPSHOT_KEY]);
    expect(storage.reloaded().read()).toEqual(snapshot({ position: 60 }));
  });
});

describe('the two entries are only believed together', () => {
  function written() {
    const storage = new MemoryStorage();
    createPlaybackSnapshotStore(storage).write(snapshot());
    return storage;
  }

  it('a small entry without its queue is worth nothing', () => {
    const storage = written();
    storage.removeItem(PLAYBACK_QUEUE_KEY);
    expect(storage.reloaded().read()).toBeNull();
  });

  it('a queue without its small entry is worth nothing', () => {
    const storage = written();
    storage.removeItem(PLAYBACK_SNAPSHOT_KEY);
    expect(storage.reloaded().read()).toBeNull();
  });

  it("a queue from another write never takes this entry's index", () => {
    const storage = written();
    const other = new MemoryStorage();
    createPlaybackSnapshotStore(other).write(snapshot({ queue: [song(7), song(8), song(9)] }));
    // Same length, same shape, a different queue: only the id tells them apart.
    storage.map.set(PLAYBACK_QUEUE_KEY, other.getItem(PLAYBACK_QUEUE_KEY)!);
    expect(storage.reloaded().read()).toBeNull();
  });

  it('a queue that lost songs is not the queue the entry describes', () => {
    const storage = written();
    const queue = JSON.parse(storage.getItem(PLAYBACK_QUEUE_KEY)!);
    storage.map.set(
      PLAYBACK_QUEUE_KEY,
      JSON.stringify({ ...queue, songs: queue.songs.slice(0, 2) })
    );
    expect(storage.reloaded().read()).toBeNull();
  });

  it('a queue the storage has no room for leaves nothing behind, and is not serialised again', () => {
    const storage = new SmallStorage(400);
    const store = createPlaybackSnapshotStore(storage);
    store.write(snapshot({ queue: [song(1)], queueIndex: 0 }));
    expect(storage.reloaded().read()).not.toBeNull();

    const stringify = vi.spyOn(JSON, 'stringify');
    const tooLong = Array.from({ length: 50 }, (_, i) => song(i));
    store.write(snapshot({ queue: tooLong }));
    // Not the queue that was stored before, under this one's index: nothing at all.
    expect(storage.map.size).toBe(0);
    expect(storage.reloaded().read()).toBeNull();

    stringify.mockClear();
    expect(store.needsQueueWrite(tooLong)).toBe(false);
    store.write(snapshot({ queue: tooLong, position: 50 }));
    store.write(snapshot({ queue: tooLong, position: 55 }));
    expect(stringify).not.toHaveBeenCalled();
    expect(storage.map.size).toBe(0);
    stringify.mockRestore();

    // A queue that fits is stored as ever.
    store.write(snapshot({ queue: [song(2)], queueIndex: 0 }));
    expect(storage.reloaded().read()?.queue).toEqual([song(2)]);
  });
});

describe('a snapshot from the version that kept the queue in one entry', () => {
  const whole = () => ({ v: 1, ...snapshot() });

  it('is still restored, so the reload onto this version keeps the music', () => {
    const storage = new MemoryStorage();
    storage.map.set(PLAYBACK_SNAPSHOT_KEY, JSON.stringify(whole()));
    expect(storage.reloaded().read()).toEqual(snapshot());
  });

  it('becomes the two entries at the next write', () => {
    const storage = new MemoryStorage();
    storage.map.set(PLAYBACK_SNAPSHOT_KEY, JSON.stringify(whole()));
    const store = storage.reloaded();
    const restored = store.read()!;

    expect(store.needsQueueWrite(restored.queue)).toBe(true);
    store.write({ ...restored, position: 50 });
    expect(storage.getItem(PLAYBACK_SNAPSHOT_KEY)).not.toContain('Track');
    expect(storage.reloaded().read()).toEqual(snapshot({ position: 50 }));
  });

  it('is rejected unless it is exactly that shape', () => {
    expect(parseWholeSnapshot(JSON.stringify({ ...whole(), queueIndex: 3 }))).toBeNull();
    expect(parseWholeSnapshot(JSON.stringify({ ...whole(), queue: [] }))).toBeNull();
    expect(parseWholeSnapshot(JSON.stringify({ ...whole(), v: 0 }))).toBeNull();
  });
});

describe('the small entry is rejected unless it is exactly the shape written', () => {
  const state = () => {
    const storage = new MemoryStorage();
    createPlaybackSnapshotStore(storage).write(snapshot());
    return JSON.parse(storage.getItem(PLAYBACK_SNAPSHOT_KEY)!) as Record<string, unknown>;
  };

  it('accepts what the store writes, and a null radio seed', () => {
    expect(parseSnapshotState(JSON.stringify(state()))?.snapshot).toEqual(
      (({ queue: _queue, ...rest }) => rest)(snapshot())
    );
    expect(parseSnapshotState(JSON.stringify({ ...state(), radioSeedId: null }))).not.toBeNull();
  });

  const cases: Array<[string, unknown]> = [
    ['not json', 'nope'],
    ['a different version', { ...state(), v: 3 }],
    ['a missing account', { ...state(), userId: '' }],
    ['no queue named', { ...state(), queueId: '' }],
    ['a queue length that is not a count', { ...state(), queueLength: 0 }],
    ['a negative index', { ...state(), queueIndex: -1 }],
    ['a fractional index', { ...state(), queueIndex: 1.5 }],
    ['a negative position', { ...state(), position: -1 }],
    ['a volume above one', { ...state(), volume: 1.5 }],
    ['a non-boolean playing flag', { ...state(), wasPlaying: 'yes' }],
    ['a stamp that is not a number', { ...state(), savedAt: 'now' }]
  ];

  for (const [label, value] of cases) {
    it(label, () => {
      const raw = typeof value === 'string' ? value : JSON.stringify(value);
      expect(parseSnapshotState(raw)).toBeNull();
    });
  }

  it('an index past the queue is rejected once the queue is known', () => {
    const storage = new MemoryStorage();
    createPlaybackSnapshotStore(storage).write(snapshot());
    // Still the entry of the queue stored beside it; only its index is out of range.
    const own = JSON.parse(storage.getItem(PLAYBACK_SNAPSHOT_KEY)!);
    storage.map.set(PLAYBACK_SNAPSHOT_KEY, JSON.stringify({ ...own, queueIndex: 3 }));
    expect(storage.reloaded().read()).toBeNull();
  });
});

describe('the queue entry is rejected unless every song is one this app wrote', () => {
  const entry = (songs: unknown[]) => JSON.stringify({ id: 'q1', songs });

  const cases: Array<[string, unknown[]]> = [
    ['an empty queue', []],
    [
      'a song with an absolute stream URL',
      [{ ...song(1), streamUrl: 'https://elsewhere.example/1.mp3' }]
    ],
    ['a song without a title', [{ ...song(1), title: undefined }]],
    ['a song whose id is not a number', [{ ...song(1), id: '1' }]],
    ['a song whose format is not a string', [{ ...song(1), format: 7 }]]
  ];

  for (const [label, songs] of cases) {
    it(label, () => expect(parseSnapshotQueue(entry(songs))).toBeNull());
  }

  it('rejects an entry without an id, and one that is not json', () => {
    expect(parseSnapshotQueue(JSON.stringify({ songs: [song(1)] }))).toBeNull();
    expect(parseSnapshotQueue('nope')).toBeNull();
  });

  it('accepts an absent cover, album and format (a queue from before songs carried one)', () => {
    const bare = { id: 1, title: 'T', artist: 'A', streamUrl: '/api/mh/songs/1/stream' };
    expect(parseSnapshotQueue(entry([bare]))?.songs).toEqual([bare]);
  });

  it('keeps a stand-in a stand-in', () => {
    const standIn = { ...song(1), standIn: true };
    expect(parseSnapshotQueue(entry([standIn]))?.songs).toEqual([standIn]);
  });
});

describe('canAutoResume', () => {
  const savedAt = 1_000_000;

  it('resumes a fresh "was playing" snapshot', () => {
    expect(canAutoResume(snapshot({ savedAt }), savedAt + 1500)).toBe(true);
  });

  it('resumes right up to the window and not past it', () => {
    expect(canAutoResume(snapshot({ savedAt }), savedAt + AUTO_RESUME_WINDOW_MS)).toBe(true);
    expect(canAutoResume(snapshot({ savedAt }), savedAt + AUTO_RESUME_WINDOW_MS + 1)).toBe(false);
  });

  it('never resumes a paused or dismissed player', () => {
    expect(canAutoResume(snapshot({ savedAt, wasPlaying: false }), savedAt + 1)).toBe(false);
    expect(canAutoResume(snapshot({ savedAt, miniPlayerDismissed: true }), savedAt + 1)).toBe(
      false
    );
  });

  it('treats a stamp from the future as stale rather than fresh', () => {
    expect(canAutoResume(snapshot({ savedAt }), savedAt - 1)).toBe(false);
  });
});
