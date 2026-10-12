import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { PlayerSong } from './player.svelte';

/**
 * The player store inside the native app shell, where the device's own player stands in for the
 * audio element (`$lib/native/native-audio`). The device moves to the next track by itself when a
 * song ends, with the page asleep or not, so what these pin is the store catching up with it:
 * one track or several, counted and queued as if the page had been awake, and never loaded twice.
 */

vi.mock('$app/environment', () => ({ browser: true }));
vi.mock('svelte-sonner', () => ({ toast: Object.assign(vi.fn(), { error: vi.fn() }) }));
vi.mock('$lib/api-client', () => ({
  coverThumbUrl: (url: string | null | undefined) => url ?? null,
  fetchRadio: vi.fn(async () => []),
  reportSongPlayed: vi.fn(async () => {}),
  toPlayerSong: vi.fn()
}));
vi.mock('$lib/stores/songs.svelte', () => ({
  songsStore: { notePlayed: vi.fn(), songsById: new Map() }
}));
vi.mock('$lib/track-list-view.svelte', () => ({ artistOf: vi.fn() }));

interface Call {
  method: string;
  options: Record<string, unknown>;
}

/** The Swift plugin, as far as the page can tell. */
const device = {
  calls: [] as Call[],
  listeners: new Map<string, (data: unknown) => void>(),
  of(method: string): Call[] {
    return this.calls.filter((call) => call.method === method);
  },
  last(method: string): Record<string, unknown> {
    return this.of(method).at(-1)!.options;
  },
  emit(event: string, data: unknown) {
    this.listeners.get(event)!(data);
  },
  /** A report on the load the page last asked for, as up to date as its last command. */
  state(report: {
    trail: string[];
    status?: 'playing' | 'waiting' | 'paused';
    position?: number;
    duration?: number;
    ended?: boolean;
  }) {
    const seq = Math.max(0, ...this.calls.map((call) => Number(call.options.seq ?? 0)));
    this.emit('state', {
      loadId: this.last('loadTrack').loadId,
      seq,
      index: report.trail.length - 1,
      status: 'playing',
      position: 0,
      duration: 200,
      ended: false,
      ...report
    });
  }
};

let platform: 'ios' | 'android' = 'ios';

vi.mock('$lib/native/shell', () => ({
  isNativeShell: () => true,
  nativePlatform: () => platform,
  nativePlugin: () => ({
    call: async (method: string, options: Record<string, unknown> = {}) => {
      device.calls.push({ method, options });
      return {};
    },
    listen: (event: string, handler: (data: unknown) => void) => {
      device.listeners.set(event, handler);
    }
  })
}));

const ORIGIN = 'https://music.example';
const url = (id: number) => `${ORIGIN}/api/mh/songs/${id}/stream`;

const song = (id: number, format?: string): PlayerSong => ({
  id,
  title: `Track ${id}`,
  artist: 'Artist',
  album: 'Album',
  streamUrl: `/api/mh/songs/${id}/stream`,
  coverUrl: `/api/mh/songs/${id}/cover`,
  format
});

/** Lets every pending promise and microtask run. */
const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

let fetchMock: ReturnType<typeof vi.fn>;

async function loadPlayer() {
  const { playerStore, initPlayer } = await import('./player.svelte');
  const { reportSongPlayed } = await import('$lib/api-client');
  initPlayer();
  return { playerStore, reportSongPlayed: vi.mocked(reportSongPlayed) };
}

beforeEach(() => {
  vi.resetModules();
  platform = 'ios';
  device.calls = [];
  device.listeners = new Map();
  fetchMock = vi.fn(async () => ({ ok: true }));
  vi.stubGlobal('document', {
    hidden: false,
    visibilityState: 'visible',
    addEventListener: vi.fn()
  });
  vi.stubGlobal('location', { href: `${ORIGIN}/overview` });
  vi.stubGlobal('navigator', {});
  vi.stubGlobal('fetch', fetchMock);
  vi.stubGlobal('requestAnimationFrame', () => 1);
  vi.stubGlobal('cancelAnimationFrame', () => {});
});

afterEach(() => vi.unstubAllGlobals());

describe('loading a track on the device', () => {
  it('sends the source, its words and the play as one message, then what follows it', async () => {
    const { playerStore } = await loadPlayer();
    await playerStore.playSong(song(1), [song(1), song(2), song(3)]);
    await settle();

    expect(device.of('loadTrack')).toHaveLength(1);
    expect(device.last('loadTrack')).toMatchObject({
      url: url(1),
      title: 'Track 1',
      artist: 'Artist',
      album: 'Album',
      artwork: `${ORIGIN}/api/mh/songs/1/cover`,
      position: 0,
      autoplay: true
    });
    const upcoming = device.last('setUpcoming');
    expect(upcoming.loadId).toBe(device.last('loadTrack').loadId);
    expect((upcoming.items as { url: string }[]).map((item) => item.url)).toEqual([url(2), url(3)]);
    expect(device.last('setCommands')).toEqual({ next: true, previous: true });
  });

  it('never checks a pick with the server first: the hand-off is synchronous', async () => {
    const { playerStore } = await loadPlayer();
    await playerStore.playSong(song(1), [song(1), song(2)]);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('asks for the decoded stream of a format the device cannot open', async () => {
    const { playerStore } = await loadPlayer();
    await playerStore.playSong(song(1, 'opus'));
    await settle();
    expect(device.last('loadTrack').url).toBe(`${url(1)}?format=wav`);
  });

  it('streams that format as it is on Android, whose player opens it', async () => {
    platform = 'android';
    const { playerStore } = await loadPlayer();
    await playerStore.playSong(song(1, 'opus'));
    await settle();
    expect(device.last('loadTrack').url).toBe(url(1));
  });

  it('shows what the device reports', async () => {
    const { playerStore } = await loadPlayer();
    await playerStore.playSong(song(1), [song(1), song(2)]);
    await settle();

    device.state({ trail: [url(1)], position: 42, duration: 180 });
    expect(playerStore.duration).toBe(180);
    expect(playerStore.isPlaying).toBe(true);

    device.state({ trail: [url(1)], status: 'paused', position: 43, duration: 180 });
    expect(playerStore.isPlaying).toBe(false);
    expect(playerStore.currentTime).toBe(43);
  });

  it('ignores news of an earlier load', async () => {
    const { playerStore } = await loadPlayer();
    await playerStore.playSong(song(1), [song(1), song(2)]);
    await settle();
    const stale = device.last('loadTrack').loadId;
    playerStore.playNext();
    await settle();

    device.emit('state', {
      loadId: stale,
      seq: 99,
      index: 0,
      trail: [url(1)],
      status: 'paused',
      position: 10,
      duration: 50,
      ended: false
    });
    expect(playerStore.currentSong?.id).toBe(2);
    expect(playerStore.duration).toBe(0);
  });
});

describe('the device moving on by itself', () => {
  it('is followed onto the next track without loading it again', async () => {
    const { playerStore, reportSongPlayed } = await loadPlayer();
    await playerStore.playSong(song(1), [song(1), song(2), song(3)]);
    await settle();
    device.state({ trail: [url(1)], position: 190 });
    reportSongPlayed.mockClear();

    device.state({ trail: [url(1), url(2)], position: 0.3, duration: 150 });
    await settle();

    expect(playerStore.currentSong?.id).toBe(2);
    expect(playerStore.isPlaying).toBe(true);
    expect(playerStore.duration).toBe(150);
    expect(reportSongPlayed).toHaveBeenCalledExactlyOnceWith(2);
    expect(device.of('loadTrack')).toHaveLength(1);
    expect(device.of('pause')).toHaveLength(0);
    // What follows is told again, from the track the device is on now.
    expect((device.last('setUpcoming').items as { url: string }[]).map((i) => i.url)).toEqual([
      url(3)
    ]);
  });

  it('is followed through every track that played while the page slept', async () => {
    const { playerStore, reportSongPlayed } = await loadPlayer();
    await playerStore.playSong(song(1), [song(1), song(2), song(3), song(4)]);
    await settle();
    reportSongPlayed.mockClear();

    device.state({ trail: [url(1), url(2), url(3)], position: 61, duration: 150 });
    await settle();

    expect(playerStore.currentSong?.id).toBe(3);
    expect(playerStore.isPlaying).toBe(true);
    expect(reportSongPlayed.mock.calls.map(([id]) => id)).toEqual([2, 3]);
    expect(device.of('loadTrack')).toHaveLength(1);
  });

  it('arrives paused when the device was paused on the way', async () => {
    const { playerStore } = await loadPlayer();
    await playerStore.playSong(song(1), [song(1), song(2)]);
    await settle();

    device.state({ trail: [url(1), url(2)], status: 'paused', position: 12 });
    await settle();

    expect(playerStore.currentSong?.id).toBe(2);
    expect(playerStore.isPlaying).toBe(false);
    expect(device.of('play')).toHaveLength(0);
  });

  it('is stopped when the store has no such next track', async () => {
    const { playerStore } = await loadPlayer();
    await playerStore.playSong(song(1), [song(1)]);
    await settle();

    // A track the queue no longer holds: the store's song is over, so nothing may play on.
    device.state({ trail: [url(1), url(9)], position: 3 });
    await settle();

    expect(playerStore.currentSong?.id).toBe(1);
    expect(device.of('pause')).toHaveLength(1);
  });

  it('gets the next track from the store when it ran out of its own', async () => {
    const { playerStore } = await loadPlayer();
    await playerStore.playSong(song(1), [song(1), song(2)]);
    await settle();
    device.state({ trail: [url(1)], position: 199 });

    device.emit('ended', { loadId: device.last('loadTrack').loadId, index: 0 });
    await settle();

    expect(playerStore.currentSong?.id).toBe(2);
    expect(device.of('loadTrack')).toHaveLength(2);
    expect(device.last('loadTrack')).toMatchObject({ url: url(2), autoplay: true });
  });
});

describe('the lock screen', () => {
  it('has its commands answered, then carried out by the store', async () => {
    const { playerStore } = await loadPlayer();
    await playerStore.playSong(song(1), [song(1), song(2)]);
    await settle();
    device.state({ trail: [url(1)], position: 5 });

    device.emit('remote', { id: 7, command: 'pause' });
    await settle();
    expect(device.last('ackRemote')).toEqual({ id: 7 });
    expect(device.of('pause')).toHaveLength(1);
    expect(playerStore.isPlaying).toBe(false);

    device.emit('remote', { id: 8, command: 'next' });
    await settle();
    expect(playerStore.currentSong?.id).toBe(2);
    expect(device.last('loadTrack')).toMatchObject({ url: url(2), autoplay: true });
  });

  it('scrubs through the store, which seeks the device once', async () => {
    const { playerStore } = await loadPlayer();
    await playerStore.playSong(song(1));
    await settle();
    device.state({ trail: [url(1)], position: 5 });

    device.emit('remote', { id: 3, command: 'seek', time: 90 });
    await settle();
    expect(device.of('seek')).toHaveLength(1);
    expect(device.last('seek')).toMatchObject({ time: 90 });
    expect(playerStore.currentTime).toBeGreaterThanOrEqual(90);
  });

  it('takes a rewind the device made by itself as a seek, not as the song ending', async () => {
    const { playerStore } = await loadPlayer();
    await playerStore.playSong(song(1), [song(1), song(2)]);
    await settle();
    device.state({ trail: [url(1)], position: 120 });

    device.state({ trail: [url(1)], position: 0.1 });
    await settle();

    expect(playerStore.currentSong?.id).toBe(1);
    expect(device.of('seek')).toHaveLength(0);
    expect(device.of('loadTrack')).toHaveLength(1);
  });
});

describe('a track the device cannot play', () => {
  it('falls back to the decoded stream when the file is the problem', async () => {
    const { playerStore } = await loadPlayer();
    await playerStore.playSong(song(1, 'flac'));
    await settle();

    device.emit('error', { loadId: device.last('loadTrack').loadId, index: 0, kind: 'format' });
    await settle();

    expect(playerStore.currentSong?.id).toBe(1);
    expect(device.last('loadTrack')).toMatchObject({ url: `${url(1)}?format=wav`, autoplay: true });
  });
});
