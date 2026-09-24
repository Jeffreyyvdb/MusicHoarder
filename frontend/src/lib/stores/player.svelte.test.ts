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

  constructor() {
    super();
    FakeAudio.instances.push(this);
  }
}

const song = (id: number): PlayerSong => ({
  id,
  title: `Track ${id}`,
  artist: 'Artist',
  streamUrl: `/api/mh/songs/${id}/stream`
});

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
