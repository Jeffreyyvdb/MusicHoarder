import { describe, expect, it } from 'vitest';
import {
  AUTO_RESUME_WINDOW_MS,
  PLAYBACK_SNAPSHOT_KEY,
  canAutoResume,
  clearPlaybackSnapshot,
  parsePlaybackSnapshot,
  readPlaybackSnapshot,
  writePlaybackSnapshot,
  type PlaybackSnapshot
} from './player-snapshot';

/**
 * The reload snapshot is the one piece of player state that outlives the document, so what it
 * accepts back is worth pinning: a stored value is trusted only when it is exactly the shape
 * this version writes, the account that wrote it is carried along, and "was playing" only
 * restarts audio when the write is fresh enough to have been a reload rather than a restored tab.
 */

function song(id: number) {
  return {
    id,
    title: `Track ${id}`,
    artist: 'Artist',
    streamUrl: `/api/mh/songs/${id}/stream`,
    coverUrl: null,
    album: 'Album'
  };
}

function snapshot(over: Partial<PlaybackSnapshot> = {}): PlaybackSnapshot {
  return {
    v: 1,
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
  private readonly map = new Map<string, string>();
  getItem(key: string) {
    return this.map.get(key) ?? null;
  }
  setItem(key: string, value: string) {
    this.map.set(key, value);
  }
  removeItem(key: string) {
    this.map.delete(key);
  }
}

describe('playback snapshot round-trip', () => {
  it('reads back exactly what was written', () => {
    const storage = new MemoryStorage();
    const written = snapshot();
    writePlaybackSnapshot(storage, written);
    expect(readPlaybackSnapshot(storage)).toEqual(written);
  });

  it('writing null removes the entry, as does clear', () => {
    const storage = new MemoryStorage();
    writePlaybackSnapshot(storage, snapshot());
    writePlaybackSnapshot(storage, null);
    expect(storage.getItem(PLAYBACK_SNAPSHOT_KEY)).toBeNull();

    writePlaybackSnapshot(storage, snapshot());
    clearPlaybackSnapshot(storage);
    expect(readPlaybackSnapshot(storage)).toBeNull();
  });

  it('is a no-op without storage and swallows a throwing store', () => {
    expect(readPlaybackSnapshot(null)).toBeNull();
    expect(() => writePlaybackSnapshot(null, snapshot())).not.toThrow();
    const broken = {
      getItem: () => {
        throw new Error('disabled');
      },
      setItem: () => {
        throw new Error('disabled');
      },
      removeItem: () => {
        throw new Error('disabled');
      }
    };
    expect(readPlaybackSnapshot(broken)).toBeNull();
    expect(() => writePlaybackSnapshot(broken, snapshot())).not.toThrow();
  });

  it("keeps the owning account so the store can refuse another account's queue", () => {
    expect(parsePlaybackSnapshot(JSON.stringify(snapshot({ userId: 'user-b' })))?.userId).toBe(
      'user-b'
    );
  });
});

describe('parsePlaybackSnapshot rejects anything but the exact shape', () => {
  const cases: Array<[string, unknown]> = [
    ['not json', 'nope'],
    ['a different version', { ...snapshot(), v: 2 }],
    ['a missing account', { ...snapshot(), userId: '' }],
    ['an empty queue', { ...snapshot(), queue: [] }],
    ['an index past the queue', { ...snapshot(), queueIndex: 3 }],
    ['a negative index', { ...snapshot(), queueIndex: -1 }],
    ['a fractional index', { ...snapshot(), queueIndex: 1.5 }],
    ['a negative position', { ...snapshot(), position: -1 }],
    ['a volume above one', { ...snapshot(), volume: 1.5 }],
    ['a non-boolean playing flag', { ...snapshot(), wasPlaying: 'yes' }],
    ['a stamp that is not a number', { ...snapshot(), savedAt: 'now' }],
    [
      'a song with an absolute stream URL',
      {
        ...snapshot(),
        queue: [{ ...song(1), streamUrl: 'https://elsewhere.example/1.mp3' }]
      }
    ],
    ['a song without a title', { ...snapshot(), queue: [{ ...song(1), title: undefined }] }],
    ['a song whose id is not a number', { ...snapshot(), queue: [{ ...song(1), id: '1' }] }]
  ];

  for (const [label, value] of cases) {
    it(label, () => {
      const raw = typeof value === 'string' ? value : JSON.stringify(value);
      expect(parsePlaybackSnapshot(raw)).toBeNull();
    });
  }

  it('accepts an absent cover and album, and a null radio seed', () => {
    const value = snapshot({
      queue: [{ id: 1, title: 'T', artist: 'A', streamUrl: '/api/mh/songs/1/stream' }],
      queueIndex: 0,
      radioSeedId: null
    });
    expect(parsePlaybackSnapshot(JSON.stringify(value))).toEqual(value);
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
