import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { PlaybackSyncHooks, PlayerRemote, PlayerSong } from './player.svelte';

/**
 * The player's side of the account's playback session: what it tells the session (a play intent,
 * a change), what it shows while the session plays elsewhere (the getters and the transport follow
 * a `PlayerRemote`), how it picks a session up (`localPlayback.adopt`), and the reload gate. The
 * sync store itself is replaced by a hand-driven fake here; without one plugged in, the other two
 * player suites pin that nothing changed.
 */

vi.mock('$app/environment', () => ({ browser: true }));
vi.mock('svelte-sonner', () => ({
  toast: Object.assign(vi.fn(), { error: vi.fn(), success: vi.fn(), info: vi.fn() })
}));
const reportSongPlayed = vi.fn(async () => {});
vi.mock('$lib/api-client', () => ({
  coverThumbUrl: (url: string | null | undefined) => url ?? null,
  fetchRadio: vi.fn(async () => []),
  reportSongPlayed: (...args: unknown[]) => reportSongPlayed(...(args as [])),
  toPlayerSong: vi.fn()
}));
const notePlayed = vi.fn();
vi.mock('$lib/stores/songs.svelte', () => ({
  songsStore: { notePlayed: (id: number) => notePlayed(id), songsById: new Map() }
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
  playResult: 'ok' | 'blocked' = 'ok';
  /** Set by a failed source; as on the real element, only a new load clears it (not `paused`). */
  error: { code: number } | null = null;
  readyState = 0;
  loop = false;
  private listeners = new Map<string, ((e: Event) => void)[]>();
  addEventListener(type: string, fn: (e: Event) => void) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), fn]);
  }
  dispatch(type: string) {
    for (const fn of this.listeners.get(type) ?? []) fn(new Event(type));
  }
  setAttribute() {}
  removeAttribute() {}
  /** As the real element's load algorithm does: back to 0:00, paused, no error. */
  load() {
    this.currentTime = 0;
    this.paused = true;
    this.readyState = 0;
    this.error = null;
  }
  /**
   * The song plays out. The element loops (see the player's `songFinished`), so the end is audio
   * flowing near it and then the jump back to 0:00, never an `ended`.
   */
  finish() {
    this.readyState = 4;
    this.currentTime = this.duration - 1;
    this.dispatch('playing');
    this.dispatch('timeupdate');
    this.currentTime = 0;
    this.dispatch('seeking');
  }
  play = vi.fn(() => {
    if (this.playResult === 'blocked') {
      return Promise.reject(new DOMException('no gesture', 'NotAllowedError'));
    }
    this.paused = false;
    return Promise.resolve();
  });
  pause() {
    this.paused = true;
  }
}

let audio: FakeAudio;
let handlers: Map<string, unknown>;
let session: Map<string, string>;

beforeEach(() => {
  vi.resetModules();
  reportSongPlayed.mockClear();
  notePlayed.mockClear();
  handlers = new Map();
  session = new Map();
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
  vi.stubGlobal('document', { hidden: false, visibilityState: 'visible', addEventListener: () => {} });
  vi.stubGlobal('window', {
    sessionStorage: {
      getItem: (k: string) => session.get(k) ?? null,
      setItem: (k: string, v: string) => session.set(k, v),
      removeItem: (k: string) => session.delete(k)
    },
    addEventListener: () => {}
  });
  vi.stubGlobal('requestAnimationFrame', () => 0);
  vi.stubGlobal('cancelAnimationFrame', () => {});
});

const song = (id: number): PlayerSong => ({
  id,
  title: `Song ${id}`,
  artist: 'Artist',
  streamUrl: `/api/mh/songs/${id}/stream`
});
const QUEUE = [song(1), song(2), song(3)];
const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

/** A hand-driven stand-in for the sync store. */
function fakeSync(overrides: Partial<PlayerRemote> = {}) {
  const remote = {
    active: false,
    song: song(9) as PlayerSong | null,
    isPlaying: true,
    currentTime: 42,
    duration: 180,
    hasNext: true,
    pause: vi.fn(),
    resume: vi.fn(),
    next: vi.fn(),
    previous: vi.fn(),
    seek: vi.fn(),
    ...overrides
  };
  const hooks = {
    remote,
    playIntent: vi.fn(),
    changed: vi.fn(),
    restoreGate: vi.fn(async () => true),
    playHere: vi.fn()
  } satisfies PlaybackSyncHooks;
  return hooks;
}

async function load() {
  const mod = await import('./player.svelte');
  return mod;
}

describe('play intents', () => {
  it('a pick, Play, Next and Previous claim; the queue running on does not', async () => {
    const { playerStore, setPlaybackSync } = await load();
    const sync = fakeSync();
    setPlaybackSync(sync);

    await playerStore.startQueue(QUEUE, 0);
    expect(sync.playIntent).toHaveBeenCalledTimes(1);

    playerStore.playNext();
    expect(sync.playIntent).toHaveBeenCalledTimes(2);
    await settle();

    audio.currentTime = 1;
    playerStore.playPrevious();
    expect(sync.playIntent).toHaveBeenCalledTimes(3);
    await settle();

    // A track ending hands on to the next one: news, not a claim.
    audio.finish();
    await settle();
    expect(sync.playIntent).toHaveBeenCalledTimes(3);
    expect(playerStore.currentSong?.id).toBe(2);
  });

  it('pausing and seeking report a change, never a claim', async () => {
    const { playerStore, setPlaybackSync } = await load();
    const sync = fakeSync();
    setPlaybackSync(sync);
    await playerStore.startQueue(QUEUE, 0);
    sync.playIntent.mockClear();
    sync.changed.mockClear();

    playerStore.pause();
    audio.dispatch('pause');
    playerStore.seek(30);
    expect(sync.playIntent).not.toHaveBeenCalled();
    expect(sync.changed).toHaveBeenCalled();
  });

  it('resuming here claims', async () => {
    const { playerStore, setPlaybackSync } = await load();
    const sync = fakeSync();
    setPlaybackSync(sync);
    await playerStore.startQueue(QUEUE, 0);
    playerStore.pause();
    sync.playIntent.mockClear();
    playerStore.resume();
    expect(sync.playIntent).toHaveBeenCalledTimes(1);
  });
});

describe('while the session plays elsewhere', () => {
  it('the getters show the session, not the leftovers', async () => {
    const { playerStore, setPlaybackSync } = await load();
    await playerStore.startQueue(QUEUE, 0);
    const sync = fakeSync({ active: true });
    setPlaybackSync(sync);

    expect(playerStore.currentSong?.id).toBe(9);
    expect(playerStore.isPlaying).toBe(true);
    expect(playerStore.currentTime).toBe(42);
    expect(playerStore.duration).toBe(180);
    expect(playerStore.hasNext).toBe(true);
    expect(playerStore.hasPrevious).toBe(true);
    expect(playerStore.airPlayAvailable).toBe(false);

    setPlaybackSync(null);
    expect(playerStore.currentSong?.id).toBe(1);
  });

  it('the transport steers the other device', async () => {
    const { playerStore, setPlaybackSync } = await load();
    const sync = fakeSync({ active: true });
    setPlaybackSync(sync);

    playerStore.togglePlay();
    expect(sync.remote.pause).toHaveBeenCalled();
    playerStore.resume();
    expect(sync.remote.resume).toHaveBeenCalled();
    playerStore.playNext();
    expect(sync.remote.next).toHaveBeenCalled();
    playerStore.playPrevious();
    expect(sync.remote.previous).toHaveBeenCalled();
    playerStore.seek(12);
    expect(sync.remote.seek).toHaveBeenCalledWith(12);
    expect(audio?.play).not.toHaveBeenCalled();
    expect(sync.playIntent).not.toHaveBeenCalled();
  });

  it("an album's Play on the session's song resumes it there: no pick, no restart", async () => {
    const { playerStore, setPlaybackSync } = await load();
    const sync = fakeSync({ active: true, isPlaying: false });
    setPlaybackSync(sync);
    playerStore.dismissMiniPlayer();
    await playerStore.startQueue([song(8), song(9)], 1, { resumeIfLoaded: true });
    expect(sync.remote.resume).toHaveBeenCalledTimes(1);
    expect(sync.playIntent).not.toHaveBeenCalled();
    expect(reportSongPlayed).not.toHaveBeenCalled();
    expect(audio?.play).not.toHaveBeenCalled();
    expect(playerStore.isMiniPlayerDismissed).toBe(false);
  });

  // Android's Play and Shuffle mean the same: the list, from the top, here — even when its first
  // track is the one the other device is on.
  it.each([
    ['paused', false],
    ['playing', true]
  ])(
    "Shuffle or a list's Play landing on the session's song (%s there) plays that list here, and claims",
    async (_, isPlaying) => {
      const { playerStore, localPlayback, setPlaybackSync } = await load();
      const sync = fakeSync({ active: true, isPlaying });
      setPlaybackSync(sync);
      playerStore.dismissMiniPlayer();
      await playerStore.startQueue([song(9), song(8)], 0);
      expect(sync.remote.resume).not.toHaveBeenCalled();
      expect(sync.playIntent).toHaveBeenCalledTimes(1);
      expect(audio.src).toBe('/api/mh/songs/9/stream');
      expect(audio.currentTime).toBe(0);
      expect(audio.paused).toBe(false);
      expect(localPlayback.state()).toMatchObject({ queue: [9, 8], queueIndex: 0 });
      expect(reportSongPlayed).toHaveBeenCalledWith(9);
      expect(playerStore.isMiniPlayerDismissed).toBe(false);
    }
  );

  it('the speed controls step aside: they would change only this idle device', async () => {
    const { playerStore, setPlaybackSync } = await load();
    expect(playerStore.speedAdjustable).toBe(true);
    setPlaybackSync(fakeSync({ active: true }));
    expect(playerStore.speedAdjustable).toBe(false);
  });

  it("playSong on the session's song toggles it there", async () => {
    const { playerStore, setPlaybackSync } = await load();
    const sync = fakeSync({ active: true });
    setPlaybackSync(sync);
    await playerStore.playSong(song(9));
    expect(sync.remote.pause).toHaveBeenCalled();
    expect(sync.playIntent).not.toHaveBeenCalled();
  });

  it('starting something else here is a local play intent, fresh from the top', async () => {
    const { playerStore, setPlaybackSync } = await load();
    await playerStore.startQueue(QUEUE, 0);
    audio.currentTime = 77; // the superseded leftovers, paused mid-song
    audio.pause();
    const sync = fakeSync({ active: true });
    setPlaybackSync(sync);

    await playerStore.startQueue(QUEUE, 0);
    expect(sync.playIntent).toHaveBeenCalledTimes(1);
    // Reloaded rather than resumed at the leftovers' stale second.
    expect(audio.currentTime).toBe(0);
    expect(audio.paused).toBe(false);
  });

  it('a media key picks the session up here instead of steering it', async () => {
    const { playerStore, setPlaybackSync } = await load();
    await playerStore.startQueue(QUEUE, 1);
    const sync = fakeSync({ active: true });
    setPlaybackSync(sync);

    (handlers.get('play') as () => void)();
    expect(sync.playHere).toHaveBeenCalledWith('none');
    (handlers.get('nexttrack') as () => void)();
    expect(sync.playHere).toHaveBeenCalledWith('next');
    (handlers.get('previoustrack') as () => void)();
    expect(sync.playHere).toHaveBeenCalledWith('previous');
    expect(sync.remote.resume).not.toHaveBeenCalled();
  });
});

describe('a pick whose pre-flight fails', () => {
  it('reports the song still on the element, not the pick', async () => {
    const { playerStore, localPlayback, setPlaybackSync } = await load();
    const sync = fakeSync();
    setPlaybackSync(sync);
    await playerStore.startQueue(QUEUE, 0);
    audio.currentTime = 20;
    sync.changed.mockClear();

    vi.mocked(fetch).mockResolvedValueOnce({ ok: false } as Response);
    await playerStore.startQueue(QUEUE, 1);
    expect(sync.changed).toHaveBeenCalled(); // the session hears what is really going on
    expect(localPlayback.state()).toMatchObject({
      song: { id: 1 },
      queueIndex: 0,
      positionMs: 20_000,
      isPlaying: true,
      loading: false
    });
  });

  it('is loading only while the pre-flight is on its way', async () => {
    const { playerStore, localPlayback } = await load();
    let answer!: (value: Response) => void;
    vi.mocked(fetch).mockImplementationOnce(
      () => new Promise<Response>((resolve) => (answer = resolve))
    );
    const picked = playerStore.startQueue(QUEUE, 1);
    expect(localPlayback.state()).toMatchObject({ song: { id: 2 }, positionMs: 0, loading: true });
    answer({ ok: false } as Response);
    await picked;
    // Nothing else was loaded: the pick is what the queue holds, paused at its start.
    expect(localPlayback.state()).toMatchObject({
      song: { id: 2 },
      isPlaying: false,
      loading: false
    });
  });
});

describe('a pick still in its pre-flight when the session pauses this device', () => {
  // A Next pressed while song 1 still plays: song 1 is what is heard, so that is what stays.
  it('is dropped: it never reaches the element, and the song still there is what is reported', async () => {
    const { playerStore, localPlayback, setPlaybackSync } = await load();
    setPlaybackSync(fakeSync());
    await playerStore.startQueue(QUEUE, 0);
    audio.currentTime = 20;
    let answer!: (value: Response) => void;
    vi.mocked(fetch).mockImplementationOnce(
      () => new Promise<Response>((resolve) => (answer = resolve))
    );
    const picked = playerStore.startQueue(QUEUE, 1);
    expect(localPlayback.loading()?.id).toBe(2);

    // Another device took the session (or sent Pause).
    localPlayback.pause();
    expect(localPlayback.loading()).toBeNull();
    answer({ ok: true } as Response);
    await picked;

    expect(audio.play).toHaveBeenCalledTimes(1);
    expect(audio.src).toBe('/api/mh/songs/1/stream');
    expect(audio.paused).toBe(true);
    expect(reportSongPlayed).not.toHaveBeenCalledWith(2);
    expect(localPlayback.state()).toMatchObject({
      song: { id: 1 },
      queueIndex: 0,
      positionMs: 20_000,
      isPlaying: false,
      loading: false
    });
    // The queue is back on the song that is here: Next goes to the pick, not past it.
    playerStore.playNext();
    await settle();
    expect(audio.src).toBe('/api/mh/songs/2/stream');
  });

  // The auto-advance after song 1 ended: there is no song 1 to go back to, only one to replay.
  it('after the song before it ended, waits on the element paused at 0:00: Resume plays it', async () => {
    const { playerStore, localPlayback, setPlaybackSync } = await load();
    setPlaybackSync(fakeSync());
    await playerStore.startQueue(QUEUE, 0);
    let answer!: (value: Response) => void;
    vi.mocked(fetch).mockImplementationOnce(
      () => new Promise<Response>((resolve) => (answer = resolve))
    );
    // Song 1 plays out — back at 0:00, paused — and the queue moves on to song 2's pre-flight.
    audio.finish();
    expect(localPlayback.loading()?.id).toBe(2);

    // Another device sent Pause (or took the session).
    localPlayback.pause();
    expect(localPlayback.loading()).toBeNull();
    answer({ ok: true } as Response);
    await settle();

    // Song 2 is on the element, paused at its start, where Android's player waits too.
    expect(audio.play).toHaveBeenCalledTimes(1); // song 1's, and nothing since
    expect(audio.src).toBe('/api/mh/songs/2/stream');
    expect(audio.paused).toBe(true);
    expect(localPlayback.state()).toMatchObject({
      song: { id: 2 },
      queueIndex: 1,
      positionMs: 0,
      isPlaying: false,
      loading: false
    });
    expect(playerStore.currentSong?.id).toBe(2);
    expect(reportSongPlayed).not.toHaveBeenCalledWith(2); // not heard yet

    // Resume plays song 2 (not song 1 again from 0:00), and that is its listen.
    await localPlayback.resume();
    expect(audio.play).toHaveBeenCalledTimes(2);
    expect(audio.src).toBe('/api/mh/songs/2/stream');
    expect(audio.paused).toBe(false);
    expect(reportSongPlayed).toHaveBeenCalledWith(2);
    expect(reportSongPlayed).toHaveBeenCalledTimes(2); // song 1's, then song 2's once
    await localPlayback.resume();
    expect(reportSongPlayed).toHaveBeenCalledTimes(2);
  });
});

describe('a track the element cannot play', () => {
  it('is not playing, whatever `paused` still says', async () => {
    const { playerStore, localPlayback, setPlaybackSync } = await load();
    const sync = fakeSync();
    setPlaybackSync(sync);
    await playerStore.startQueue(QUEUE, 0);
    sync.changed.mockClear();

    // As the media error steps leave it: `play()` was called, the source failed, and `paused` is
    // never set back — only the error says anything happened. (What the store then does about it —
    // reload, skip, give up — is the player's recovery; see playback-sync's test of giving up.)
    audio.error = { code: 2 };
    expect(audio.paused).toBe(false);
    expect(localPlayback.playing()).toBe(false);
    expect(localPlayback.state()).toMatchObject({ song: { id: 1 }, isPlaying: false });

    // A new load clears the error: the next track plays, and says so.
    playerStore.playNext();
    await settle();
    expect(localPlayback.state()).toMatchObject({ song: { id: 2 }, isPlaying: true });
  });
});

describe('localPlayback.adopt', () => {
  it('plays at the position with nothing awaited before play(), and is not a new listen', async () => {
    const { localPlayback, playerStore } = await load();
    const outcome = localPlayback.adopt({
      queue: QUEUE,
      index: 1,
      positionSec: 83,
      radioSeedId: 1
    });
    // Synchronously: the element has the track, the second, and play() was called.
    expect(audio.src).toBe('/api/mh/songs/2/stream');
    expect(audio.currentTime).toBe(83);
    expect(audio.play).toHaveBeenCalledTimes(1);
    expect(fetch).not.toHaveBeenCalled();
    await expect(outcome).resolves.toBe('played');
    expect(playerStore.currentSong?.id).toBe(2);
    expect(playerStore.hasNext).toBe(true);
    expect(reportSongPlayed).not.toHaveBeenCalled();
    expect(notePlayed).not.toHaveBeenCalled();
  });

  it('counts a listen when it starts another song (Next or Previous onto it)', async () => {
    const { localPlayback } = await load();
    localPlayback.adopt({ queue: QUEUE, index: 2, positionSec: 0, radioSeedId: 1, listen: true });
    expect(reportSongPlayed).toHaveBeenCalledWith(3);
  });

  it('turns stand-ins into rows once the library has them, dropping one it lacks', async () => {
    const { localPlayback, playerStore } = await load();
    const standIn = (id: number): PlayerSong => ({ ...song(id), title: 'Unknown title', standIn: true });
    localPlayback.adopt({
      queue: [standIn(1), standIn(5), standIn(2), standIn(3)],
      index: 2,
      positionSec: 10,
      radioSeedId: 1
    });
    const { toPlayerSong } = await import('$lib/api-client');
    vi.mocked(toPlayerSong).mockImplementation((row) => song(row.id));
    const rows = new Map([1, 2, 3].map((id) => [id, { id } as never]));
    localPlayback.hydrate(rows);
    expect(localPlayback.state()).toMatchObject({ queue: [1, 2, 3], queueIndex: 1 });
    expect(playerStore.currentSong).toMatchObject({ id: 2, title: 'Song 2' });
    expect(playerStore.currentSong?.standIn).toBeUndefined();
    // Played on as it is: the element was not touched.
    expect(audio.play).toHaveBeenCalledTimes(1);
  });

  it('says when the browser refused to start it', async () => {
    const { localPlayback, initPlayer } = await load();
    initPlayer();
    audio.playResult = 'blocked';
    const outcome = localPlayback.adopt({ queue: QUEUE, index: 0, positionSec: 0, radioSeedId: null });
    await expect(outcome).resolves.toBe('blocked');
  });

  it('reports its state from the queue item, before the element catches up', async () => {
    const { localPlayback, playerStore } = await load();
    await playerStore.startQueue(QUEUE, 0);
    audio.currentTime = 12.5;
    expect(localPlayback.state()).toMatchObject({
      song: { id: 1 },
      queue: [1, 2, 3],
      queueIndex: 0,
      positionMs: 12_500,
      isPlaying: true,
      loading: false
    });
  });
});

describe('the reload restore', () => {
  function storeSnapshot(savedAt = Date.now()) {
    session.set(
      'mh:playback',
      JSON.stringify({
        v: 1,
        userId: 'u1',
        queue: QUEUE,
        queueIndex: 1,
        position: 30,
        wasPlaying: true,
        volume: 1,
        radioSeedId: 1,
        radioExhausted: false,
        miniPlayerDismissed: false,
        savedAt
      })
    );
  }

  it('waits for the gate, and stays quiet when another device is live', async () => {
    storeSnapshot();
    const { initPlayer, setPlaybackSync } = await load();
    const sync = fakeSync();
    sync.restoreGate.mockResolvedValue(false);
    setPlaybackSync(sync);
    initPlayer('u1');
    await settle();
    expect(sync.restoreGate).toHaveBeenCalled();
    expect(audio.play).not.toHaveBeenCalled();
    expect(audio.src).toBe('/api/mh/songs/2/stream');
  });

  it('carries on and claims when the gate allows it', async () => {
    storeSnapshot();
    const { initPlayer, setPlaybackSync } = await load();
    const sync = fakeSync();
    setPlaybackSync(sync);
    initPlayer('u1');
    await settle();
    expect(audio.play).toHaveBeenCalledTimes(1);
    expect(sync.playIntent).toHaveBeenCalledTimes(1);
    expect(reportSongPlayed).not.toHaveBeenCalled();
  });

  it('resumes at once without a session plugged in, as before', async () => {
    storeSnapshot();
    const { initPlayer } = await load();
    initPlayer('u1');
    expect(audio.play).toHaveBeenCalledTimes(1);
  });
});
