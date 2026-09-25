import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { PlayerSong } from './player.svelte';

/**
 * Background playback in the installed iOS app hangs on three things this store does, none of
 * which a desktop browser would ever notice going wrong:
 *
 *  • it declares the `playback` audio session on a play intent (and not before one), so WebKit
 *    cannot let the category lapse between two tracks;
 *  • while the page is hidden, the hand-off from `ended` to the next track is synchronous — no
 *    pre-flight round trip to the server sits between the old song ending and the new one playing;
 *  • a stream or a station that fails is retried, and a track that will not play is skipped,
 *    within the seconds a silent hidden page has before iOS suspends it — and a retry the page
 *    slept through gives up rather than starting music whenever the app is next opened.
 */

vi.mock('$app/environment', () => ({ browser: true }));
vi.mock('svelte-sonner', () => ({ toast: Object.assign(vi.fn(), { error: vi.fn() }) }));
vi.mock('$lib/api-client', () => ({
  coverThumbUrl: () => null,
  fetchRadio: vi.fn(async () => []),
  reportSongPlayed: vi.fn(async () => {}),
  toPlayerSong: vi.fn()
}));
vi.mock('$lib/stores/songs.svelte', () => ({
  songsStore: { notePlayed: vi.fn(), songsById: new Map() }
}));
vi.mock('$lib/track-list-view.svelte', () => ({ artistOf: vi.fn() }));

class FakeAudio extends EventTarget {
  static instances: FakeAudio[] = [];
  src = '';
  preload = '';
  volume = 1;
  defaultPlaybackRate = 1;
  playbackRate = 1;
  preservesPitch = true;
  currentTime = 0;
  duration = NaN;
  paused = true;
  readyState = 0;
  play = vi.fn(async () => {
    this.paused = false;
  });
  pause = vi.fn(() => {
    this.paused = true;
  });
  load = vi.fn();
  removeAttribute = vi.fn();
  error: { code: number } | null = null;
  /** Every type playable unless a test says otherwise, like a desktop Chrome. */
  canPlayType = vi.fn((_type: string) => 'maybe');

  /** The element failing its load the way a browser reports it: `error` set, then the event. */
  fail(code: number) {
    this.error = { code };
    this.dispatchEvent(new Event('error'));
  }

  /** Audio flowing from `position`, as the element reports it. */
  playFrom(position: number) {
    this.readyState = 4;
    this.currentTime = position;
    this.dispatchEvent(new Event('playing'));
    this.dispatchEvent(new Event('timeupdate'));
  }

  constructor() {
    super();
    FakeAudio.instances.push(this);
  }
}

const song = (id: number, format?: string): PlayerSong => ({
  id,
  title: `Track ${id}`,
  artist: 'Artist',
  streamUrl: `/api/mh/songs/${id}/stream`,
  format
});

/** Lets every pending promise run, so a negative assertion is not just early. */
const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

const MEDIA_ERR_NETWORK = 2;
const MEDIA_ERR_SRC_NOT_SUPPORTED = 4;
const OGG_OPUS = 'audio/ogg; codecs="opus"';

let doc: { hidden: boolean };
let audioSession: { type: string };
let fetchMock: ReturnType<typeof vi.fn>;

async function loadPlayer() {
  const { playerStore, initPlayer } = await import('./player.svelte');
  initPlayer();
  return { playerStore, initPlayer, audio: FakeAudio.instances.at(-1)! };
}

beforeEach(() => {
  vi.resetModules();
  FakeAudio.instances = [];
  doc = { hidden: false };
  audioSession = { type: 'auto' };
  fetchMock = vi.fn(async () => ({ ok: true }));
  vi.stubGlobal('Audio', FakeAudio);
  vi.stubGlobal('document', doc);
  vi.stubGlobal('navigator', { audioSession });
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => vi.unstubAllGlobals());

describe('audio session', () => {
  it('is left alone at boot and declared as playback on the first play intent', async () => {
    const { playerStore } = await loadPlayer();
    expect(audioSession.type).toBe('auto');

    await playerStore.playSong(song(1));
    expect(audioSession.type).toBe('playback');
  });

  it('is skipped where the browser has no Audio Session API', async () => {
    vi.stubGlobal('navigator', {});
    const { playerStore, audio } = await loadPlayer();

    await playerStore.playSong(song(1));
    expect(audio.play).toHaveBeenCalled();
  });
});

describe('track hand-off', () => {
  it('checks a visible pick with the server before swapping the source', async () => {
    const { playerStore, audio } = await loadPlayer();

    await playerStore.playSong(song(1), [song(1), song(2)]);
    expect(fetchMock).toHaveBeenCalledWith('/api/mh/songs/1/stream', {
      headers: { Range: 'bytes=0-0' }
    });
    expect(audio.src).toBe('/api/mh/songs/1/stream');
  });

  it('keeps the old song when the visible pre-flight finds no file', async () => {
    const { playerStore, audio } = await loadPlayer();
    await playerStore.playSong(song(1), [song(1), song(2)]);

    fetchMock.mockResolvedValueOnce({ ok: false });
    playerStore.playNext();
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
    expect(audio.src).toBe('/api/mh/songs/1/stream');
    expect(playerStore.currentSong?.id).toBe(1);
  });

  it('starts the next track inside the `ended` event while the app is in the background', async () => {
    const { playerStore, audio } = await loadPlayer();
    await playerStore.playSong(song(1), [song(1), song(2)]);
    fetchMock.mockClear();
    audio.play.mockClear();

    doc.hidden = true;
    audio.dispatchEvent(new Event('ended'));

    // Synchronously — no await between the old song ending and the new one starting.
    expect(audio.src).toBe('/api/mh/songs/2/stream');
    expect(audio.play).toHaveBeenCalledTimes(1);
    expect(playerStore.currentSong?.id).toBe(2);
    expect(fetchMock).not.toHaveBeenCalled();
  });
});

describe('formats the browser cannot play', () => {
  it('streams a file the browser can play as it is', async () => {
    const { playerStore, audio } = await loadPlayer();

    await playerStore.playSong(song(1, 'opus'));

    expect(audio.canPlayType).toHaveBeenCalledWith(OGG_OPUS);
    expect(audio.src).toBe('/api/mh/songs/1/stream');
  });

  it('streams the decoded version when the browser says it cannot play the file, checking the original first', async () => {
    const { playerStore, audio } = await loadPlayer();
    audio.canPlayType.mockImplementation((type) => (type === OGG_OPUS ? '' : 'maybe'));

    await playerStore.playSong(song(1, 'opus'));

    expect(fetchMock).toHaveBeenCalledWith('/api/mh/songs/1/stream', {
      headers: { Range: 'bytes=0-0' }
    });
    expect(audio.src).toBe('/api/mh/songs/1/stream?format=wav');
    expect(audio.play).toHaveBeenCalled();
  });

  it('falls back to the decoded stream when the original fails, and sends the rest of the queue straight there', async () => {
    const { toast } = await import('svelte-sonner');
    const { playerStore, audio } = await loadPlayer();
    await playerStore.playSong(song(1, 'opus'), [song(1, 'opus'), song(2, 'opus')]);
    audio.play.mockClear();
    fetchMock.mockClear();

    // Safari claims Ogg Opus and still refuses the file.
    audio.fail(MEDIA_ERR_SRC_NOT_SUPPORTED);

    expect(audio.src).toBe('/api/mh/songs/1/stream?format=wav');
    expect(audio.play).toHaveBeenCalledTimes(1);
    expect(toast.error).not.toHaveBeenCalled();

    // The original answered and the decoded stream plays: it was the format, so the next track in
    // it goes straight to its decoded stream.
    audio.dispatchEvent(new Event('loadedmetadata'));
    await settle();
    playerStore.playNext();
    await vi.waitFor(() => expect(audio.src).toBe('/api/mh/songs/2/stream?format=wav'));
  });

  it('does not blame the format when the original was not reachable', async () => {
    // The same error code covers a 404 or a proxy timeout; one missing mp3 must not have this
    // browser convert every mp3 after it.
    const { playerStore, audio } = await loadPlayer();
    await playerStore.playSong(song(1, 'mp3'), [song(1, 'mp3'), song(2, 'mp3')]);

    fetchMock.mockResolvedValueOnce({ ok: false });
    audio.fail(MEDIA_ERR_SRC_NOT_SUPPORTED);
    audio.dispatchEvent(new Event('loadedmetadata'));
    await settle();

    playerStore.playNext();
    await vi.waitFor(() => expect(audio.src).toBe('/api/mh/songs/2/stream'));
  });

  it('does not blame the format when the decoded stream fails too', async () => {
    const { playerStore, audio } = await loadPlayer();
    await playerStore.playSong(song(1, 'mp3'), [song(1, 'mp3'), song(2, 'mp3')]);

    audio.fail(MEDIA_ERR_SRC_NOT_SUPPORTED);
    audio.fail(MEDIA_ERR_SRC_NOT_SUPPORTED);
    await settle();

    playerStore.playNext();
    await vi.waitFor(() => expect(audio.src).toBe('/api/mh/songs/2/stream'));
  });

  it('keeps the position and the paused state when it falls back', async () => {
    const { playerStore, audio } = await loadPlayer();
    await playerStore.playSong(song(1, 'flac'));
    playerStore.pause();
    playerStore.seek(42);
    audio.play.mockClear();

    // A decode failure part-way in: this file, not the format.
    audio.dispatchEvent(new Event('loadedmetadata'));
    audio.fail(3);

    expect(audio.src).toBe('/api/mh/songs/1/stream?format=wav');
    expect(audio.currentTime).toBe(42);
    expect(audio.play).not.toHaveBeenCalled();
  });

  it('does not send a network failure to the decoded stream', async () => {
    const { playerStore, audio } = await loadPlayer();
    await playerStore.playSong(song(1, 'opus'));

    audio.fail(MEDIA_ERR_NETWORK);
    expect(audio.src).toBe('/api/mh/songs/1/stream');
  });

  it('reports a failure of the decoded stream itself', async () => {
    const { toast } = await import('svelte-sonner');
    const { playerStore, audio } = await loadPlayer();
    await playerStore.playSong(song(2, 'opus'));
    playerStore.pause();

    audio.fail(MEDIA_ERR_SRC_NOT_SUPPORTED);
    expect(audio.src).toBe('/api/mh/songs/2/stream?format=wav');
    audio.fail(MEDIA_ERR_SRC_NOT_SUPPORTED);
    expect(toast.error).toHaveBeenCalledTimes(1);
  });
});

describe('recovering a stream', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  /** A queue playing song 1 in the background, a minute in. */
  async function playingInBackground(queue = [song(1), song(2), song(3)]) {
    const { toast } = await import('svelte-sonner');
    const loaded = await loadPlayer();
    await loaded.playerStore.playSong(queue[0], queue);
    loaded.audio.playFrom(60);
    loaded.audio.play.mockClear();
    loaded.audio.load.mockClear();
    doc.hidden = true;
    return { ...loaded, toast };
  }

  it('reloads a failed stream from the second it reached, after a moment', async () => {
    const { audio, toast } = await playingInBackground();

    audio.fail(MEDIA_ERR_NETWORK);
    expect(toast.error).not.toHaveBeenCalled();

    vi.advanceTimersByTime(999);
    expect(audio.load).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);
    expect(audio.src).toBe('/api/mh/songs/1/stream');
    expect(audio.load).toHaveBeenCalledTimes(1);
    expect(audio.currentTime).toBe(60);
    expect(audio.play).toHaveBeenCalledTimes(1);
  });

  it('skips a track whose stream keeps failing, straight away', async () => {
    const { playerStore, audio, toast } = await playingInBackground();

    audio.fail(MEDIA_ERR_NETWORK);
    vi.advanceTimersByTime(1000);
    audio.fail(MEDIA_ERR_NETWORK);
    vi.advanceTimersByTime(3000);
    audio.fail(MEDIA_ERR_NETWORK);

    // Synchronously, like any other hand-off in the background.
    expect(playerStore.currentSong?.id).toBe(2);
    expect(audio.src).toBe('/api/mh/songs/2/stream');
    expect(toast.error).toHaveBeenCalledWith('Skipped a track', expect.anything());
  });

  it('earns its reloads back once the stream plays on past where it failed', async () => {
    const { playerStore, audio } = await playingInBackground();

    for (const position of [60, 90, 120]) {
      audio.fail(MEDIA_ERR_NETWORK);
      vi.advanceTimersByTime(1000);
      audio.playFrom(position + 15);
    }
    expect(playerStore.currentSong?.id).toBe(1);
  });

  it('stops, paused, after three tracks in a row would not play', async () => {
    const queue = [song(1), song(2), song(3), song(4), song(5)];
    const { playerStore, audio, toast } = await playingInBackground(queue);

    const failForGood = () => {
      audio.fail(MEDIA_ERR_NETWORK);
      vi.advanceTimersByTime(1000);
      audio.fail(MEDIA_ERR_NETWORK);
      vi.advanceTimersByTime(3000);
      audio.fail(MEDIA_ERR_NETWORK);
    };
    failForGood();
    failForGood();
    failForGood();
    expect(playerStore.currentSong?.id).toBe(4);

    failForGood();
    expect(playerStore.currentSong?.id).toBe(4);
    expect(playerStore.isPlaying).toBe(false);
    expect(toast.error).toHaveBeenLastCalledWith('Playback failed', expect.anything());
    vi.advanceTimersByTime(60_000);
    expect(playerStore.currentSong?.id).toBe(4);
  });

  it('gives up rather than playing when the page slept through the retry, and Play picks it up', async () => {
    const { playerStore, audio, toast } = await playingInBackground();

    audio.fail(MEDIA_ERR_NETWORK);
    // iOS suspended the page: the clock moved on without the timer firing.
    vi.setSystemTime(Date.now() + 10 * 60_000);
    vi.advanceTimersByTime(1000);

    expect(audio.load).not.toHaveBeenCalled();
    expect(audio.play).not.toHaveBeenCalled();
    expect(playerStore.isPlaying).toBe(false);
    expect(toast.error).toHaveBeenCalledWith('Playback failed', expect.anything());

    playerStore.resume();
    expect(audio.load).toHaveBeenCalledTimes(1);
    expect(audio.currentTime).toBe(60);
    expect(audio.play).toHaveBeenCalledTimes(1);
  });

  it('lets a Play pressed during a pending retry replace it, rather than reload twice', async () => {
    const { playerStore, audio } = await playingInBackground();

    audio.fail(MEDIA_ERR_NETWORK);
    playerStore.resume();
    expect(audio.load).toHaveBeenCalledTimes(1);

    vi.advanceTimersByTime(60_000);
    expect(audio.load).toHaveBeenCalledTimes(1);
  });

  it('does not retry a stream the listener paused', async () => {
    const { playerStore, audio } = await playingInBackground();

    audio.fail(MEDIA_ERR_NETWORK);
    playerStore.pause();
    vi.advanceTimersByTime(60_000);
    expect(audio.load).not.toHaveBeenCalled();
    expect(audio.play).not.toHaveBeenCalled();
  });

  it('reloads a stream that stalls mid-track, and leaves one that recovers by itself alone', async () => {
    const { audio } = await playingInBackground();

    audio.dispatchEvent(new Event('waiting'));
    vi.advanceTimersByTime(5000);
    audio.playFrom(70);
    vi.advanceTimersByTime(60_000);
    expect(audio.load).not.toHaveBeenCalled();

    audio.dispatchEvent(new Event('waiting'));
    vi.advanceTimersByTime(10_000 + 1000);
    expect(audio.load).toHaveBeenCalledTimes(1);
    expect(audio.currentTime).toBe(70);
    expect(audio.play).toHaveBeenCalledTimes(1);
  });

  it('does not take a new track still loading for a stall', async () => {
    const { audio } = await playingInBackground();
    audio.dispatchEvent(new Event('ended'));
    audio.load.mockClear();

    audio.dispatchEvent(new Event('waiting'));
    vi.advanceTimersByTime(60_000);
    expect(audio.load).not.toHaveBeenCalled();
  });
});

describe('the station', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  async function stationFixture() {
    const api = await import('$lib/api-client');
    const { songsStore } = await import('$lib/stores/songs.svelte');
    vi.mocked(api.toPlayerSong).mockImplementation((row) => song((row as { id: number }).id));
    (songsStore.songsById as Map<number, unknown>).set(7, { id: 7 });
    return vi.mocked(api.fetchRadio);
  }

  it('survives a request that could not be made, and asks again when the queue runs out', async () => {
    const fetchRadio = await stationFixture();
    fetchRadio.mockRejectedValueOnce(new TypeError('Load failed'));
    const { playerStore, audio } = await loadPlayer();
    await playerStore.playSong(song(1), [song(1)]);
    await vi.advanceTimersByTimeAsync(0);
    expect(fetchRadio).toHaveBeenCalledTimes(1);
    expect(playerStore.hasNext).toBe(true);

    // The end of the queue: the station is still unreachable, then answers.
    fetchRadio.mockRejectedValueOnce(new TypeError('Load failed'));
    fetchRadio.mockResolvedValueOnce([7]);
    doc.hidden = true;
    audio.dispatchEvent(new Event('ended'));
    await vi.advanceTimersByTimeAsync(0);
    expect(fetchRadio).toHaveBeenCalledTimes(2);
    expect(playerStore.currentSong?.id).toBe(1);

    await vi.advanceTimersByTimeAsync(1000);
    expect(playerStore.currentSong?.id).toBe(7);
    expect(audio.src).toBe('/api/mh/songs/7/stream');
  });

  it('ends on a refusal', async () => {
    const fetchRadio = await stationFixture();
    fetchRadio.mockRejectedValueOnce(Object.assign(new Error('Unauthorized'), { status: 401 }));
    const { playerStore } = await loadPlayer();
    await playerStore.playSong(song(1), [song(1)]);
    await vi.advanceTimersByTimeAsync(0);

    expect(playerStore.hasNext).toBe(false);
  });
});
