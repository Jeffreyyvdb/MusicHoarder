import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { NativeAudio, type RemoteCommand } from './native-audio.ts';

/**
 * `NativeAudio` against a scripted device: what a new page does about a device that is already
 * playing, and the corners of the protocol the store's own tests (`player-native.svelte.test.ts`)
 * do not reach.
 */

const A = 'https://music.example/a';
const B = 'https://music.example/b';
const C = 'https://music.example/c';

interface Call {
  method: string;
  options: Record<string, unknown>;
}

function makeDevice(atStart: Record<string, unknown> = {}) {
  const calls: Call[] = [];
  const listeners = new Map<string, (data: unknown) => void>();
  const answers = new Map<string, unknown>([['getState', atStart]]);
  return {
    calls,
    answers,
    of: (method: string) => calls.filter((call) => call.method === method),
    emit: (event: string, data: unknown) => listeners.get(event)!(data),
    plugin: {
      call: async (method: string, options: Record<string, unknown> = {}) => {
        calls.push({ method, options });
        return answers.get(method) ?? {};
      },
      listen: (event: string, handler: (data: unknown) => void) =>
        void listeners.set(event, handler)
    }
  };
}

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

const playing = (loadId: number, trail: string[], more: Record<string, unknown> = {}) => ({
  loadId,
  seq: 40,
  index: trail.length - 1,
  trail,
  status: 'playing',
  position: 30,
  duration: 200,
  ended: false,
  ...more
});

beforeEach(() => {
  vi.stubGlobal('document', { visibilityState: 'visible', addEventListener: vi.fn() });
  vi.stubGlobal('location', { href: 'https://music.example/overview' });
});

afterEach(() => vi.unstubAllGlobals());

describe('a page that starts while the device is playing', () => {
  it('takes over the track it was about to load instead of starting it again', async () => {
    const device = makeDevice(playing(77, [A]));
    const audio = new NativeAudio(device.plugin, 'ios');
    const heard: string[] = [];
    audio.addEventListener('play', () => heard.push('play'));

    audio.src = A;
    audio.load();
    audio.currentTime = 12; // where the reload snapshot last saw it
    await settle();

    expect(device.of('loadTrack')).toHaveLength(0);
    expect(device.of('stop')).toHaveLength(0);
    expect(audio.paused).toBe(false);
    expect(heard).toEqual(['play']);
    expect(audio.currentTime).toBeGreaterThanOrEqual(30);
    // From here on it speaks of the device's own load.
    expect(device.of('setUpcoming').at(-1)?.options).toMatchObject({ loadId: 77, at: 0 });
  });

  it('follows a device that has played on past that track', async () => {
    const device = makeDevice(playing(77, [A, B, C]));
    const audio = new NativeAudio(device.plugin, 'ios');
    const ends: number[] = [];
    // The store's part: a song's end loads the next one.
    const queue = [B, C];
    audio.addEventListener('seeking', () => {
      ends.push(audio.currentTime);
      const next = queue.shift();
      if (!next) return;
      audio.src = next;
      audio.load();
      void audio.play();
    });

    audio.src = B;
    queue.shift();
    audio.load();
    await settle();

    expect(ends).toHaveLength(1); // B came round; A was before this page's time
    expect(audio.src).toBe(C);
    expect(device.of('loadTrack')).toHaveLength(0);
    expect(device.of('setUpcoming').at(-1)?.options).toMatchObject({ loadId: 77, at: 2 });
  });

  it('loads its own track when the device is on something else', async () => {
    const device = makeDevice(playing(77, [C]));
    const audio = new NativeAudio(device.plugin, 'ios');

    audio.src = A;
    audio.load();
    void audio.play();
    await settle();

    expect(device.of('loadTrack')).toHaveLength(1);
    expect(device.of('loadTrack')[0].options).toMatchObject({ url: A, autoplay: true });
  });

  it('loads its own track when the device holds it paused', async () => {
    const device = makeDevice(playing(77, [A], { status: 'paused' }));
    const audio = new NativeAudio(device.plugin, 'ios');

    audio.src = A;
    audio.load();
    audio.currentTime = 12;
    await settle();

    expect(device.of('loadTrack')[0].options).toMatchObject({
      url: A,
      position: 12,
      autoplay: false
    });
  });

  it('stops a device nothing here asked for', async () => {
    const device = makeDevice(playing(77, [A]));
    new NativeAudio(device.plugin, 'ios');
    await settle();

    expect(device.of('stop')).toHaveLength(1);
  });

  it('leaves an idle device alone', async () => {
    const device = makeDevice({});
    new NativeAudio(device.plugin, 'ios');
    await settle();

    expect(device.of('stop')).toHaveLength(0);
  });
});

describe('a loaded track', () => {
  async function loaded() {
    const device = makeDevice();
    const audio = new NativeAudio(device.plugin, 'ios');
    await settle();
    audio.src = A;
    audio.load();
    void audio.play();
    await settle();
    const { loadId, seq } = device.of('loadTrack')[0].options as { loadId: number; seq: number };
    const report = (more: Record<string, unknown> = {}) =>
      device.emit('state', { ...playing(loadId, [A]), seq, ...more });
    return { device, audio, loadId, report };
  }

  it('names the track its upcoming list follows', async () => {
    const { device, audio, loadId } = await loaded();

    audio.setUpcoming([{ url: B, title: 'B', artist: '', album: '', artwork: null }]);
    await settle();

    expect(device.of('setUpcoming').at(-1)?.options).toMatchObject({ loadId, at: 0 });
  });

  it('leaves a lock-screen command the device already carried out', async () => {
    const { device, audio } = await loaded();
    const commands: RemoteCommand[] = [];
    audio.onRemote = (command) => commands.push(command);

    device.answers.set('ackRemote', { expired: true });
    device.emit('remote', { id: 1, command: 'next' });
    await settle();
    expect(commands).toEqual([]);

    device.answers.set('ackRemote', { expired: false });
    device.emit('remote', { id: 2, command: 'next' });
    await settle();
    expect(commands).toEqual(['next']);
  });

  it('reports the end of a last track that was played again', async () => {
    const { audio, report } = await loaded();
    let ends = 0;
    audio.addEventListener('seeking', () => (ends += 1));

    report({ position: 199 });
    report({ position: 200, status: 'paused', ended: true });
    expect(ends).toBe(1);

    report({ position: 3 }); // Play again: the device starts the track over
    report({ position: 200, status: 'paused', ended: true });
    expect(ends).toBe(2);
  });

  it('does not take the end of the queue for a rewind', async () => {
    const { audio, report } = await loaded();
    const commands: RemoteCommand[] = [];
    audio.onRemote = (command) => commands.push(command);

    report({ position: 199 });
    report({ position: 0, status: 'paused', ended: true }); // iOS has let go of the item

    expect(commands).toEqual([]);
  });
});
