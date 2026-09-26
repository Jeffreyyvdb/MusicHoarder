import { describe, expect, it } from 'vitest';
import {
  DEVICE_ID_KEY,
  claimTabDeviceId,
  describeDevice,
  isValidDeviceId,
  randomDeviceId,
  readOrCreateId,
  type ChannelLike
} from './device';

class MemoryStorage {
  data = new Map<string, string>();
  getItem(key: string) {
    return this.data.get(key) ?? null;
  }
  setItem(key: string, value: string) {
    this.data.set(key, value);
  }
}

const throwingStorage = {
  getItem(): string | null {
    throw new DOMException('denied', 'SecurityError');
  },
  setItem(): void {
    throw new DOMException('denied', 'SecurityError');
  }
};

describe('device ids', () => {
  it('match the contract format', () => {
    expect(isValidDeviceId(randomDeviceId())).toBe(true);
    expect(isValidDeviceId('abc_DEF-12')).toBe(true);
    expect(isValidDeviceId('short')).toBe(false);
    expect(isValidDeviceId('has space 123')).toBe(false);
    expect(isValidDeviceId('x'.repeat(65))).toBe(false);
    expect(isValidDeviceId(null)).toBe(false);
  });

  it('keeps a stored id and stores a new one', () => {
    const storage = new MemoryStorage();
    const first = readOrCreateId(storage, 'k', () => 'created-0001');
    expect(first).toBe('created-0001');
    expect(storage.getItem('k')).toBe('created-0001');
    expect(readOrCreateId(storage, 'k', () => 'created-0002')).toBe('created-0001');
  });

  it('replaces a stored value that is not an id', () => {
    const storage = new MemoryStorage();
    storage.setItem('k', 'nope');
    expect(readOrCreateId(storage, 'k', () => 'created-0003')).toBe('created-0003');
  });

  it('still yields an id when storage throws or is missing', () => {
    expect(readOrCreateId(throwingStorage, 'k', () => 'created-0004')).toBe('created-0004');
    expect(readOrCreateId(null, 'k', () => 'created-0005')).toBe('created-0005');
  });
});

/** An in-memory BroadcastChannel: every channel on the bus hears the others, never itself. */
class Bus {
  channels: FakeChannel[] = [];
  open(): FakeChannel {
    const channel = new FakeChannel(this);
    this.channels.push(channel);
    return channel;
  }
}

class FakeChannel implements ChannelLike {
  listeners: ((event: MessageEvent) => void)[] = [];
  closed = false;
  constructor(private bus: Bus) {}
  postMessage(message: unknown) {
    for (const other of this.bus.channels) {
      if (other === this || other.closed) continue;
      for (const listener of other.listeners) listener({ data: message } as MessageEvent);
    }
  }
  addEventListener(_type: 'message', listener: (event: MessageEvent) => void) {
    this.listeners.push(listener);
  }
  removeEventListener(_type: 'message', listener: (event: MessageEvent) => void) {
    this.listeners = this.listeners.filter((l) => l !== listener);
  }
  close() {
    this.closed = true;
  }
}

/** Timers the test fires by hand. */
function manualTimers() {
  const pending: (() => void)[] = [];
  return {
    setTimer: (fn: () => void) => {
      pending.push(fn);
    },
    fire: () => pending.splice(0).forEach((fn) => fn())
  };
}

describe('claimTabDeviceId', () => {
  let counter = 0;
  const create = () => `fresh-${String(++counter).padStart(4, '0')}`;

  it('keeps the id when no other tab holds it', async () => {
    const bus = new Bus();
    const storage = new MemoryStorage();
    storage.setItem(DEVICE_ID_KEY, 'tab-a-0001');
    const timers = manualTimers();
    const tab = claimTabDeviceId({ storage, channel: bus.open(), create, setTimer: timers.setTimer });
    timers.fire();
    await expect(tab.ready).resolves.toBe('tab-a-0001');
  });

  it('draws a new id when a duplicated tab finds its id taken', async () => {
    const bus = new Bus();
    const timers = manualTimers();
    const originalStorage = new MemoryStorage();
    originalStorage.setItem(DEVICE_ID_KEY, 'tab-a-0001');
    const original = claimTabDeviceId({
      storage: originalStorage,
      channel: bus.open(),
      create,
      setTimer: timers.setTimer
    });
    timers.fire();
    await original.ready;

    // Duplicating a tab copies its sessionStorage, id included.
    const copyStorage = new MemoryStorage();
    copyStorage.setItem(DEVICE_ID_KEY, 'tab-a-0001');
    const copy = claimTabDeviceId({ storage: copyStorage, channel: bus.open(), create, setTimer: timers.setTimer });
    timers.fire();
    const settled = await copy.ready;

    expect(settled).not.toBe('tab-a-0001');
    expect(copyStorage.getItem(DEVICE_ID_KEY)).toBe(settled);
    // The original keeps its id and does not react to the answer meant for the newcomer.
    expect(original.deviceId).toBe('tab-a-0001');
    expect(originalStorage.getItem(DEVICE_ID_KEY)).toBe('tab-a-0001');
  });

  it('stops answering once disposed', async () => {
    const bus = new Bus();
    const timers = manualTimers();
    const storage = new MemoryStorage();
    storage.setItem(DEVICE_ID_KEY, 'tab-b-0001');
    const first = claimTabDeviceId({ storage, channel: bus.open(), create, setTimer: timers.setTimer });
    timers.fire();
    await first.ready;
    first.dispose();

    const again = claimTabDeviceId({ storage, channel: bus.open(), create, setTimer: timers.setTimer });
    timers.fire();
    await expect(again.ready).resolves.toBe('tab-b-0001');
  });

  it('settles at once without a channel', async () => {
    const storage = new MemoryStorage();
    storage.setItem(DEVICE_ID_KEY, 'tab-c-0001');
    const tab = claimTabDeviceId({ storage, channel: null, create });
    await expect(tab.ready).resolves.toBe('tab-c-0001');
    tab.dispose();
  });

  it('works when storage throws', async () => {
    const tab = claimTabDeviceId({ storage: throwingStorage, channel: null, create });
    expect(isValidDeviceId(await tab.ready)).toBe(true);
  });
});

describe('describeDevice', () => {
  const UA = {
    macSafari:
      'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15',
    macChrome:
      'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36',
    winChrome:
      'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36',
    winEdge:
      'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36 Edg/129.0.0.0',
    linuxFirefox: 'Mozilla/5.0 (X11; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0',
    iphoneSafari:
      'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1',
    iphoneChrome:
      'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/129.0.6668.69 Mobile/15E148 Safari/604.1',
    iphoneApp:
      'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148',
    ipad: 'Mozilla/5.0 (iPad; CPU OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1',
    androidChrome:
      'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Mobile Safari/537.36',
    androidTablet:
      'Mozilla/5.0 (Linux; Android 14; SM-X710) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36',
    samsung:
      'Mozilla/5.0 (Linux; Android 14; SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/25.0 Chrome/121.0.0.0 Mobile Safari/537.36',
    chromebook:
      'Mozilla/5.0 (X11; CrOS x86_64 14541.0.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36'
  };

  it.each([
    [UA.macSafari, 'Safari on Mac', 'computer'],
    [UA.macChrome, 'Chrome on Mac', 'computer'],
    [UA.winChrome, 'Chrome on Windows', 'computer'],
    [UA.winEdge, 'Edge on Windows', 'computer'],
    [UA.linuxFirefox, 'Firefox on Linux', 'computer'],
    [UA.iphoneSafari, 'Safari on iPhone', 'phone'],
    [UA.iphoneChrome, 'Chrome on iPhone', 'phone'],
    [UA.ipad, 'Safari on iPad', 'tablet'],
    [UA.androidChrome, 'Chrome on Android', 'phone'],
    [UA.androidTablet, 'Chrome on Android tablet', 'tablet'],
    [UA.samsung, 'Samsung Internet on Android', 'phone'],
    [UA.chromebook, 'Chrome on ChromeOS', 'computer'],
    ['curl/8.0', 'Browser', 'unknown']
  ])('%s → %s', (ua, name, kind) => {
    expect(describeDevice(ua)).toEqual({ name, kind });
  });

  it('names the installed app after the app, not a browser', () => {
    expect(describeDevice(UA.iphoneApp, { installed: true })).toEqual({
      name: 'MusicHoarder on iPhone',
      kind: 'phone'
    });
    expect(describeDevice('curl/8.0', { installed: true }).name).toBe('MusicHoarder');
  });

  it('tells an iPad asking for the desktop site from a Mac by its touch points', () => {
    expect(describeDevice(UA.macSafari, { maxTouchPoints: 5 })).toEqual({
      name: 'Safari on iPad',
      kind: 'tablet'
    });
    expect(describeDevice(UA.macSafari, { maxTouchPoints: 0 }).kind).toBe('computer');
  });
});
