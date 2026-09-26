import { describe, expect, it } from 'vitest';
import {
  parseCommandEvent,
  parseDevice,
  parseDevices,
  parseDevicesEvent,
  parseEventData,
  parseOverview,
  parseSession,
  parseSessionEvent,
  parseStateResponse
} from './wire';

const wireSession = {
  version: 42,
  songId: 123,
  title: 'Nightswim',
  artist: 'R.E.M.',
  album: 'Automatic for the People',
  queue: [120, 123, 131],
  queueIndex: 1,
  positionMs: 83000,
  durationMs: 215000,
  isPlaying: true,
  playbackRate: 1.0,
  radioSeedId: 120,
  shuffle: false,
  activeDeviceId: 'abc12345',
  activeDeviceName: 'Safari on iPhone',
  live: true,
  lastCommandId: null,
  updatedAtUtc: '2026-09-25T10:00:00Z'
};

describe('parseSession', () => {
  it('reads the contract example as it is', () => {
    expect(parseSession(wireSession)).toEqual(wireSession);
  });

  it('refuses a message without its identity', () => {
    expect(parseSession(null)).toBeNull();
    expect(parseSession([])).toBeNull();
    expect(parseSession({ ...wireSession, version: undefined })).toBeNull();
    expect(parseSession({ ...wireSession, version: -1 })).toBeNull();
    expect(parseSession({ ...wireSession, version: 1.5 })).toBeNull();
    expect(parseSession({ ...wireSession, songId: null })).toBeNull();
    expect(parseSession({ ...wireSession, songId: '123' })).toBeNull();
  });

  it('refuses a queue it could not play from', () => {
    expect(parseSession({ ...wireSession, queue: [] })).toBeNull();
    expect(parseSession({ ...wireSession, queue: 'x' })).toBeNull();
    expect(parseSession({ ...wireSession, queue: [1, 'two', 3] })).toBeNull();
    expect(parseSession({ ...wireSession, queueIndex: 3 })).toBeNull();
    expect(parseSession({ ...wireSession, queueIndex: -1 })).toBeNull();
    expect(parseSession({ ...wireSession, queueIndex: null })).toBeNull();
  });

  it('defaults what it only displays', () => {
    const parsed = parseSession({
      version: 1,
      songId: 5,
      queue: [5],
      queueIndex: 0,
      positionMs: -20,
      durationMs: 0,
      playbackRate: 0,
      radioSeedId: 'x',
      title: 3,
      activeDeviceId: '  ',
      live: 'yes',
      isPlaying: 1
    });
    expect(parsed).toMatchObject({
      title: null,
      artist: null,
      positionMs: 0,
      durationMs: null,
      playbackRate: 1,
      radioSeedId: null,
      activeDeviceId: null,
      activeDeviceName: null,
      live: false,
      isPlaying: false,
      shuffle: false,
      lastCommandId: null
    });
  });

  it('does not share the queue array with the message', () => {
    const raw = { ...wireSession, queue: [1, 2] , queueIndex: 0 };
    const parsed = parseSession(raw)!;
    raw.queue.push(3);
    expect(parsed.queue).toEqual([1, 2]);
  });
});

describe('parseDevice(s)', () => {
  const device = {
    deviceId: 'dev-00001',
    installId: 'inst-0001',
    name: 'Safari on Mac',
    kind: 'computer',
    client: 'web',
    online: true,
    isActive: false
  };

  it('reads a device', () => {
    expect(parseDevice(device)).toEqual(device);
  });

  it('drops a device nobody could address, and only that one', () => {
    expect(parseDevice({ ...device, deviceId: '' })).toBeNull();
    expect(parseDevices([device, { name: 'ghost' }, 7, { ...device, deviceId: 'dev-00002' }])).toHaveLength(2);
    expect(parseDevices('nope')).toEqual([]);
  });

  it('defaults an unknown kind, client and name', () => {
    expect(parseDevice({ deviceId: 'dev-00003', kind: 'fridge', client: 'ios' })).toEqual({
      deviceId: 'dev-00003',
      installId: null,
      name: 'Unknown device',
      kind: 'unknown',
      client: 'web',
      online: false,
      isActive: false
    });
    expect(parseDevice({ deviceId: 'dev-00004', client: 'android' })?.client).toBe('android');
  });
});

describe('events', () => {
  it('snapshot: an invalid session reads as none, the devices still count', () => {
    const overview = parseOverview({ session: { version: 'x' }, devices: [{ deviceId: 'dev-00001' }] });
    expect(overview?.session).toBeNull();
    expect(overview?.devices).toHaveLength(1);
    expect(parseOverview({ session: null, devices: [] })).toEqual({ session: null, devices: [] });
    expect(parseOverview('x')).toBeNull();
  });

  it('session: only a valid session', () => {
    expect(parseSessionEvent({ session: wireSession })?.version).toBe(42);
    expect(parseSessionEvent({ session: null })).toBeNull();
    expect(parseSessionEvent(wireSession)).toBeNull();
  });

  it('devices: needs a list', () => {
    expect(parseDevicesEvent({ devices: [] })).toEqual([]);
    expect(parseDevicesEvent({})).toBeNull();
  });

  it('command: known commands only, and a seek needs its position', () => {
    const base = { commandId: 'c1', fromDeviceId: 'dev-00001', fromDeviceName: 'Safari on Mac' };
    expect(parseCommandEvent({ ...base, command: 'pause', positionMs: null })).toEqual({
      ...base,
      command: 'pause',
      positionMs: null
    });
    expect(parseCommandEvent({ ...base, command: 'seek', positionMs: 12345 })?.positionMs).toBe(12345);
    expect(parseCommandEvent({ ...base, command: 'seek', positionMs: null })).toBeNull();
    expect(parseCommandEvent({ ...base, command: 'shuffle' })).toBeNull();
    expect(parseCommandEvent({ command: 'pause' })).toBeNull();
    expect(parseCommandEvent({ ...base, command: 'next', fromDeviceName: 7 })?.fromDeviceName).toBeNull();
  });

  it('state response: anything but an explicit yes is a refusal', () => {
    expect(parseStateResponse({ accepted: true, session: wireSession }).accepted).toBe(true);
    expect(parseStateResponse({ accepted: 'true', session: wireSession }).accepted).toBe(false);
    expect(parseStateResponse(null)).toEqual({ accepted: false, session: null });
  });

  it('event data: JSON or nothing', () => {
    expect(parseEventData('{"a":1}')).toEqual({ a: 1 });
    expect(parseEventData('{')).toBeNull();
    expect(parseEventData(undefined)).toBeNull();
  });
});
