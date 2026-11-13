import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { PlayerSong } from './player.svelte';

/**
 * Background playback in the installed iOS app hangs on three things this store does, none of
 * which a desktop browser would ever notice going wrong:
 *
 *  • it declares the `playback` audio session on a play intent (and not before one), so WebKit
 *    cannot let the category lapse between two tracks;
 *  • a song never ends as far as WebKit is concerned: the element loops, and the jump back to its
 *    start is the hand-off — a real `ended` makes WebKit deactivate the audio session, which an app
 *    in the background cannot get back, so the next song would wait for the app to be opened;
 *  • while the page is hidden, that hand-off is synchronous — no pre-flight round trip to the
 *    server sits between the old song ending and the new one playing;
 *  • a stream or a station that fails is retried, and a track that will not play is skipped,
 *    within the seconds a silent hidden page has before iOS suspends it — and a retry the page
 *    slept through gives up rather than starting music whenever the app is next opened.
 */

vi.mock('$app/env', () => ({ browser: true }));
vi.mock('svelte-sonner', () => ({ toast: Object.assign(vi.fn(), { error: vi.fn() }) }));
vi.mock('#lib/api-client.ts', () => ({
  coverThumbUrl: () => null,
  fetchRadio: vi.fn(async () => []),
  reportSongPlayed: vi.fn(async () => {}),
  toPlayerSong: vi.fn()
}));
vi.mock('#lib/stores/songs.svelte.ts', () => ({
  songsStore: { notePlayed: vi.fn(), songsById: new Map() }
}));
vi.mock('#lib/track-list-view.svelte.ts', () => ({ artistOf: vi.fn() }));

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
  loop = false;
  seeking = false;
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

  /** The song reaching its end with `loop` set: the element seeks itself back to the start. */
  loopToStart() {
    this.currentTime = 0;
    this.dispatchEvent(new Event('seeking'));
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

/**
 * What the browser's frames are to the store: a callback asked for runs once, when a frame is
 * drawn, and not at all while the page is hidden.
 */
function fakeFrames() {
  const waiting = new Map<number, FrameRequestCallback>();
  let asked = 0;
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => {
    waiting.set(++asked, callback);
    return asked;
  });
  vi.stubGlobal('cancelAnimationFrame', (handle: number) => waiting.delete(handle));
  return {
    /** Frames asked for so far. */
    get asked() {
      return asked;
    },
    /** Frames asked for and not drawn yet. */
    get waiting() {
      return waiting.size;
    },
    draw(now: number) {
      const due = [...waiting.values()];
      waiting.clear();
      for (const callback of due) callback(now);
    }
  };
}

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

  it('starts the next track the moment the song comes round, while the app is in the background', async () => {
    const { playerStore, audio } = await loadPlayer();
    await playerStore.playSong(song(1), [song(1), song(2)]);
    audio.playFrom(200);
    fetchMock.mockClear();
    audio.play.mockClear();

    doc.hidden = true;
    audio.loopToStart();

    // Synchronously — no await between the old song ending and the new one starting.
    expect(audio.src).toBe('/api/mh/songs/2/stream');
    expect(audio.play).toHaveBeenCalledTimes(1);
    expect(playerStore.currentSong?.id).toBe(2);
    expect(fetchMock).not.toHaveBeenCalled();
  });
});

describe('the end of a song', () => {
  it('is never an `ended` to the browser: the element loops', async () => {
    // A song that plays to its end has WebKit deactivate the audio session, and an app in the
    // background may not activate it again — the next song would wait for the app to be opened.
    const { audio } = await loadPlayer();
    expect(audio.loop).toBe(true);
  });

  it('holds the restarted song silent while a visible pick is checked with the server', async () => {
    const { playerStore, audio } = await loadPlayer();
    await playerStore.playSong(song(1), [song(1), song(2)]);
    audio.playFrom(200);

    audio.loopToStart();
    expect(audio.pause).toHaveBeenCalledTimes(1);
    expect(audio.src).toBe('/api/mh/songs/1/stream');

    await vi.waitFor(() => expect(audio.src).toBe('/api/mh/songs/2/stream'));
    expect(playerStore.currentSong?.id).toBe(2);
  });

  it('moves on once, whichever event reports it first', async () => {
    const { playerStore, audio } = await loadPlayer();
    await playerStore.playSong(song(1), [song(1), song(2), song(3)]);
    audio.playFrom(200);
    doc.hidden = true;

    audio.loopToStart();
    audio.dispatchEvent(new Event('timeupdate'));
    expect(playerStore.currentSong?.id).toBe(2);
  });

  it('is caught by the position alone where the loop comes round without a seek', async () => {
    const { playerStore, audio } = await loadPlayer();
    await playerStore.playSong(song(1), [song(1), song(2)]);
    audio.playFrom(200);
    doc.hidden = true;

    audio.currentTime = 0.1;
    audio.dispatchEvent(new Event('timeupdate'));
    expect(playerStore.currentSong?.id).toBe(2);
  });

  it('is not a seek back to the start, like Previous restarting the song', async () => {
    const { playerStore, audio } = await loadPlayer();
    await playerStore.playSong(song(1), [song(1), song(2)]);
    audio.playFrom(120);
    doc.hidden = true;

    playerStore.seek(0);
    audio.dispatchEvent(new Event('seeking'));
    audio.playFrom(0.2);

    expect(playerStore.currentSong?.id).toBe(1);
    expect(audio.pause).not.toHaveBeenCalled();
  });

  it('is not a decoded stream taking over near the start', async () => {
    const { playerStore, audio } = await loadPlayer();
    await playerStore.playSong(song(1, 'flac'), [song(1, 'flac'), song(2, 'flac')]);
    audio.playFrom(0.8);
    doc.hidden = true;

    audio.fail(3);
    audio.playFrom(0.2);

    expect(audio.src).toBe('/api/mh/songs/1/stream?format=wav');
    expect(playerStore.currentSong?.id).toBe(1);
  });

  it('leaves a song with nothing after it loaded, paused at its start', async () => {
    const api = await import('#lib/api-client.ts');
    vi.mocked(api.fetchRadio).mockRejectedValueOnce(
      Object.assign(new Error('Not found'), { status: 404 })
    );
    const { playerStore, audio } = await loadPlayer();
    await playerStore.playSong(song(1), [song(1)]);
    await settle();
    audio.playFrom(200);
    audio.play.mockClear();

    audio.loopToStart();
    await settle();

    expect(audio.pause).toHaveBeenCalledTimes(1);
    expect(audio.play).not.toHaveBeenCalled();
    expect(audio.src).toBe('/api/mh/songs/1/stream');
    expect(playerStore.currentSong?.id).toBe(1);
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
    audio.loopToStart();
    audio.load.mockClear();

    audio.dispatchEvent(new Event('waiting'));
    vi.advanceTimersByTime(60_000);
    expect(audio.load).not.toHaveBeenCalled();
  });
});

describe('the position while a song plays', () => {
  let frames: ReturnType<typeof fakeFrames>;

  beforeEach(() => {
    vi.useFakeTimers();
    frames = fakeFrames();
  });
  afterEach(() => vi.useRealTimers());

  async function playing() {
    const loaded = await loadPlayer();
    await loaded.playerStore.playSong(song(1), [song(1), song(2)]);
    loaded.audio.dispatchEvent(new Event('play'));
    return loaded;
  }

  it('is committed about ten times a second, each from one frame — not a frame asked for on every frame', async () => {
    const { playerStore, audio } = await playing();

    // A second of a 60 Hz screen: a frame every 16.7 ms, drawn only if something asked for it.
    let drawn = 0;
    for (let frame = 0; frame < 60; frame++) {
      audio.currentTime = frame / 60;
      if (frames.waiting > 0) {
        drawn += 1;
        frames.draw(1000 + (frame * 1000) / 60);
        expect(playerStore.currentTime).toBe(audio.currentTime);
        // The commit asks for nothing: the next frame is only wanted once the interval is up.
        expect(frames.waiting).toBe(0);
      }
      vi.advanceTimersByTime(1000 / 60);
    }
    expect(drawn).toBe(10);
    expect(frames.asked).toBeLessThanOrEqual(11);
  });

  it('commits on every frame it takes, however little time the frame says has passed', async () => {
    const { playerStore, audio } = await playing();
    audio.currentTime = 5;
    frames.draw(1000);
    vi.advanceTimersByTime(90);

    // A frame stamped a hair under the interval is still the one this commit waited for.
    audio.currentTime = 5.1;
    frames.draw(1099.9);
    expect(playerStore.currentTime).toBe(5.1);
  });

  it('keeps nobody who needs the exact second waiting: a report, a seek and the lock screen read the element', async () => {
    const setPositionState = vi.fn();
    vi.stubGlobal('MediaMetadata', class {});
    vi.stubGlobal('navigator', {
      audioSession,
      mediaSession: { metadata: null, setActionHandler: () => {}, setPositionState }
    });
    const { playerStore, audio } = await playing();
    const { localPlayback } = await import('./player.svelte');
    audio.duration = 200;
    audio.dispatchEvent(new Event('loadedmetadata'));
    audio.currentTime = 5;
    frames.draw(1000);

    // Forty milliseconds on, between two commits: what is shown waits, what is reported does not.
    audio.currentTime = 5.04;
    expect(playerStore.currentTime).toBe(5);
    expect(localPlayback.state()).toMatchObject({ positionMs: 5040, durationMs: 200_000 });

    playerStore.seek(42);
    expect(playerStore.currentTime).toBe(42);
    expect(setPositionState).toHaveBeenLastCalledWith({
      duration: 200,
      position: 42,
      playbackRate: 1
    });
  });

  it('tells the lock screen where a stream reloaded in the background resumes, not the second last drawn', async () => {
    const setPositionState = vi.fn();
    vi.stubGlobal('MediaMetadata', class {});
    vi.stubGlobal('navigator', {
      audioSession,
      mediaSession: { metadata: null, setActionHandler: () => {}, setPositionState }
    });
    const { playerStore, audio } = await playing();
    audio.duration = 200;
    audio.dispatchEvent(new Event('loadedmetadata'));
    audio.currentTime = 5;
    frames.draw(1000);

    // The app leaves the screen five seconds in. No frame is drawn from here on, so 0:05 is the
    // last position committed, while the song plays on for two minutes and then loses its stream.
    doc.hidden = true;
    audio.playFrom(125);
    audio.fail(MEDIA_ERR_NETWORK);
    vi.advanceTimersByTime(1000);
    expect(audio.load).toHaveBeenCalledTimes(2);
    audio.dispatchEvent(new Event('loadedmetadata'));

    // The lock screen counts on from what it is told here, and it is the only place to see it.
    expect(playerStore.currentTime).toBe(5);
    expect(setPositionState).toHaveBeenLastCalledWith({
      duration: 200,
      position: 125,
      playbackRate: 1
    });
  });

  it('stops with the song, and a pause shows the exact second it stopped at', async () => {
    const { playerStore, audio } = await playing();
    frames.draw(1000);

    audio.currentTime = 7.25;
    audio.dispatchEvent(new Event('pause'));
    expect(playerStore.currentTime).toBe(7.25);
    vi.advanceTimersByTime(60_000);
    expect(frames.waiting).toBe(0);
    expect(vi.getTimerCount()).toBe(0);

    // Paused while it was waiting on a frame: that frame is given back too.
    audio.dispatchEvent(new Event('play'));
    expect(frames.waiting).toBe(1);
    audio.dispatchEvent(new Event('pause'));
    expect(frames.waiting).toBe(0);
  });

  it('parks while the page is hidden, and carries on from the first frame after', async () => {
    const { playerStore, audio } = await playing();
    frames.draw(1000);
    const before = frames.asked;

    // Hidden: timers still fire, frames do not. One frame stays asked for, and nothing else runs.
    vi.advanceTimersByTime(10 * 60_000);
    expect(frames.asked).toBe(before + 1);
    expect(vi.getTimerCount()).toBe(0);

    audio.currentTime = 600;
    frames.draw(601_000);
    expect(playerStore.currentTime).toBe(600);
    vi.advanceTimersByTime(90);
    expect(frames.waiting).toBe(1);
  });
});

describe('surviving a reload', () => {
  // The reload snapshot is two entries in the tab's storage: the queue, and everything else. The
  // queue can be the whole library, and storing it holds the main thread for a tenth of a second
  // on a phone, so it is written when the queue is a different one and at a quiet moment — not
  // with every position write, and not in the middle of a tap.
  const QUEUE_ENTRY = 'mh:playback:queue';
  const SMALL_ENTRY = 'mh:playback';

  let frames: ReturnType<typeof fakeFrames>;
  /** The tab's sessionStorage: it outlives the document, so a "reload" below keeps it. */
  let stored: Map<string, string>;
  /** Every key written, in order. */
  let writes: string[];
  let pageEvents: Map<string, () => void>;

  beforeEach(() => {
    vi.useFakeTimers();
    frames = fakeFrames();
    stored = new Map();
    writes = [];
    pageEvents = new Map();
    const listen = (type: string, listener: () => void) => void pageEvents.set(type, listener);
    Object.assign(doc, { visibilityState: 'visible', addEventListener: listen });
    vi.stubGlobal('window', {
      sessionStorage: {
        getItem: (key: string) => stored.get(key) ?? null,
        setItem: (key: string, value: string) => {
          writes.push(key);
          stored.set(key, value);
        },
        removeItem: (key: string) => void stored.delete(key)
      },
      addEventListener: listen
    });
  });
  afterEach(() => vi.useRealTimers());

  /** A document of the signed-in app: the player with its reload snapshot on. */
  async function boot() {
    const { playerStore, localPlayback, initPlayer } = await import('./player.svelte');
    initPlayer('u1');
    return { playerStore, localPlayback, audio: FakeAudio.instances.at(-1)! };
  }

  /** The same tab loading the app again: a new document over the storage the last one left. */
  async function reload() {
    pageEvents.get('pagehide')!();
    vi.resetModules();
    FakeAudio.instances = [];
    writes = [];
    return boot();
  }

  const library = (size: number) => Array.from({ length: size }, (_, i) => song(i + 1));
  const smallEntry = () => JSON.parse(stored.get(SMALL_ENTRY)!) as Record<string, unknown>;

  it('writes the queue once, and only the small entry for as long as the song plays', async () => {
    const { playerStore, audio } = await boot();
    await playerStore.startQueue(library(10_000), 4);
    audio.dispatchEvent(new Event('play'));

    // Half a minute of playing, a frame every tenth of a second.
    for (let ms = 0; ms <= 30_000; ms += 100) {
      audio.currentTime = ms / 1000;
      frames.draw(ms);
      vi.advanceTimersByTime(100);
    }
    // The queue with the first write, then the position every five seconds, by itself.
    expect(writes).toEqual([QUEUE_ENTRY, ...Array(6).fill(SMALL_ENTRY)]);
    expect(stored.get(QUEUE_ENTRY)!.length).toBeGreaterThan(500_000);
    expect(stored.get(SMALL_ENTRY)!.length).toBeLessThan(400);
    expect(smallEntry()).toMatchObject({ queueIndex: 4, position: 30 });
  });

  it('does not write on the first frame of a track: only once it has played for five seconds', async () => {
    const { playerStore, audio } = await boot();
    await playerStore.startQueue(library(3), 0);
    pageEvents.get('pagehide')!();
    writes = [];

    // Paused a while ago; the frames below carry on from a clock long past the last write.
    audio.dispatchEvent(new Event('play'));
    frames.draw(60_000);
    expect(writes).toEqual([]);

    for (let ms = 60_100; ms <= 65_000; ms += 100) {
      vi.advanceTimersByTime(100);
      frames.draw(ms);
    }
    expect(writes).toEqual([SMALL_ENTRY]);
  });

  it('keeps a new queue out of the tap that made it: it waits for a quiet moment, and the entries keep what they had', async () => {
    const { playerStore } = await boot();
    await playerStore.startQueue([song(1), song(2)], 0);
    pageEvents.get('pagehide')!();
    const before = new Map(stored);
    writes = [];

    await playerStore.startQueue(library(10_000), 7);
    playerStore.seek(12); // a write asked for while the new queue is not stored yet
    expect(writes).toEqual([]);
    vi.advanceTimersByTime(999);
    expect(writes).toEqual([]);
    // A tab that died here comes back to the queue and song it had, not to half of each.
    expect(stored).toEqual(before);

    vi.advanceTimersByTime(1);
    expect(writes).toEqual([QUEUE_ENTRY, SMALL_ENTRY]);
    expect(smallEntry()).toMatchObject({ queueIndex: 7, position: 12 });
  });

  it('takes the browser up on an idle moment where it offers one', async () => {
    let idle: (() => void) | null = null;
    const requestIdle = vi.fn((callback: () => void, _options?: { timeout: number }) => {
      idle = callback;
      return 1;
    });
    vi.stubGlobal('requestIdleCallback', requestIdle);
    vi.stubGlobal('cancelIdleCallback', () => (idle = null));
    const { playerStore } = await boot();

    await playerStore.startQueue(library(3), 0);
    playerStore.seek(12);
    playerStore.seek(13); // asked for once, however many writes want it
    expect(requestIdle).toHaveBeenCalledTimes(1);
    // Idle or not, it is written within two seconds.
    expect(requestIdle.mock.calls[0][1]).toEqual({ timeout: 2000 });
    vi.advanceTimersByTime(60_000);
    expect(writes).toEqual([]);

    idle!();
    expect(writes).toEqual([QUEUE_ENTRY, SMALL_ENTRY]);
  });

  it('writes everything at once when the page goes away or out of sight', async () => {
    const { playerStore } = await boot();
    await playerStore.startQueue(library(3), 1);
    playerStore.seek(12);
    expect(writes).toEqual([]);

    pageEvents.get('pagehide')!();
    expect(writes).toEqual([QUEUE_ENTRY, SMALL_ENTRY]);
    // The write that was waiting has been done: it does not come round again.
    vi.advanceTimersByTime(60_000);
    expect(writes).toEqual([QUEUE_ENTRY, SMALL_ENTRY]);

    await playerStore.startQueue(library(4), 2);
    Object.assign(doc, { visibilityState: 'hidden' });
    pageEvents.get('visibilitychange')!();
    expect(writes).toEqual([QUEUE_ENTRY, SMALL_ENTRY, QUEUE_ENTRY, SMALL_ENTRY]);
  });

  it('writes a queue that changed while the page is hidden without waiting: nobody is kept waiting there', async () => {
    const { playerStore } = await boot();
    await playerStore.startQueue(library(3), 0);
    doc.hidden = true;
    playerStore.seek(30); // the lock screen's scrubber
    expect(writes).toEqual([QUEUE_ENTRY, SMALL_ENTRY]);
  });

  it('writes nothing in the hand-off to the next track while the app is in the background', async () => {
    const { playerStore, audio } = await boot();
    await playerStore.startQueue(library(3), 0);
    pageEvents.get('pagehide')!();
    audio.playFrom(200);
    audio.play.mockClear();
    writes = [];

    doc.hidden = true;
    audio.loopToStart();
    // The next song is on the element and asked to play; the storage was not touched on the way.
    expect(audio.src).toBe('/api/mh/songs/2/stream');
    expect(audio.play).toHaveBeenCalledTimes(1);
    expect(writes).toEqual([]);
  });

  it('brings back exactly what was playing: a library-sized queue in its order, the song, the second, the station', async () => {
    // Shuffled, as a queue is: the order is the queue's own, not the library's.
    const queue = Array.from({ length: 10_000 }, (_, i) => song(((i * 7919) % 10_000) + 1, 'opus'));
    const first = await boot();
    await first.playerStore.startQueue(queue, 1234);
    first.playerStore.setVolume(0.4);
    first.audio.currentTime = 83.5;

    const { playerStore, localPlayback, audio } = await reload();
    expect(localPlayback.state()).toMatchObject({
      song: queue[1234],
      queue: queue.map((s) => s.id),
      queueIndex: 1234,
      radioSeedId: queue[1234].id
    });
    expect(playerStore.currentSong).toEqual(queue[1234]);
    expect(playerStore.currentTime).toBe(83.5);
    expect(playerStore.volume).toBe(0.4);
    expect(playerStore.hasNext).toBe(true);
    expect(audio.src).toBe(queue[1234].streamUrl);
    expect(audio.currentTime).toBe(83.5);
    expect(audio.play).toHaveBeenCalledTimes(1); // it was playing, and the reload was just now

    // What came back is what is stored: the next write is the small entry alone.
    playerStore.seek(90);
    pageEvents.get('pagehide')!();
    expect(writes).toEqual([SMALL_ENTRY, SMALL_ENTRY]);
  });

  it('brings back a snapshot written before the queue had an entry of its own, and stores it as two', async () => {
    stored.set(
      SMALL_ENTRY,
      JSON.stringify({
        v: 1,
        userId: 'u1',
        queue: library(3),
        queueIndex: 2,
        position: 30,
        wasPlaying: false,
        volume: 1,
        radioSeedId: 1,
        radioExhausted: false,
        miniPlayerDismissed: false,
        savedAt: Date.now()
      })
    );
    const { localPlayback } = await boot();
    expect(localPlayback.state()).toMatchObject({
      queue: [1, 2, 3],
      queueIndex: 2,
      radioSeedId: 1
    });

    const again = await reload();
    expect(stored.has(QUEUE_ENTRY)).toBe(true);
    expect(smallEntry()).toMatchObject({ v: 2, queueIndex: 2 });
    expect(again.localPlayback.state()).toMatchObject({ queue: [1, 2, 3], queueIndex: 2 });
  });

  it("removes both entries rather than inherit another account's queue, and when playback is stopped", async () => {
    const first = await boot();
    await first.playerStore.startQueue(library(3), 0);
    pageEvents.get('pagehide')!();
    expect(stored.size).toBe(2);

    // Another account signs in on this tab.
    vi.resetModules();
    FakeAudio.instances = [];
    const { playerStore, initPlayer } = await import('./player.svelte');
    initPlayer('u2');
    expect(playerStore.currentSong).toBeNull();
    expect(stored.size).toBe(0);

    await playerStore.startQueue(library(3), 0);
    pageEvents.get('pagehide')!();
    expect(stored.size).toBe(2);
    playerStore.stop();
    pageEvents.get('pagehide')!();
    expect(stored.size).toBe(0);
  });
});

describe('the station', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  async function stationFixture() {
    const api = await import('#lib/api-client.ts');
    const { songsStore } = await import('#lib/stores/songs.svelte.ts');
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
    audio.playFrom(200);
    doc.hidden = true;
    audio.loopToStart();
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
