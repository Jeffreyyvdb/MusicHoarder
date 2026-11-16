import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// The node environment has no Web Audio: a context that records the tones it starts and the state
// it was in, and that starts only when the test says so (as WebKit starts one only on a gesture).
let started: string[] = [];
let contexts: FakeContext[] = [];

class FakeNode {
  constructor(private ctx: FakeContext) {}
  connect(next: unknown) {
    return next;
  }
  start() {
    started.push(this.ctx.state);
  }
  stop() {}
  frequency = { value: 0, setValueAtTime() {}, exponentialRampToValueAtTime() {} };
  gain = { value: 0, setValueAtTime() {}, linearRampToValueAtTime() {}, exponentialRampToValueAtTime() {} };
  Q = { value: 0 };
  playbackRate = { value: 1 };
  type = '';
  buffer: unknown = null;
}

class FakeContext {
  state: 'suspended' | 'running' | 'closed' = 'suspended';
  currentTime = 0;
  sampleRate = 48000;
  destination = {};
  private pending: (() => void)[] = [];
  constructor() {
    contexts.push(this);
  }
  /** Stays pending until the context starts, as the spec has it. */
  resume() {
    if (this.state === 'running') return Promise.resolve();
    return new Promise<void>((resolve) => this.pending.push(resolve));
  }
  start() {
    this.state = 'running';
    this.pending.splice(0).forEach((resolve) => resolve());
  }
  createGain() {
    return new FakeNode(this);
  }
  createOscillator() {
    return new FakeNode(this);
  }
  createBufferSource() {
    return new FakeNode(this);
  }
  createBiquadFilter() {
    return new FakeNode(this);
  }
  createBuffer(_channels: number, length: number) {
    return { getChannelData: () => new Float32Array(length) };
  }
}

let session: { type: string };

function stubPage({ shell }: { shell: boolean }) {
  session = { type: 'auto' };
  vi.stubGlobal('localStorage', { getItem: () => 'on', setItem() {} });
  vi.stubGlobal('document', { documentElement: { dataset: { mhSkin: 'hardware' } }, activeElement: null });
  vi.stubGlobal('navigator', { audioSession: session });
  vi.stubGlobal('window', {
    AudioContext: FakeContext,
    ...(shell ? { Capacitor: { isNativePlatform: () => true, getPlatform: () => 'ios' } } : {})
  });
}

async function load() {
  vi.resetModules();
  return import('./sound.ts');
}

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

beforeEach(() => {
  started = [];
  contexts = [];
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('key sounds', () => {
  it('play a sound asked for before the context started once it starts, and only that one', async () => {
    stubPage({ shell: false });
    const { soundFor } = await load();
    soundFor('success');
    soundFor('error');
    expect(started).toEqual([]);
    contexts[0].start();
    await flush();
    // One chime (two tones), made on a running clock; the error asked meanwhile is dropped.
    expect(started).toEqual(['running', 'running']);
  });

  it('drop a waiting sound once it no longer belongs to the press', async () => {
    stubPage({ shell: false });
    const now = vi.spyOn(performance, 'now').mockReturnValue(1000);
    const { soundFor } = await load();
    soundFor('success');
    now.mockReturnValue(2000);
    contexts[0].start();
    await flush();
    expect(started).toEqual([]);
    now.mockRestore();
  });

  it('play at once on a running context', async () => {
    stubPage({ shell: false });
    const { soundFor } = await load();
    soundFor('success');
    contexts[0].start();
    await flush();
    started = [];
    soundFor('success');
    expect(started).toEqual(['running', 'running']);
  });

  it('make no audio context in the app shell, whose audio session is the native player’s', async () => {
    // A context there let WebKit set the shared session to ambient: swiping the app away to the
    // Home Screen stopped the music. Declaring `playback` instead took the session from the player.
    stubPage({ shell: true });
    const { soundFor, soundsAvailable } = await load();
    soundFor('success');
    soundFor('light');
    await flush();
    expect(contexts).toEqual([]);
    expect(session.type).toBe('auto');
    expect(soundsAvailable()).toBe(false);
  });

  it('leave a browser page’s audio session alone', async () => {
    stubPage({ shell: false });
    const { soundFor } = await load();
    soundFor('success');
    expect(session.type).toBe('auto');
  });
});
