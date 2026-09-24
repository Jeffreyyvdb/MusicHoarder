import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { PlayerSong } from './player.svelte';

/**
 * Background playback in the installed iOS app hangs on two things this store does, neither of
 * which a desktop browser would ever notice going wrong:
 *
 *  • it declares the `playback` audio session on a play intent (and not before one), so WebKit
 *    cannot let the category lapse between two tracks;
 *  • while the page is hidden, the hand-off from `ended` to the next track is synchronous — no
 *    pre-flight round trip to the server sits between the old song ending and the new one playing.
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

  it('streams the AAC rendition when the browser says it cannot play the file, checking the original first', async () => {
    const { playerStore, audio } = await loadPlayer();
    audio.canPlayType.mockImplementation((type) => (type === OGG_OPUS ? '' : 'maybe'));

    await playerStore.playSong(song(1, 'opus'));

    // The pre-flight asks for the original, which answers at once; the rendition waits on ffmpeg.
    expect(fetchMock).toHaveBeenCalledWith('/api/mh/songs/1/stream', {
      headers: { Range: 'bytes=0-0' }
    });
    expect(audio.src).toBe('/api/mh/songs/1/stream?format=aac');
    expect(audio.play).toHaveBeenCalled();
  });

  it('falls back to the rendition when the original fails, and sends the rest of the queue straight there', async () => {
    const { toast } = await import('svelte-sonner');
    const { playerStore, audio } = await loadPlayer();
    await playerStore.playSong(song(1, 'opus'), [song(1, 'opus'), song(2, 'opus')]);
    audio.play.mockClear();
    fetchMock.mockClear();

    // Safari claims Ogg Opus and still refuses the file.
    audio.fail(MEDIA_ERR_SRC_NOT_SUPPORTED);

    expect(audio.src).toBe('/api/mh/songs/1/stream?format=aac');
    expect(audio.play).toHaveBeenCalledTimes(1);
    expect(toast.error).not.toHaveBeenCalled();

    // The original answered and the rendition plays: it was the format. The next track, in the
    // same format, is converted ahead of time and then played as its rendition.
    audio.dispatchEvent(new Event('loadedmetadata'));
    await vi.waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith('/api/mh/songs/2/stream?format=aac', {
        headers: { Range: 'bytes=0-0' }
      })
    );
    playerStore.playNext();
    await vi.waitFor(() => expect(audio.src).toBe('/api/mh/songs/2/stream?format=aac'));
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

  it('does not blame the format when the rendition fails too', async () => {
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

    expect(audio.src).toBe('/api/mh/songs/1/stream?format=aac');
    expect(audio.currentTime).toBe(42);
    expect(audio.play).not.toHaveBeenCalled();
  });

  it('reports a failure the rendition cannot fix, and a failure of the rendition itself', async () => {
    const { toast } = await import('svelte-sonner');
    const { playerStore, audio } = await loadPlayer();
    await playerStore.playSong(song(1, 'opus'));

    audio.fail(MEDIA_ERR_NETWORK);
    expect(audio.src).toBe('/api/mh/songs/1/stream');
    expect(toast.error).toHaveBeenCalledTimes(1);

    await playerStore.playSong(song(2, 'opus'));
    audio.fail(MEDIA_ERR_SRC_NOT_SUPPORTED);
    expect(audio.src).toBe('/api/mh/songs/2/stream?format=aac');
    audio.fail(MEDIA_ERR_SRC_NOT_SUPPORTED);
    expect(toast.error).toHaveBeenCalledTimes(2);
  });

  it('has the server convert the next track while this one plays', async () => {
    const { playerStore, audio } = await loadPlayer();
    audio.canPlayType.mockImplementation((type) => (type === OGG_OPUS ? '' : 'maybe'));
    await playerStore.playSong(song(1, 'mp3'), [song(1, 'mp3'), song(2, 'opus'), song(3, 'opus')]);
    fetchMock.mockClear();

    audio.dispatchEvent(new Event('loadedmetadata'));
    audio.dispatchEvent(new Event('loadedmetadata'));

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledWith('/api/mh/songs/2/stream?format=aac', {
      headers: { Range: 'bytes=0-0' }
    });
  });

  it('converts nothing ahead when the next track plays as it is', async () => {
    const { playerStore, audio } = await loadPlayer();
    await playerStore.playSong(song(1, 'opus'), [song(1, 'opus'), song(2, 'opus')]);
    fetchMock.mockClear();

    audio.dispatchEvent(new Event('loadedmetadata'));

    expect(fetchMock).not.toHaveBeenCalled();
  });
});
