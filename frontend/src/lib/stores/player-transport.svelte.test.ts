import { beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * The player store owns a real `new Audio()` and the OS Media Session, neither of which exists
 * under node. Both are faked here with just enough surface for the transport rules: the element
 * keeps `currentTime` and answers play/pause, and the Media Session records the handlers the store
 * registers so a test can see which OS buttons are live. SvelteKit and the app modules the store
 * imports are mocked; each test gets a fresh store (vi.resetModules + a dynamic import).
 */

vi.mock('$app/environment', () => ({ browser: true }));
vi.mock('svelte-sonner', () => ({
  toast: Object.assign(vi.fn(), { error: vi.fn(), success: vi.fn(), info: vi.fn() })
}));
vi.mock('$lib/api-client', () => ({
  coverThumbUrl: (url: string | null | undefined) => url ?? null,
  fetchRadio: vi.fn(async () => []),
  reportSongPlayed: vi.fn(async () => {}),
  toPlayerSong: vi.fn()
}));
vi.mock('$lib/stores/songs.svelte', () => ({
  songsStore: { notePlayed: vi.fn(), songsById: new Map() }
}));
vi.mock('$lib/track-list-view.svelte', () => ({ artistOf: () => '' }));

class FakeAudio {
  src = '';
  currentTime = 0;
  duration = 200;
  paused = true;
  volume = 1;
  playbackRate = 1;
  defaultPlaybackRate = 1;
  preservesPitch = true;
  preload = '';
  private listeners = new Map<string, ((e: Event) => void)[]>();
  addEventListener(type: string, fn: (e: Event) => void) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), fn]);
  }
  setAttribute() {}
  removeAttribute() {}
  load() {}
  play() {
    this.paused = false;
    return Promise.resolve();
  }
  pause() {
    this.paused = true;
  }
}

let audio: FakeAudio;
let handlers: Map<string, unknown>;

beforeEach(() => {
  vi.resetModules();
  handlers = new Map();
  vi.stubGlobal(
    'Audio',
    class extends FakeAudio {
      constructor() {
        super();
        audio = this; // eslint-disable-line @typescript-eslint/no-this-alias
      }
    }
  );
  vi.stubGlobal('MediaMetadata', class {});
  vi.stubGlobal('navigator', {
    mediaSession: {
      metadata: null,
      playbackState: 'none',
      setActionHandler: (action: string, handler: unknown) => handlers.set(action, handler),
      setPositionState: () => {}
    }
  });
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => ({ ok: true }))
  );
  // A visible page: loadAndPlay only skips its pre-flight fetch while hidden, and the hidden-page
  // hand-off has its own suite (player.svelte.test.ts).
  vi.stubGlobal('document', { hidden: false });
  vi.stubGlobal('requestAnimationFrame', () => 0);
  vi.stubGlobal('cancelAnimationFrame', () => {});
});

const song = (id: number) => ({
  id,
  title: `Song ${id}`,
  artist: 'Artist',
  streamUrl: `/stream/${id}`
});
const QUEUE = [song(1), song(2), song(3)];

async function freshStore() {
  const { playerStore } = await import('./player.svelte');
  return playerStore;
}

/** Let loadAndPlay's awaited range probe settle. */
const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

describe('playPrevious', () => {
  it('restarts the current track once it is more than 3s in', async () => {
    const player = await freshStore();
    await player.playSong(QUEUE[1], QUEUE, 1);
    audio.currentTime = 42;
    player.playPrevious();
    await settle();
    expect(player.currentSong?.id).toBe(2);
    expect(audio.currentTime).toBe(0);
    expect(player.currentTime).toBe(0);
  });

  it('goes back one item near the start of a track', async () => {
    const player = await freshStore();
    await player.playSong(QUEUE[1], QUEUE, 1);
    audio.currentTime = 1.2;
    player.playPrevious();
    await settle();
    expect(player.currentSong?.id).toBe(1);
  });

  it('restarts the first item instead of going dark', async () => {
    const player = await freshStore();
    await player.playSong(QUEUE[0], QUEUE, 0);
    audio.currentTime = 2;
    expect(player.hasPrevious).toBe(true);
    player.playPrevious();
    await settle();
    expect(player.currentSong?.id).toBe(1);
    expect(audio.currentTime).toBe(0);
  });

  it('keeps a paused track paused when it restarts', async () => {
    const player = await freshStore();
    await player.playSong(QUEUE[1], QUEUE, 1);
    player.pause();
    audio.currentTime = 30;
    player.playPrevious();
    expect(audio.paused).toBe(true);
    expect(audio.currentTime).toBe(0);
  });

  it('is unavailable only while nothing is loaded', async () => {
    const player = await freshStore();
    expect(player.hasPrevious).toBe(false);
    await player.playSong(QUEUE[0], QUEUE, 0);
    expect(player.hasPrevious).toBe(true);
  });
});

describe('Media Session seek actions', () => {
  it('never registers seekbackward/seekforward, so iOS shows Previous/Next instead of ±10s', async () => {
    const player = await freshStore();
    await player.playSong(QUEUE[1], QUEUE, 1);
    expect(handlers.has('seekbackward')).toBe(false);
    expect(handlers.has('seekforward')).toBe(false);
    expect(typeof handlers.get('seekto')).toBe('function');
    expect(typeof handlers.get('nexttrack')).toBe('function');
  });
});

describe('Media Session previoustrack', () => {
  it('stays registered at the head of the queue, where it restarts the track', async () => {
    const player = await freshStore();
    await player.playSong(QUEUE[0], QUEUE, 0);
    const previous = handlers.get('previoustrack');
    expect(typeof previous).toBe('function');
    audio.currentTime = 10;
    (previous as () => void)();
    expect(audio.currentTime).toBe(0);
    expect(player.currentSong?.id).toBe(1);
  });

  it('steps back from a later item near its start', async () => {
    const player = await freshStore();
    await player.playSong(QUEUE[2], QUEUE, 2);
    audio.currentTime = 0.5;
    (handlers.get('previoustrack') as () => void)();
    await settle();
    expect(player.currentSong?.id).toBe(2);
  });
});

describe('startQueue', () => {
  it('plays the queue from the given index', async () => {
    const player = await freshStore();
    await player.startQueue(QUEUE, 1);
    expect(player.currentSong?.id).toBe(2);
    expect(audio.paused).toBe(false);
    expect(player.hasNext).toBe(true);
  });

  it('never pauses the song that is already playing', async () => {
    const player = await freshStore();
    await player.startQueue(QUEUE, 0);
    audio.currentTime = 42;
    await player.startQueue(QUEUE, 0);
    expect(audio.paused).toBe(false);
    // Kept playing where it was rather than restarting.
    expect(audio.currentTime).toBe(42);
    expect(player.currentSong?.id).toBe(1);
  });

  it('resumes the loaded song when it is paused', async () => {
    const player = await freshStore();
    await player.startQueue(QUEUE, 2);
    player.pause();
    audio.currentTime = 17;
    await player.startQueue(QUEUE, 2);
    expect(audio.paused).toBe(false);
    expect(audio.currentTime).toBe(17);
  });

  it('re-seeds the queue even when the target is already loaded', async () => {
    const player = await freshStore();
    await player.startQueue([QUEUE[0]], 0);
    expect(player.hasNext).toBe(true); // the station, not a queued track
    await player.startQueue(QUEUE, 0);
    player.playNext();
    await settle();
    expect(player.currentSong?.id).toBe(2);
  });

  it('brings a hidden mini player back even when the song is already playing', async () => {
    const player = await freshStore();
    await player.startQueue(QUEUE, 0);
    player.dismissMiniPlayer();
    expect(player.isMiniPlayerDismissed).toBe(true);
    await player.startQueue(QUEUE, 0);
    expect(audio.paused).toBe(false);
    expect(player.isMiniPlayerDismissed).toBe(false);
  });

  it('does nothing for an index outside the queue', async () => {
    const player = await freshStore();
    await player.startQueue(QUEUE, 5);
    expect(player.currentSong).toBeNull();
  });

  it('differs from playSong, which toggles the loaded song', async () => {
    const player = await freshStore();
    await player.playSong(QUEUE[0], QUEUE, 0);
    await player.playSong(QUEUE[0], QUEUE, 0);
    expect(audio.paused).toBe(true);
  });
});
