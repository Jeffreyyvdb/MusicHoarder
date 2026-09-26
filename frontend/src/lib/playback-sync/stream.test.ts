import { describe, expect, it, vi } from 'vitest';
import {
  PLAYBACK_STREAM_MAX_BACKOFF_MS,
  nextStreamBackoff,
  openPlaybackStream,
  playbackStreamUrl
} from '$lib/api-client';

/**
 * The stream opener in api-client: one listener per named server event, validation before any
 * handler sees a payload, and its own retry for the one case `EventSource` gives up on.
 */

class FakeEventSource {
  static instances: FakeEventSource[] = [];
  readyState = 0;
  closed = false;
  onerror: (() => void) | null = null;
  private listeners = new Map<string, ((event: Event) => void)[]>();
  constructor(public url: string) {
    FakeEventSource.instances.push(this);
  }
  addEventListener(type: string, listener: (event: Event) => void) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]);
  }
  close() {
    this.closed = true;
    this.readyState = 2;
  }
  emit(type: string, data: unknown) {
    const event = { data: typeof data === 'string' ? data : JSON.stringify(data) } as MessageEvent;
    for (const listener of this.listeners.get(type) ?? []) listener(event);
  }
  fail(state: 0 | 2) {
    this.readyState = state;
    this.onerror?.();
  }
}

const device = { deviceId: 'dev-00001', installId: 'inst-0001', name: 'Safari on Mac', kind: 'computer' as const };
const session = {
  version: 3,
  songId: 5,
  queue: [5],
  queueIndex: 0,
  positionMs: 0,
  isPlaying: false,
  live: true
};

function open() {
  FakeEventSource.instances = [];
  const handlers = {
    onSnapshot: vi.fn(),
    onSession: vi.fn(),
    onDevices: vi.fn(),
    onCommand: vi.fn(),
    onDown: vi.fn()
  };
  const timers: { fn: () => void; ms: number }[] = [];
  const close = openPlaybackStream(device, handlers, {
    createEventSource: (url) => new FakeEventSource(url) as unknown as EventSource,
    setTimer: (fn, ms) => {
      timers.push({ fn, ms });
      return timers.length as unknown as ReturnType<typeof setTimeout>;
    },
    clearTimer: () => {}
  });
  return { handlers, timers, close, es: () => FakeEventSource.instances.at(-1)! };
}

describe('playbackStreamUrl', () => {
  it('goes through the same-origin proxy with the device in the query', () => {
    const url = new URL(playbackStreamUrl(device), 'https://x.test');
    expect(url.pathname).toBe('/api/mh/api/playback/stream');
    expect(Object.fromEntries(url.searchParams)).toEqual({
      deviceId: 'dev-00001',
      installId: 'inst-0001',
      name: 'Safari on Mac',
      kind: 'computer',
      client: 'web'
    });
  });

  it('leaves out a missing install id', () => {
    expect(playbackStreamUrl({ ...device, installId: null })).not.toContain('installId');
  });
});

describe('openPlaybackStream', () => {
  it('hands each named event to its handler, validated', () => {
    const { handlers, es } = open();
    es().emit('snapshot', { session, devices: [] });
    es().emit('session', { session: { ...session, version: 4 } });
    es().emit('devices', { devices: [{ deviceId: 'dev-00002' }] });
    es().emit('command', { commandId: 'c1', command: 'pause' });
    es().emit('ping', {});

    expect(handlers.onSnapshot).toHaveBeenCalledWith(
      expect.objectContaining({ session: expect.objectContaining({ version: 3 }), devices: [] })
    );
    expect(handlers.onSession).toHaveBeenCalledWith(expect.objectContaining({ version: 4 }));
    expect(handlers.onDevices).toHaveBeenCalledWith([expect.objectContaining({ deviceId: 'dev-00002' })]);
    expect(handlers.onCommand).toHaveBeenCalledWith(expect.objectContaining({ commandId: 'c1', command: 'pause' }));
  });

  it('drops what does not validate', () => {
    const { handlers, es } = open();
    es().emit('session', { session: { version: 'x' } });
    es().emit('command', { commandId: 'c1', command: 'explode' });
    es().emit('snapshot', 'not json {');
    expect(handlers.onSession).not.toHaveBeenCalled();
    expect(handlers.onCommand).not.toHaveBeenCalled();
    expect(handlers.onSnapshot).not.toHaveBeenCalled();
  });

  it('leaves a dropped connection to the browser while it is reconnecting', () => {
    const { handlers, timers, es } = open();
    es().fail(0);
    expect(handlers.onDown).not.toHaveBeenCalled();
    expect(timers).toHaveLength(0);
  });

  it("treats the server's routine end of a stream as the browser's reconnect: no backoff", () => {
    const { handlers, timers, es } = open();
    const source = es();
    source.emit('snapshot', { session, devices: [] });
    // The server ends every stream after a few minutes; EventSource reopens the same object.
    source.fail(0);
    source.emit('snapshot', { session, devices: [] });
    expect(handlers.onSnapshot).toHaveBeenCalledTimes(2);
    expect(handlers.onDown).not.toHaveBeenCalled();
    expect(timers).toHaveLength(0);
    expect(FakeEventSource.instances).toHaveLength(1);

    // Its reopening refused (the API restarting behind the proxy): retried from 1 s, not later.
    source.fail(2);
    expect(timers.at(-1)?.ms).toBe(1000);
  });

  it('reopens a stream the browser gave up on, backing off', () => {
    const { handlers, timers, es } = open();
    const first = es();
    first.fail(2);
    expect(handlers.onDown).toHaveBeenCalledWith(1000);
    expect(timers.at(-1)?.ms).toBe(1000);

    timers.at(-1)!.fn();
    expect(FakeEventSource.instances).toHaveLength(2);
    es().fail(2);
    expect(timers.at(-1)?.ms).toBe(2000);

    // A snapshot proves the stream works: the next failure starts from 1 s again.
    timers.at(-1)!.fn();
    es().emit('snapshot', { session: null, devices: [] });
    es().fail(2);
    expect(timers.at(-1)?.ms).toBe(1000);
  });

  it('closes for good', () => {
    const { timers, es, close } = open();
    const source = es();
    close();
    expect(source.closed).toBe(true);
    source.fail(2);
    expect(timers).toHaveLength(0);
  });
});

describe('nextStreamBackoff', () => {
  it('doubles from 1 s up to 30 s', () => {
    expect(nextStreamBackoff(0)).toBe(1000);
    expect(nextStreamBackoff(1000)).toBe(2000);
    expect(nextStreamBackoff(16_000)).toBe(PLAYBACK_STREAM_MAX_BACKOFF_MS);
    expect(nextStreamBackoff(PLAYBACK_STREAM_MAX_BACKOFF_MS)).toBe(PLAYBACK_STREAM_MAX_BACKOFF_MS);
  });
});
