import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ApiSong, PlaybackStreamHandlers } from '$lib/api-client';
import type { PlaybackSession, PlaybackStateReport } from '$lib/playback-sync/wire';

/**
 * The sync store driven end to end: the real player on a fake `<audio>`, a hand-driven stream in
 * place of `EventSource`, and the three playback calls stubbed. Each case is one of the contract's
 * client behaviours — a play intent claims, a device that is superseded steps aside, commands are
 * carried out and acknowledged, a session is picked up without counting a listen, the transport
 * steers the device that holds it, and a reload stays quiet while another device plays.
 */

const toast = vi.hoisted(() =>
  Object.assign(vi.fn(), { error: vi.fn(), success: vi.fn(), info: vi.fn() })
);
const api = vi.hoisted(() => ({
  handlers: null as PlaybackStreamHandlers | null,
  opened: 0,
  reportPlaybackState: vi.fn(),
  sendPlaybackCommand: vi.fn(),
  fetchPlayback: vi.fn(),
  reportSongPlayed: vi.fn(async () => {})
}));
/** The library's rows; a test that needs them reactive swaps in a `SvelteMap`. */
const library = vi.hoisted(() => ({ rows: new Map<number, unknown>() }));

vi.mock('$app/environment', () => ({ browser: true }));
vi.mock('svelte-sonner', () => ({ toast }));
vi.mock('$lib/hooks/viewport-insets.svelte', () => ({ isInstalledApp: () => false }));
vi.mock('$lib/track-list-view.svelte', () => ({ artistOf: () => 'Artist' }));
vi.mock('$lib/stores/songs.svelte', () => ({
  songsStore: {
    notePlayed: vi.fn(),
    get songsById() {
      return library.rows;
    }
  }
}));
vi.mock('$lib/api-client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('$lib/api-client')>();
  return {
    ...actual,
    fetchRadio: vi.fn(async () => []),
    reportSongPlayed: api.reportSongPlayed,
    fetchPlayback: api.fetchPlayback,
    reportPlaybackState: api.reportPlaybackState,
    sendPlaybackCommand: api.sendPlaybackCommand,
    openPlaybackStream: (_device: unknown, handlers: PlaybackStreamHandlers) => {
      api.handlers = handlers;
      api.opened += 1;
      return () => {
        api.handlers = null;
      };
    }
  };
});

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
  /** `play()` stays unanswered (the stream buffering) until `finishLoading()`. */
  hold = false;
  private waiting: { resolve: () => void; reject: (err: unknown) => void } | null = null;
  private listeners = new Map<string, ((e: Event) => void)[]>();
  addEventListener(type: string, fn: (e: Event) => void) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), fn]);
  }
  dispatch(type: string) {
    for (const fn of this.listeners.get(type) ?? []) fn(new Event(type));
  }
  setAttribute() {}
  removeAttribute() {}
  load() {
    this.currentTime = 0;
    this.paused = true;
    this.readyState = 0;
    this.error = null;
  }
  /**
   * The track plays out. The element loops (see the player's `songFinished`), so the end is audio
   * flowing near it and then the jump back to 0:00, never an `ended`.
   */
  end() {
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
    this.dispatch('play');
    if (!this.hold) return Promise.resolve();
    return new Promise<void>((resolve, reject) => (this.waiting = { resolve, reject }));
  });
  finishLoading() {
    this.waiting?.resolve();
    this.waiting = null;
  }
  pause() {
    if (this.paused) return;
    this.paused = true;
    // As the real element does: a pause aborts a play() still waiting to start.
    this.waiting?.reject(new DOMException('interrupted by pause()', 'AbortError'));
    this.waiting = null;
    this.dispatch('pause');
  }
}

const ME = 'tab-mac-0001';
const PHONE = 'phone-00001';
const USER = { id: 'u1', email: 'a@example.com', role: 'Owner' as const, displayName: null };

let audio: FakeAudio | undefined;
let sessionStore: Map<string, string>;
/** Every session the "server" produces is newer than the last, as versions are. */
let version = 0;
const nextVersion = () => ++version;

beforeEach(() => {
  vi.resetModules();
  vi.useFakeTimers({
    toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date']
  });
  toast.mockClear();
  toast.error.mockClear();
  api.handlers = null;
  api.opened = 0;
  audio = undefined;
  api.reportPlaybackState.mockReset();
  // The server's answer to an accepted report: the session as this device just described it.
  api.reportPlaybackState.mockImplementation(async (report: PlaybackStateReport) => ({
    accepted: true,
    session: remoteSession({
      songId: report.songId,
      queue: report.queue ?? [report.songId],
      queueIndex: report.queue ? report.queueIndex : 0,
      positionMs: report.positionMs,
      isPlaying: report.isPlaying,
      radioSeedId: report.radioSeedId,
      activeDeviceId: report.deviceId,
      activeDeviceName: report.deviceName,
      lastCommandId: report.inResponseTo
    })
  }));
  api.sendPlaybackCommand.mockReset();
  api.fetchPlayback.mockReset();
  api.fetchPlayback.mockResolvedValue({ session: null, devices: [] });
  api.reportSongPlayed.mockClear();
  library.rows = new Map();
  for (const id of [1, 2, 3]) library.rows.set(id, row(id));

  sessionStore = new Map([['mh:device-id', ME]]);
  const storage = (map: Map<string, string>) => ({
    getItem: (k: string) => map.get(k) ?? null,
    setItem: (k: string, v: string) => void map.set(k, v),
    removeItem: (k: string) => void map.delete(k)
  });
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
  vi.stubGlobal('BroadcastChannel', undefined);
  vi.stubGlobal('navigator', {
    userAgent:
      'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15',
    maxTouchPoints: 0,
    mediaSession: {
      metadata: null,
      playbackState: 'none',
      setActionHandler: () => {},
      setPositionState: () => {}
    }
  });
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => ({ ok: true }))
  );
  vi.stubGlobal('document', {
    hidden: false,
    visibilityState: 'visible',
    addEventListener: () => {}
  });
  vi.stubGlobal('window', {
    sessionStorage: storage(sessionStore),
    localStorage: storage(new Map([['mh:install-id', 'install-0001']])),
    addEventListener: () => {}
  });
  vi.stubGlobal('requestAnimationFrame', () => 0);
  vi.stubGlobal('cancelAnimationFrame', () => {});
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

function row(id: number): ApiSong {
  return {
    id,
    sourcePath: `/music/${id}.flac`,
    fileName: `${id}.flac`,
    fileSizeBytes: 1,
    title: `Song ${id}`,
    artist: 'Artist',
    durationSeconds: 200
  } as ApiSong;
}

const song = (id: number) => ({
  id,
  title: `Song ${id}`,
  artist: 'Artist',
  streamUrl: `/api/mh/songs/${id}/stream`
});
const QUEUE = [song(1), song(2), song(3)];

function remoteSession(overrides: Partial<PlaybackSession> = {}): PlaybackSession {
  return {
    version: nextVersion(),
    songId: 2,
    title: 'Song 2',
    artist: 'Artist',
    album: null,
    queue: [1, 2, 3],
    queueIndex: 1,
    positionMs: 30_000,
    durationMs: 200_000,
    isPlaying: false,
    playbackRate: 1,
    radioSeedId: 7,
    shuffle: false,
    activeDeviceId: PHONE,
    activeDeviceName: 'Safari on iPhone',
    live: true,
    lastCommandId: null,
    updatedAtUtc: null,
    ...overrides
  };
}

const phoneDevice = {
  deviceId: PHONE,
  installId: 'install-phone',
  name: 'Safari on iPhone',
  kind: 'phone' as const,
  client: 'web' as const,
  online: true,
  isActive: true
};

/** Let promise chains and anything due within `ms` run. */
const flush = (ms = 0) => vi.advanceTimersByTimeAsync(ms);

async function boot(user: Parameters<typeof import('./playback-sync.svelte').playbackSync.start>[0] = USER) {
  const player = await import('./player.svelte');
  const { playbackSync } = await import('./playback-sync.svelte');
  playbackSync.start(user);
  await flush(); // the tab's id settles, the stream opens
  return { ...player, playbackSync };
}

function reports(): PlaybackStateReport[] {
  return api.reportPlaybackState.mock.calls.map((call) => call[0] as PlaybackStateReport);
}

describe('a local play intent', () => {
  it('plays here, then claims the session with the whole queue', async () => {
    const { playerStore } = await boot();
    api.handlers!.onSnapshot({ session: null, devices: [] });

    await playerStore.startQueue(QUEUE, 0);
    expect(audio!.play).toHaveBeenCalledTimes(1);
    await flush(100);

    expect(reports()).toHaveLength(1);
    expect(reports()[0]).toMatchObject({
      deviceId: ME,
      installId: 'install-0001',
      deviceName: 'Safari on Mac',
      deviceKind: 'computer',
      client: 'web',
      claim: true,
      inResponseTo: null,
      songId: 1,
      queue: [1, 2, 3],
      queueIndex: 0,
      isPlaying: true
    });
  });

  it('is sent once the tab knows its id, not lost before', async () => {
    const player = await import('./player.svelte');
    const { playbackSync } = await import('./playback-sync.svelte');
    playbackSync.start(USER);
    // The id is still settling: the intent waits for it rather than going nowhere.
    void player.playerStore.startQueue(QUEUE, 0);
    await flush(100);
    expect(reports()).toHaveLength(1);
    expect(reports()[0]).toMatchObject({ claim: true, deviceId: ME });
  });
});

describe('when the session moves to another device', () => {
  it('pauses here and says where the music went, offering Play here', async () => {
    const { playerStore } = await boot();
    api.handlers!.onSnapshot({ session: null, devices: [] });
    await playerStore.startQueue(QUEUE, 0);
    await flush(100);
    expect(audio!.paused).toBe(false);

    api.handlers!.onDevices([phoneDevice]);
    api.handlers!.onSession(remoteSession({ isPlaying: true }));

    expect(audio!.paused).toBe(true);
    expect(toast).toHaveBeenCalledWith(
      'Now playing on Safari on iPhone',
      expect.objectContaining({ action: expect.objectContaining({ label: 'Play here' }) })
    );
    // The player now shows the session, not the paused leftovers.
    expect(playerStore.currentSong?.id).toBe(2);
    expect(playerStore.isPlaying).toBe(true);
  });

  it('steps aside when a report is refused, too', async () => {
    const { playerStore } = await boot();
    api.handlers!.onSnapshot({ session: null, devices: [] });
    await playerStore.startQueue(QUEUE, 0);
    await flush(100);
    api.handlers!.onSession(remoteSession({ songId: 1, queueIndex: 0, activeDeviceId: ME }));

    // Suspended through the move (an iPhone in a pocket): the next report learns of it.
    api.reportPlaybackState.mockResolvedValueOnce({
      accepted: false,
      session: remoteSession({ isPlaying: true })
    });
    playerStore.seek(12);
    await flush(1000);

    expect(reports().at(-1)).toMatchObject({ claim: false, positionMs: 12_000 });
    expect(audio!.paused).toBe(true);
    expect(toast).toHaveBeenCalledTimes(1);
  });

  it('does not step aside for a session of its own in flight', async () => {
    const { playerStore } = await boot();
    api.handlers!.onSnapshot({ session: remoteSession({ isPlaying: true }), devices: [phoneDevice] });
    await playerStore.startQueue(QUEUE, 0); // claims; the answer has not come back yet
    api.handlers!.onSession(remoteSession({ isPlaying: true }));
    expect(audio!.paused).toBe(false);
    expect(toast).not.toHaveBeenCalled();
  });

  it('steps aside once its claim is answered, when another device claimed just after it', async () => {
    const { playerStore } = await boot();
    api.handlers!.onSnapshot({ session: null, devices: [phoneDevice] });
    let answer!: (response: unknown) => void;
    api.reportPlaybackState.mockImplementationOnce(() => new Promise((resolve) => (answer = resolve)));
    await playerStore.startQueue(QUEUE, 0);
    await flush(100); // the claim is on its way
    const ours = remoteSession({ songId: 1, queueIndex: 0, isPlaying: true, activeDeviceId: ME });
    // The phone's claim, which the server took after ours, reaches the stream before our answer.
    api.handlers!.onSession(remoteSession({ isPlaying: true }));
    expect(audio!.paused).toBe(false);

    answer({ accepted: true, session: ours });
    await flush();
    expect(audio!.paused).toBe(true);
    expect(toast).toHaveBeenCalledWith('Now playing on Safari on iPhone', expect.anything());
  });

  it("never stops a share link's track: it plays on, and stays on screen", async () => {
    // Started on the share page, then a soft navigation into the app: the session starts after.
    const player = await import('./player.svelte');
    const shared = { id: 5, title: 'Shared', artist: 'Friend', streamUrl: '/api/mh/share/tok/5/stream' };
    await player.playerStore.startQueue([shared], 0);
    const { playbackSync } = await import('./playback-sync.svelte');
    playbackSync.start(USER);
    await flush();

    api.handlers!.onSnapshot({ session: remoteSession({ live: false }), devices: [] });
    api.handlers!.onDevices([phoneDevice]);
    api.handlers!.onSession(remoteSession({ isPlaying: true }));
    expect(audio!.paused).toBe(false);
    expect(toast).not.toHaveBeenCalled();
    expect(playbackSync.showsSession).toBe(false);
    expect(player.playerStore.currentSong?.id).toBe(5);
    await flush(30_000);
    expect(reports()).toHaveLength(0);
  });
});

describe('a pick still in its pre-flight', () => {
  /** This tab holds the session, playing song 1; the next pick's pre-flight waits for `release`. */
  async function holdingWithSlowPreflight() {
    const booted = await boot();
    api.handlers!.onSnapshot({ session: null, devices: [phoneDevice] });
    await booted.playerStore.startQueue(QUEUE, 0);
    await flush(100);
    expect(reports().at(-1)).toMatchObject({ claim: true, songId: 1, isPlaying: true });
    // The media sits behind a network mount: opening the next file takes a moment.
    let answer: ((res: Response) => void) | null = null;
    vi.mocked(fetch).mockImplementationOnce(
      () => new Promise<Response>((resolve) => (answer = resolve))
    );
    return { ...booted, release: () => answer?.({ ok: true } as Response) };
  }

  it('never starts once another device took the session after the song before it ended', async () => {
    const { release } = await holdingWithSlowPreflight();
    // Song 1 ends: the element stops, and the queue moves on to song 2's pre-flight.
    audio!.end();
    expect(fetch).toHaveBeenLastCalledWith('/api/mh/songs/2/stream', expect.anything());

    // Play on the phone meanwhile: its claim reaches this tab while nothing here is audible.
    api.handlers!.onSession(remoteSession({ isPlaying: true }));
    expect(toast).toHaveBeenCalledWith('Now playing on Safari on iPhone', expect.anything());

    release();
    await flush(100);
    expect(audio!.play).toHaveBeenCalledTimes(1); // song 1's, and nothing since
    // Song 2 waits on the element, paused at its start — never played.
    expect(audio!.src).toBe('/api/mh/songs/2/stream');
    expect(audio!.paused).toBe(true);
    expect(api.reportSongPlayed).not.toHaveBeenCalledWith(2);
  });

  it('sent to the phone from this tab, steps aside for its claim without calling it news', async () => {
    const { release, playbackSync } = await holdingWithSlowPreflight();
    api.sendPlaybackCommand.mockResolvedValue('c-transfer');
    playbackSync.choose(playbackSync.entries.find((e) => e.deviceId === PHONE)!);
    await flush(); // sent; the phone is picking it up
    // Song 1 ends meanwhile, and the queue moves on to song 2's pre-flight.
    audio!.end();

    // The phone's claim answering the transfer.
    api.handlers!.onSession(remoteSession({ isPlaying: true, lastCommandId: 'c-transfer' }));
    expect(toast).not.toHaveBeenCalled(); // no "Now playing on …" with a Play here to undo it

    release();
    await flush(6_000);
    expect(audio!.play).toHaveBeenCalledTimes(1); // song 1's, and nothing since
    expect(audio!.paused).toBe(true);
    expect(toast).not.toHaveBeenCalled();
    expect(toast.error).not.toHaveBeenCalled();
  });

  it('never starts once the phone that sent Next took the session back', async () => {
    const { release } = await holdingWithSlowPreflight();
    api.handlers!.onCommand({
      commandId: 'c-next',
      command: 'next',
      positionMs: null,
      fromDeviceId: PHONE,
      fromDeviceName: 'Safari on iPhone'
    });
    await flush(100); // acknowledged; song 1 plays on while song 2's pre-flight is out

    // "This device" on the phone.
    api.handlers!.onSession(remoteSession({ isPlaying: true }));
    expect(audio!.paused).toBe(true);

    release();
    await flush(100);
    expect(audio!.play).toHaveBeenCalledTimes(1);
    expect(audio!.src).toBe('/api/mh/songs/1/stream');
    expect(audio!.paused).toBe(true);
  });

  it('a Pause from the phone holds, and its Resume plays the song that was coming next', async () => {
    const { release } = await holdingWithSlowPreflight();
    audio!.end();
    api.handlers!.onCommand({
      commandId: 'c-pause',
      command: 'pause',
      positionMs: null,
      fromDeviceId: PHONE,
      fromDeviceName: 'Safari on iPhone'
    });
    await flush(100);
    // The acknowledgement says what is here: song 2, paused at its start — not about to play, and
    // not song 1 at its end, which a Resume would replay from the top.
    expect(reports().at(-1)).toMatchObject({
      inResponseTo: 'c-pause',
      songId: 2,
      queueIndex: 1,
      positionMs: 0,
      isPlaying: false
    });

    release();
    await flush(100);
    expect(audio!.play).toHaveBeenCalledTimes(1);
    expect(audio!.paused).toBe(true);

    // The phone, showing song 2 paused at 0:00, sends Resume: song 2 plays here, as it would on
    // Android in the same spot — and that is its listen.
    api.handlers!.onCommand({
      commandId: 'c-resume',
      command: 'resume',
      positionMs: null,
      fromDeviceId: PHONE,
      fromDeviceName: 'Safari on iPhone'
    });
    await flush(100);
    expect(audio!.play).toHaveBeenCalledTimes(2);
    expect(audio!.src).toBe('/api/mh/songs/2/stream');
    expect(audio!.currentTime).toBe(0);
    expect(api.reportSongPlayed).toHaveBeenLastCalledWith(2);
    expect(reports().at(-1)).toMatchObject({
      inResponseTo: 'c-resume',
      songId: 2,
      queueIndex: 1,
      isPlaying: true
    });
  });
});

describe('a pick whose file is missing', () => {
  it('is not reported as playing: what still plays here is', async () => {
    const { playerStore } = await boot();
    api.handlers!.onSnapshot({ session: null, devices: [] });
    await playerStore.startQueue(QUEUE, 0);
    await flush(100);
    api.handlers!.onSession(remoteSession({ songId: 1, queueIndex: 0, activeDeviceId: ME }));
    api.reportPlaybackState.mockClear();

    vi.mocked(fetch).mockResolvedValueOnce({ ok: false } as Response);
    await playerStore.startQueue(QUEUE, 1);
    await flush(20_100); // the claim, then a heartbeat
    expect(reports().length).toBeGreaterThanOrEqual(2);
    for (const report of reports()) {
      expect(report).toMatchObject({ songId: 1, queueIndex: 0, isPlaying: true });
    }
  });

  it('claims paused when nothing else is loaded here, never "playing at 0:00"', async () => {
    const { playerStore } = await boot();
    api.handlers!.onSnapshot({ session: remoteSession({ isPlaying: true }), devices: [phoneDevice] });
    vi.mocked(fetch).mockResolvedValueOnce({ ok: false } as Response);
    await playerStore.startQueue(QUEUE, 0);
    await flush(100);
    expect(reports().at(-1)).toMatchObject({ claim: true, songId: 1, positionMs: 0, isPlaying: false });
  });
});

describe('a track the element cannot play', () => {
  it('is reported stopped once the player gives up on it, and every heartbeat after says the same', async () => {
    const { playerStore } = await boot();
    api.handlers!.onSnapshot({ session: null, devices: [] });
    await playerStore.startQueue(QUEUE, 0);
    await flush(100);
    api.handlers!.onSession(
      remoteSession({ songId: 1, queueIndex: 0, isPlaying: true, activeDeviceId: ME })
    );
    api.reportPlaybackState.mockClear();

    // The stream drops: `play` already said "playing", then the source fails — `error` fires and
    // `paused` is never set back. The player schedules a reload from the same second, but iOS
    // suspends the page before it can run; the late retry gives up, paused where it got to.
    audio!.error = { code: 2 };
    audio!.dispatch('error');
    vi.setSystemTime(Date.now() + 60_000);
    await flush(1_000); // the retry comes due, late: it gives up
    await flush(1_000);
    expect(reports()).toHaveLength(1);
    expect(reports()[0]).toMatchObject({ claim: false, songId: 1, isPlaying: false });

    await flush(40_000);
    expect(reports().length).toBeGreaterThan(1);
    for (const report of reports()) expect(report.isPlaying).toBe(false);
  });
});

describe('commands addressed to this device', () => {
  async function holding() {
    const booted = await boot();
    api.handlers!.onSnapshot({ session: null, devices: [] });
    await booted.playerStore.startQueue(QUEUE, 0);
    await flush(100);
    api.handlers!.onSession(remoteSession({ songId: 1, queueIndex: 0, activeDeviceId: ME }));
    api.reportPlaybackState.mockClear();
    return booted;
  }

  it('pause and seek act here and are acknowledged', async () => {
    await holding();
    api.handlers!.onCommand({
      commandId: 'c-pause',
      command: 'pause',
      positionMs: null,
      fromDeviceId: PHONE,
      fromDeviceName: 'Safari on iPhone'
    });
    expect(audio!.paused).toBe(true);
    await flush(100);
    expect(reports().at(-1)).toMatchObject({ claim: false, inResponseTo: 'c-pause', isPlaying: false });

    api.handlers!.onCommand({
      commandId: 'c-seek',
      command: 'seek',
      positionMs: 90_000,
      fromDeviceId: PHONE,
      fromDeviceName: 'Safari on iPhone'
    });
    expect(audio!.currentTime).toBe(90);
    await flush(100);
    expect(reports().at(-1)).toMatchObject({ inResponseTo: 'c-seek', positionMs: 90_000 });
  });

  it('a transfer picks the session up at its position and claims it, without a new listen', async () => {
    const { playerStore } = await boot();
    api.handlers!.onSnapshot({ session: remoteSession(), devices: [phoneDevice] });

    api.handlers!.onCommand({
      commandId: 'c-transfer',
      command: 'transfer',
      positionMs: null,
      fromDeviceId: PHONE,
      fromDeviceName: 'Safari on iPhone'
    });
    // Synchronously: the session's song, at its second, asked to play.
    expect(audio!.src).toBe('/api/mh/songs/2/stream');
    expect(audio!.currentTime).toBe(30);
    expect(audio!.play).toHaveBeenCalledTimes(1);
    await flush(100);

    expect(reports().at(-1)).toMatchObject({
      claim: true,
      inResponseTo: 'c-transfer',
      songId: 2,
      queue: [1, 2, 3],
      queueIndex: 1,
      radioSeedId: 7
    });
    expect(api.reportSongPlayed).not.toHaveBeenCalled();
    expect(playerStore.currentSong?.id).toBe(2);
  });

  it("does not step aside for the sender's own report while the transfer is still starting", async () => {
    const { initPlayer } = await boot();
    api.handlers!.onSnapshot({ session: remoteSession({ isPlaying: true }), devices: [phoneDevice] });
    initPlayer(); // the element exists before the command arrives
    audio!.hold = true; // its stream takes a moment to buffer

    api.handlers!.onCommand({
      commandId: 'c-transfer',
      command: 'transfer',
      positionMs: null,
      fromDeviceId: PHONE,
      fromDeviceName: 'Safari on iPhone'
    });
    // The phone's heartbeat lands meanwhile, still naming the phone.
    api.handlers!.onSession(remoteSession({ isPlaying: true, positionMs: 50_000 }));
    expect(audio!.paused).toBe(false);
    expect(toast).not.toHaveBeenCalled();
    expect(reports()).toHaveLength(0); // no claim before it plays

    audio!.finishLoading();
    await flush(100);
    expect(reports().at(-1)).toMatchObject({ claim: true, inResponseTo: 'c-transfer', songId: 2 });
  });

  it('a transfer the browser will not play is not claimed; it asks for a tap', async () => {
    const { initPlayer } = await boot();
    api.handlers!.onSnapshot({ session: remoteSession(), devices: [phoneDevice] });
    initPlayer(); // the element exists before the command arrives
    audio!.playResult = 'blocked';

    api.handlers!.onCommand({
      commandId: 'c-transfer',
      command: 'transfer',
      positionMs: null,
      fromDeviceId: PHONE,
      fromDeviceName: 'Safari on iPhone'
    });
    await flush(100);

    expect(reports().filter((r) => r.claim)).toHaveLength(0);
    expect(toast).toHaveBeenCalledWith(
      'Tap to play here',
      expect.objectContaining({ action: expect.objectContaining({ label: 'Play here' }) })
    );
  });
});

describe('steering the device that holds the session', () => {
  it('sends the transport as commands and says when one does not land', async () => {
    const { playerStore } = await boot();
    api.handlers!.onSnapshot({
      session: remoteSession({ isPlaying: true }),
      devices: [phoneDevice]
    });
    api.sendPlaybackCommand.mockResolvedValue('cmd-1');

    playerStore.togglePlay();
    expect(api.sendPlaybackCommand).toHaveBeenCalledWith({
      fromDeviceId: ME,
      targetDeviceId: PHONE,
      command: 'pause',
      positionMs: null
    });
    // Shown paused at once, without waiting for the other device; nothing here was touched (no
    // element was even created).
    expect(playerStore.isPlaying).toBe(false);
    expect(audio).toBeUndefined();

    await flush(5_000);
    expect(toast.error).toHaveBeenCalledWith('Couldn’t reach Safari on iPhone', undefined);
  });

  it('a burst acknowledged by its newest command has landed in full', async () => {
    const { playerStore } = await boot();
    api.handlers!.onSnapshot({ session: remoteSession({ isPlaying: true }), devices: [phoneDevice] });
    api.sendPlaybackCommand
      .mockResolvedValueOnce('n1')
      .mockResolvedValueOnce('n2')
      .mockResolvedValueOnce('n3');

    playerStore.playNext();
    playerStore.playNext();
    playerStore.playNext();
    await flush();
    // The phone ran all three and reported once, naming the newest.
    api.handlers!.onSession(remoteSession({ isPlaying: true, lastCommandId: 'n3' }));
    await flush(6_000);
    expect(toast.error).not.toHaveBeenCalled();
  });

  it('an older command answered after a newer one landed has landed too', async () => {
    const { playerStore } = await boot();
    api.handlers!.onSnapshot({ session: remoteSession({ isPlaying: true }), devices: [phoneDevice] });
    let answerFirst!: (id: string) => void;
    api.sendPlaybackCommand
      .mockImplementationOnce(() => new Promise((resolve) => (answerFirst = resolve)))
      .mockResolvedValueOnce('n2');

    playerStore.playNext();
    playerStore.playNext();
    await flush();
    api.handlers!.onSession(remoteSession({ isPlaying: true, lastCommandId: 'n2' }));
    answerFirst('n1'); // its own answer comes back last
    await flush(6_000);
    expect(toast.error).not.toHaveBeenCalled();
  });

  it("an album's Play on the session's song resumes it there, not here from the top", async () => {
    const { playerStore } = await boot();
    api.handlers!.onSnapshot({ session: remoteSession(), devices: [phoneDevice] });
    api.sendPlaybackCommand.mockResolvedValue('cmd-3');

    await playerStore.startQueue(QUEUE, 1, { resumeIfLoaded: true });
    expect(api.sendPlaybackCommand).toHaveBeenCalledWith(expect.objectContaining({ command: 'resume' }));
    expect(audio).toBeUndefined();
    expect(api.reportSongPlayed).not.toHaveBeenCalled();
  });

  it("Shuffle landing on the session's song plays the shuffled list here and takes the session", async () => {
    const { playerStore, playbackSync } = await boot();
    api.handlers!.onSnapshot({ session: remoteSession({ isPlaying: true }), devices: [phoneDevice] });

    await playerStore.startQueue([song(2), song(3), song(1)], 0);
    expect(api.sendPlaybackCommand).not.toHaveBeenCalled();
    expect(audio!.src).toBe('/api/mh/songs/2/stream');
    expect(audio!.currentTime).toBe(0);
    expect(audio!.paused).toBe(false);
    expect(api.reportSongPlayed).toHaveBeenCalledWith(2);
    await flush(100);
    expect(reports().at(-1)).toMatchObject({
      claim: true,
      songId: 2,
      queue: [2, 3, 1],
      queueIndex: 0,
      positionMs: 0,
      isPlaying: true
    });
    expect(playbackSync.mode).toBe('local');
  });

  it('is satisfied by the acknowledgement', async () => {
    const { playerStore } = await boot();
    api.handlers!.onSnapshot({ session: remoteSession(), devices: [phoneDevice] });
    api.sendPlaybackCommand.mockResolvedValue('cmd-2');

    playerStore.resume();
    await flush();
    api.handlers!.onSession(remoteSession({ isPlaying: true, lastCommandId: 'cmd-2' }));
    await flush(6_000);
    expect(toast.error).not.toHaveBeenCalled();
    expect(playerStore.isPlaying).toBe(true);
  });
});

describe('a remembered session', () => {
  it('shows paused, and Play picks it up here at the saved second', async () => {
    const { playerStore, playbackSync } = await boot();
    api.handlers!.onSnapshot({
      session: remoteSession({ live: false, positionMs: 42_000 }),
      devices: []
    });
    expect(playbackSync.mode).toBe('remembered');
    expect(playbackSync.line).toBe('Last played on Safari on iPhone');
    expect(playerStore.currentSong?.id).toBe(2);
    expect(playerStore.isPlaying).toBe(false);

    playerStore.togglePlay();
    expect(audio!.src).toBe('/api/mh/songs/2/stream');
    expect(audio!.currentTime).toBe(42);
    expect(audio!.play).toHaveBeenCalledTimes(1);
    await flush(100);
    expect(reports().at(-1)).toMatchObject({ claim: true, songId: 2, radioSeedId: 7 });
    expect(api.reportSongPlayed).not.toHaveBeenCalled();
    expect(playbackSync.mode).toBe('local');
  });

  it('Next picks it up one on, from the top — and that is a new listen', async () => {
    const { playerStore } = await boot();
    api.handlers!.onSnapshot({ session: remoteSession({ live: false, positionMs: 42_000 }), devices: [] });
    playerStore.playNext();
    expect(audio!.src).toBe('/api/mh/songs/3/stream');
    expect(audio!.currentTime).toBe(0);
    expect(api.reportSongPlayed).toHaveBeenCalledWith(3);
    await flush(100);
    expect(reports().at(-1)).toMatchObject({ claim: true, songId: 3, queueIndex: 2 });
  });

  it("an album's Play on its song picks it up at the saved second, not a new listen", async () => {
    const { playerStore } = await boot();
    api.handlers!.onSnapshot({ session: remoteSession({ live: false, positionMs: 42_000 }), devices: [] });
    await playerStore.startQueue(QUEUE, 1, { resumeIfLoaded: true });
    expect(audio!.src).toBe('/api/mh/songs/2/stream');
    expect(audio!.currentTime).toBe(42);
    expect(api.reportSongPlayed).not.toHaveBeenCalled();
  });

  it("a list's Play starting on its song plays that list here, from the top — a new listen", async () => {
    const { playerStore } = await boot();
    api.handlers!.onSnapshot({ session: remoteSession({ live: false, positionMs: 42_000 }), devices: [] });
    await playerStore.startQueue([song(2), song(1)], 0);
    expect(audio!.src).toBe('/api/mh/songs/2/stream');
    expect(audio!.currentTime).toBe(0);
    expect(api.reportSongPlayed).toHaveBeenCalledWith(2);
    await flush(100);
    expect(reports().at(-1)).toMatchObject({ claim: true, songId: 2, queue: [2, 1], queueIndex: 0 });
  });

  it("a reconnect's snapshot of the same session keeps a scrub waiting on it", async () => {
    const { playerStore } = await boot();
    const remembered = remoteSession({ live: false, positionMs: 42_000 });
    api.handlers!.onSnapshot({ session: remembered, devices: [] });
    playerStore.seek(100);
    // The server ends every stream after a few minutes; the reconnect repeats what it said.
    api.handlers!.onSnapshot({ session: remembered, devices: [] });
    expect(playerStore.currentTime).toBe(100);
  });

  it('picks up a song the Tracks list would not show: every /songs row counts', async () => {
    library.rows.set(4, { ...row(4), libraryBuildStatus: 'Pending', destinationPath: null });
    const { playerStore } = await boot();
    api.handlers!.onSnapshot({
      session: remoteSession({ live: false, songId: 4, queue: [1, 4], queueIndex: 1 }),
      devices: []
    });
    playerStore.togglePlay();
    expect(audio!.src).toBe('/api/mh/songs/4/stream');
    expect(toast.error).not.toHaveBeenCalled();
  });

  it('picked up before the library loaded, keeps the whole queue and fills it in later', async () => {
    library.rows = new Map();
    const { playerStore, localPlayback } = await boot();
    api.handlers!.onSnapshot({
      session: remoteSession({ live: false, title: 'From the phone', queue: [1, 2, 99] }),
      devices: []
    });
    playerStore.togglePlay();
    expect(playerStore.currentSong?.title).toBe('From the phone');
    await flush(100);
    // The claim carries the account's queue, not the one song this tab could name.
    expect(reports().at(-1)).toMatchObject({ claim: true, songId: 2, queue: [1, 2, 99], queueIndex: 1 });

    // The rows arrive (the store's effect hands them over): each stand-in becomes its row, and one
    // the library lacks leaves the queue.
    for (const id of [1, 2, 3]) library.rows.set(id, row(id));
    localPlayback.hydrate(library.rows as Map<number, ApiSong>);
    expect(playerStore.currentSong?.title).toBe('Song 2');
    expect(localPlayback.state()).toMatchObject({ queue: [1, 2], queueIndex: 1 });
  });

  it('refuses a song this library does not have', async () => {
    const { playerStore } = await boot();
    api.handlers!.onSnapshot({
      session: remoteSession({ live: false, songId: 99, queue: [1, 99, 3] }),
      devices: []
    });
    playerStore.togglePlay();
    expect(toast.error).toHaveBeenCalledWith('This song isn’t available here');
    expect(reports()).toHaveLength(0);
  });

  describe('sent to the device it was last played on, back online', () => {
    async function transferToPhone() {
      const booted = await boot();
      api.handlers!.onSnapshot({
        session: remoteSession({ live: false, positionMs: 42_000 }),
        devices: [phoneDevice]
      });
      expect(booted.playbackSync.mode).toBe('remembered');
      api.sendPlaybackCommand.mockResolvedValue('c-transfer');
      booted.playbackSync.choose(booted.playbackSync.entries.find((e) => e.deviceId === PHONE)!);
      expect(api.sendPlaybackCommand).toHaveBeenCalledWith(
        expect.objectContaining({ command: 'transfer', targetDeviceId: PHONE })
      );
      await flush();
      return booted;
    }

    it('has not landed just because the session names it: a failed pick-up says so', async () => {
      await transferToPhone();
      // The phone could not start it (its library would not load): no claim ever comes back.
      await flush(5_000);
      expect(toast.error).toHaveBeenCalledWith(
        'Couldn’t reach Safari on iPhone',
        expect.objectContaining({ action: expect.objectContaining({ label: 'Play here' }) })
      );
    });

    it('lands on the claim that answers it', async () => {
      await transferToPhone();
      api.handlers!.onSession(
        remoteSession({ live: true, isPlaying: true, lastCommandId: 'c-transfer' })
      );
      await flush(6_000);
      expect(toast.error).not.toHaveBeenCalled();
    });
  });
});

describe('the reload restore', () => {
  function storeSnapshot() {
    sessionStore.set(
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
        savedAt: Date.now()
      })
    );
  }

  it('stays quiet while another device plays the session', async () => {
    storeSnapshot();
    const { initPlayer } = await boot();
    initPlayer('u1');
    api.handlers!.onSnapshot({ session: remoteSession({ isPlaying: true }), devices: [phoneDevice] });
    await flush(5_000);
    expect(audio!.play).not.toHaveBeenCalled();
  });

  it('carries on when this tab still holds the session', async () => {
    storeSnapshot();
    const { initPlayer } = await boot();
    initPlayer('u1');
    api.handlers!.onSnapshot({ session: remoteSession({ activeDeviceId: ME }), devices: [] });
    await flush();
    expect(audio!.play).toHaveBeenCalledTimes(1);
  });

  it('carries on after 3 s without a snapshot, as before', async () => {
    storeSnapshot();
    const { initPlayer } = await boot();
    initPlayer('u1');
    await flush(2_900);
    expect(audio!.play).not.toHaveBeenCalled();
    await flush(200);
    expect(audio!.play).toHaveBeenCalledTimes(1);
  });
});

describe('the stream reconnecting', () => {
  it('reports at once when it finds this device still holding the session', async () => {
    const { playerStore } = await boot();
    api.handlers!.onSnapshot({ session: null, devices: [] });
    await playerStore.startQueue(QUEUE, 0);
    await flush(100);
    api.reportPlaybackState.mockClear();

    // An API restart: the server remembers the session, not that it was playing.
    api.handlers!.onSnapshot({
      session: remoteSession({ songId: 1, queueIndex: 0, activeDeviceId: ME, live: false }),
      devices: []
    });
    await flush(100);
    expect(reports()).toHaveLength(1);
    expect(reports()[0]).toMatchObject({ claim: false, songId: 1, isPlaying: true });
  });

  it('says the music stopped when it holds the session as playing with nothing of it loaded', async () => {
    const { playerStore } = await boot();
    // Back within the server's reconnect grace (a reload that restored nothing): the server still
    // has this device playing, and would carry on counting until it stopped hearing from it.
    api.handlers!.onSnapshot({
      session: remoteSession({ activeDeviceId: ME, isPlaying: true, positionMs: 80_000 }),
      devices: []
    });
    await flush(100);
    expect(reports()).toHaveLength(1);
    expect(reports()[0]).toMatchObject({
      deviceId: ME,
      claim: false,
      inResponseTo: null,
      songId: 2,
      queue: null,
      queueIndex: 1,
      isPlaying: false,
      radioSeedId: 7
    });
    expect(reports()[0].positionMs).toBeGreaterThanOrEqual(80_000);
    expect(reports()[0].positionMs).toBeLessThan(81_000);
    // Nothing started here: the session is shown paused, ready for Play to pick it up.
    expect(audio).toBeUndefined();
    expect(playerStore.currentSong?.id).toBe(2);
    expect(playerStore.isPlaying).toBe(false);

    // Once it is paused there is nothing more to say.
    api.handlers!.onSnapshot({
      session: remoteSession({ activeDeviceId: ME, isPlaying: false, positionMs: 80_000 }),
      devices: []
    });
    await flush(30_000);
    expect(reports()).toHaveLength(1);
  });
});

describe('the demo account', () => {
  it('never starts: no stream, no reports, the player as it always was', async () => {
    const { playerStore, playbackSync } = await boot({ ...USER, role: 'Demo' });
    expect(playbackSync.enabled).toBe(false);
    expect(api.opened).toBe(0);
    await playerStore.startQueue(QUEUE, 0);
    await flush(30_000);
    expect(reports()).toHaveLength(0);
    expect(playbackSync.entries).toEqual([]);
  });
});
