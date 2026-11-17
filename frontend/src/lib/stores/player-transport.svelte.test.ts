import { beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * The player store owns a real `new Audio()` and the OS Media Session, neither of which exists
 * under node. Both are faked here with just enough surface for the transport rules: the element
 * keeps `currentTime` and answers play/pause, and the Media Session records the handlers the store
 * registers so a test can see which OS buttons are live. SvelteKit and the app modules the store
 * imports are mocked; each test gets a fresh store (vi.resetModules + a dynamic import).
 */

vi.mock('$app/env', () => ({ browser: true }));
vi.mock('svelte-sonner', () => ({
  toast: Object.assign(vi.fn(), { error: vi.fn(), success: vi.fn(), info: vi.fn() })
}));
vi.mock('#lib/api-client.ts', () => ({
  coverThumbUrl: (url: string | null | undefined) => url ?? null,
  fetchRadio: vi.fn(async () => []),
  reportSongPlayed: vi.fn(async () => {}),
  toPlayerSong: vi.fn()
}));
vi.mock('#lib/stores/songs.svelte.ts', () => ({
  songsStore: { notePlayed: vi.fn(), songsById: new Map() }
}));
vi.mock('#lib/track-list-view.svelte', () => ({ artistOf: () => '' }));

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

describe('Media Session play after the computer wakes', () => {
  const MAC_UA =
    'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36';

  /** A Mac, whose monotonic clock stops while it sleeps and whose wall clock does not. */
  function onAMac() {
    const nav = navigator as unknown as Record<string, unknown>;
    vi.stubGlobal('navigator', { ...nav, userAgent: MAC_UA, maxTouchPoints: 0 });
    vi.stubGlobal('window', { addEventListener: () => {}, removeEventListener: () => {} });
    (document as unknown as Record<string, unknown>).addEventListener = () => {};
    let wall = 1_700_000_000_000;
    let mono = 1_000;
    vi.spyOn(Date, 'now').mockImplementation(() => wall);
    vi.spyOn(performance, 'now').mockImplementation(() => mono);
    return {
      sleep: (ms: number) => (wall += ms),
      pass: (ms: number) => {
        wall += ms;
        mono += ms;
      }
    };
  }

  const osPlay = () => (handlers.get('play') as () => void)();

  it('leaves paused music paused when the OS says play as the lid opens', async () => {
    const mac = onAMac();
    const player = await freshStore();
    await player.playSong(QUEUE[0], QUEUE, 0);
    player.pause();
    mac.sleep(40 * 60 * 1000);
    mac.pass(500);
    osPlay();
    await settle();
    expect(audio.paused).toBe(true);
    expect(player.isPlaying).toBe(false);
    vi.restoreAllMocks();
  });

  it('plays on a press a few seconds after the wake', async () => {
    const mac = onAMac();
    const player = await freshStore();
    await player.playSong(QUEUE[0], QUEUE, 0);
    player.pause();
    mac.sleep(40 * 60 * 1000);
    osPlay();
    mac.pass(6_000);
    osPlay();
    await settle();
    expect(audio.paused).toBe(false);
    vi.restoreAllMocks();
  });

  it('plays at once when the computer never slept', async () => {
    const mac = onAMac();
    const player = await freshStore();
    await player.playSong(QUEUE[0], QUEUE, 0);
    player.pause();
    mac.pass(40 * 60 * 1000);
    osPlay();
    await settle();
    expect(audio.paused).toBe(false);
    vi.restoreAllMocks();
  });
});

describe('trackStep', () => {
  it('is next once a skip has loaded the following song', async () => {
    const player = await freshStore();
    await player.playSong(QUEUE[0], QUEUE, 0);
    player.playNext();
    expect(player.currentSong?.id).toBe(1);
    await settle();
    expect(player.currentSong?.id).toBe(2);
    expect(player.trackStep).toBe('next');
  });

  it('is previous after stepping back near the start of a song', async () => {
    const player = await freshStore();
    await player.playSong(QUEUE[2], QUEUE, 2);
    audio.currentTime = 1;
    player.playPrevious();
    await settle();
    expect(player.currentSong?.id).toBe(2);
    expect(player.trackStep).toBe('previous');
  });

  it('is a swap when a song is picked rather than stepped to', async () => {
    const player = await freshStore();
    await player.playSong(QUEUE[0], QUEUE, 0);
    player.playNext();
    await settle();
    await player.playSong(QUEUE[0], QUEUE, 0);
    expect(player.currentSong?.id).toBe(1);
    expect(player.trackStep).toBe('swap');
  });
});
