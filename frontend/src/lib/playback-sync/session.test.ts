import { describe, expect, it } from 'vitest';
import {
  QUEUE_CAP,
  adoptStart,
  commandLanded,
  deriveMode,
  deviceLabel,
  extrapolatePositionMs,
  isSameBrowser,
  isSuperseded,
  landedCommands,
  mayAutoResume,
  nowPlayingElsewhere,
  resolveAdoptQueue,
  sessionLine,
  shouldApplySession,
  showsSession,
  snapshotReportFor,
  trimQueue,
  withLocalClaim
} from './session';
import type { PlaybackSession } from './wire';

const ME = 'me-000001';
const PHONE = 'phone-0001';

function session(overrides: Partial<PlaybackSession> = {}): PlaybackSession {
  return {
    version: 1,
    songId: 123,
    title: 'Nightswim',
    artist: 'R.E.M.',
    album: null,
    queue: [120, 123, 131],
    queueIndex: 1,
    positionMs: 83_000,
    durationMs: 215_000,
    isPlaying: true,
    playbackRate: 1,
    radioSeedId: 120,
    shuffle: false,
    activeDeviceId: PHONE,
    activeDeviceName: 'Safari on iPhone',
    live: true,
    lastCommandId: null,
    updatedAtUtc: null,
    ...overrides
  };
}

describe('deriveMode', () => {
  it('is local with no session, the feature off, or no id yet', () => {
    expect(deriveMode(null, ME, true)).toBe('local');
    expect(deriveMode(session(), ME, false)).toBe('local');
    expect(deriveMode(session(), null, true)).toBe('local');
  });

  it('is local while this device holds it, live or not', () => {
    expect(deriveMode(session({ activeDeviceId: ME }), ME, true)).toBe('local');
    expect(deriveMode(session({ activeDeviceId: ME, live: false }), ME, true)).toBe('local');
  });

  it('is remote while another reachable device holds it', () => {
    expect(deriveMode(session(), ME, true)).toBe('remote');
    expect(deriveMode(session({ isPlaying: false }), ME, true)).toBe('remote');
  });

  it('is remembered when the device holding it is gone (or there is none)', () => {
    expect(deriveMode(session({ live: false }), ME, true)).toBe('remembered');
    expect(deriveMode(session({ activeDeviceId: null, live: false }), ME, true)).toBe('remembered');
  });
});

describe('showsSession', () => {
  it('shows the session away from local, never without one', () => {
    expect(showsSession('remote', session(), true)).toBe(true);
    expect(showsSession('remembered', session(), true)).toBe(true);
    expect(showsSession('remote', null, false)).toBe(false);
  });

  it('shows it in local mode only when nothing is loaded here', () => {
    expect(showsSession('local', session({ activeDeviceId: ME }), true)).toBe(false);
    expect(showsSession('local', session({ activeDeviceId: ME }), false)).toBe(true);
  });
});

describe('withLocalClaim', () => {
  it('names this device as the live holder, leaving the rest', () => {
    const claimed = withLocalClaim(session({ live: false }), ME, 'Safari on Mac');
    expect(claimed).toMatchObject({
      activeDeviceId: ME,
      activeDeviceName: 'Safari on Mac',
      live: true,
      version: 1,
      songId: 123
    });
    expect(deriveMode(claimed, ME, true)).toBe('local');
  });
});

describe('extrapolatePositionMs', () => {
  it('stands still while paused', () => {
    expect(extrapolatePositionMs(session({ isPlaying: false }), 1000, 61_000)).toBe(83_000);
  });

  it('moves with the clock while playing', () => {
    expect(extrapolatePositionMs(session(), 1000, 3500)).toBe(85_500);
  });

  it('moves at the playback rate', () => {
    expect(extrapolatePositionMs(session({ playbackRate: 0.5 }), 0, 10_000)).toBe(88_000);
    expect(extrapolatePositionMs(session({ playbackRate: 0 }), 0, 1000)).toBe(84_000);
  });

  it('never runs past the end, nor backwards', () => {
    expect(extrapolatePositionMs(session(), 0, 1_000_000)).toBe(215_000);
    expect(extrapolatePositionMs(session(), 5000, 1000)).toBe(83_000);
    expect(extrapolatePositionMs(session({ positionMs: -5 }), 0, 0)).toBe(0);
  });

  it('runs on without a known duration', () => {
    expect(extrapolatePositionMs(session({ durationMs: null }), 0, 500_000)).toBe(583_000);
  });
});

describe('trimQueue', () => {
  const range = (n: number) => Array.from({ length: n }, (_, i) => i);

  it('sends a queue within the cap as it is', () => {
    expect(trimQueue([1, 2, 3], 2)).toEqual({ queue: [1, 2, 3], queueIndex: 2 });
    expect(trimQueue(range(QUEUE_CAP), 999).queue).toHaveLength(QUEUE_CAP);
  });

  it('keeps the current song and 50 before it, re-basing the index', () => {
    const ids = range(2000);
    const trimmed = trimQueue(ids, 700);
    expect(trimmed.queue).toHaveLength(QUEUE_CAP);
    expect(trimmed.queue[0]).toBe(650);
    expect(trimmed.queue.at(-1)).toBe(1649);
    expect(trimmed.queue[trimmed.queueIndex]).toBe(700);
    expect(trimmed.queueIndex).toBe(50);
  });

  it('starts at the head when the song is near it', () => {
    const trimmed = trimQueue(range(1200), 10);
    expect(trimmed.queue[0]).toBe(0);
    expect(trimmed.queueIndex).toBe(10);
    expect(trimmed.queue).toHaveLength(QUEUE_CAP);
  });

  it('takes what is left near the tail', () => {
    const trimmed = trimQueue(range(1500), 1499);
    expect(trimmed.queue).toEqual(range(1500).slice(1449));
    expect(trimmed.queue[trimmed.queueIndex]).toBe(1499);
  });
});

describe('shouldApplySession', () => {
  it('always applies a snapshot, even an older one', () => {
    expect(shouldApplySession(3, 10, 'snapshot')).toBe(true);
  });

  it('applies a session event only when it is newer', () => {
    expect(shouldApplySession(11, 10, 'session')).toBe(true);
    expect(shouldApplySession(10, 10, 'session')).toBe(false);
    expect(shouldApplySession(9, 10, 'session')).toBe(false);
    expect(shouldApplySession(0, null, 'session')).toBe(true);
  });
});

describe('isSuperseded', () => {
  it('is when another device holds it while this one plays', () => {
    expect(isSuperseded(session(), ME, true, false)).toBe(true);
  });

  it("is not while paused (or playing a share link's track), holding it, or claiming it", () => {
    expect(isSuperseded(session(), ME, false, false)).toBe(false);
    expect(isSuperseded(session({ activeDeviceId: ME }), ME, true, false)).toBe(false);
    expect(isSuperseded(session(), ME, true, true)).toBe(false);
  });

  it('is not without a session or a holder', () => {
    expect(isSuperseded(null, ME, true, false)).toBe(false);
    expect(isSuperseded(session({ activeDeviceId: null }), ME, true, false)).toBe(false);
  });
});

describe('mayAutoResume', () => {
  it('resumes with no session, or when this device still holds it', () => {
    expect(mayAutoResume(null, ME)).toBe(true);
    expect(mayAutoResume(session({ activeDeviceId: ME }), ME)).toBe(true);
  });

  it('resumes when the session elsewhere is only remembered', () => {
    expect(mayAutoResume(session({ live: false }), ME)).toBe(true);
  });

  it('stays quiet while another device is live, even paused', () => {
    expect(mayAutoResume(session(), ME)).toBe(false);
    expect(mayAutoResume(session({ isPlaying: false }), ME)).toBe(false);
  });
});

// Case for case with Android's `snapshotReportFor` (PlaybackSyncTest).
describe('snapshotReportFor', () => {
  it('a holder with the session loaded reports at once, playing or not', () => {
    expect(snapshotReportFor(session({ activeDeviceId: ME }), ME, true)).toBe('heartbeat');
    expect(snapshotReportFor(session({ activeDeviceId: ME, isPlaying: false }), ME, true)).toBe(
      'heartbeat'
    );
  });

  it('a holder with nothing of it loaded, while it still says playing here, says it stopped', () => {
    // Back within the server's reconnect grace: the server cannot tell, and would keep the session
    // playing a minute past where the music stopped.
    expect(snapshotReportFor(session({ activeDeviceId: ME, isPlaying: true }), ME, false)).toBe(
      'stopped'
    );
    // Already paused (or frozen by the server): nothing to put right.
    expect(snapshotReportFor(session({ activeDeviceId: ME, isPlaying: false }), ME, false)).toBe(
      'none'
    );
  });

  it("another device's session, or none, asks nothing of this one", () => {
    expect(snapshotReportFor(session(), ME, true)).toBe('none');
    expect(snapshotReportFor(session(), ME, false)).toBe('none');
    expect(snapshotReportFor(null, ME, false)).toBe('none');
    // Before this tab knows its id, no session can name it.
    expect(snapshotReportFor(session({ activeDeviceId: null }), null, false)).toBe('none');
  });
});

describe('commandLanded', () => {
  const pause = {
    commandId: 'c1',
    command: 'pause' as const,
    targetDeviceId: PHONE,
    targetHeldSession: true
  };

  it('lands on its acknowledgement', () => {
    expect(commandLanded(session({ lastCommandId: 'c1' }), pause)).toBe(true);
    expect(commandLanded(session({ lastCommandId: 'c0' }), pause)).toBe(false);
    expect(commandLanded(null, pause)).toBe(false);
  });

  it('a transfer lands when the target holds the session', () => {
    const transfer = {
      commandId: 'c2',
      command: 'transfer' as const,
      targetDeviceId: 'tv-000001',
      targetHeldSession: false
    };
    expect(commandLanded(session({ activeDeviceId: 'tv-000001' }), transfer)).toBe(true);
    expect(commandLanded(session(), transfer)).toBe(false);
    // Only a transfer lands that way.
    expect(commandLanded(session(), { ...pause, targetDeviceId: PHONE })).toBe(false);
  });

  it('a transfer to the device that already held the session lands only on its acknowledgement', () => {
    // The phone is the remembered holder, back online with nothing loaded: the session names it
    // before the transfer has done anything there.
    const transfer = {
      commandId: 'c3',
      command: 'transfer' as const,
      targetDeviceId: PHONE,
      targetHeldSession: true
    };
    expect(commandLanded(session({ live: false, isPlaying: false }), transfer)).toBe(false);
    expect(commandLanded(session({ live: true }), transfer)).toBe(false);
    // Its claim answers the transfer by id.
    expect(commandLanded(session({ live: true, lastCommandId: 'c3' }), transfer)).toBe(true);
  });
});

describe('landedCommands', () => {
  const next = (commandId: string, targetDeviceId: string | null = PHONE) => ({
    commandId,
    command: 'next' as const,
    targetDeviceId,
    targetHeldSession: true
  });

  it('a later command landing settles the ones sent before it to the same device', () => {
    // Three taps on Next; the phone acknowledged the burst with the newest id only.
    const pending = [next('c1'), next('c2'), next('c3')];
    expect(landedCommands(session({ lastCommandId: 'c3' }), pending)).toEqual(['c1', 'c2', 'c3']);
    expect(landedCommands(session({ lastCommandId: 'c2' }), pending)).toEqual(['c1', 'c2']);
  });

  it('never settles a command sent after the one that landed, nor one to another device', () => {
    const pending = [next('c1', 'tv-000001'), next('c2'), next('c3')];
    expect(landedCommands(session({ lastCommandId: 'c2' }), pending)).toEqual(['c2']);
    expect(landedCommands(session({ lastCommandId: 'c0' }), pending)).toEqual([]);
    expect(landedCommands(null, pending)).toEqual([]);
  });
});

describe('labels', () => {
  const tab = { deviceId: 'tab-000002', installId: 'inst-0001', name: 'Safari on Mac' };
  const phone = { deviceId: PHONE, installId: 'inst-0002', name: 'Safari on iPhone' };

  it('names this device, another tab of this browser, and the rest', () => {
    expect(deviceLabel({ ...tab, deviceId: ME }, ME, 'inst-0001')).toBe('This device');
    expect(deviceLabel(tab, ME, 'inst-0001')).toBe('Another tab');
    expect(deviceLabel(phone, ME, 'inst-0001')).toBe('Safari on iPhone');
    expect(deviceLabel(tab, ME, null)).toBe('Safari on Mac');
  });

  it('isSameBrowser needs a shared install id and a different device', () => {
    expect(isSameBrowser(tab, ME, 'inst-0001')).toBe(true);
    expect(isSameBrowser({ ...tab, deviceId: ME }, ME, 'inst-0001')).toBe(false);
    expect(isSameBrowser({ ...tab, installId: null }, ME, null)).toBe(false);
    expect(isSameBrowser(null, ME, 'inst-0001')).toBe(false);
  });

  it('says where the session is', () => {
    expect(sessionLine('remote', session(), false)).toBe('Playing on Safari on iPhone');
    expect(sessionLine('remote', session({ isPlaying: false }), false)).toBe('Paused on Safari on iPhone');
    expect(sessionLine('remembered', session({ live: false }), false)).toBe(
      'Last played on Safari on iPhone'
    );
    expect(sessionLine('remote', session(), true)).toBe('Playing in another tab');
    expect(sessionLine('remembered', session(), true)).toBe('Last played in another tab');
  });

  it('manages without a name, and says nothing in local mode', () => {
    expect(sessionLine('remote', session({ activeDeviceName: null }), false)).toBe(
      'Playing on another device'
    );
    expect(sessionLine('remembered', session({ activeDeviceName: '  ' }), false)).toBe('Last played');
    expect(sessionLine('local', session(), false)).toBeNull();
    expect(sessionLine('remote', null, false)).toBeNull();
  });

  it('titles the superseded toast', () => {
    expect(nowPlayingElsewhere('Safari on iPhone', false)).toBe('Now playing on Safari on iPhone');
    expect(nowPlayingElsewhere('Safari on Mac', true)).toBe('Now playing in another tab');
    expect(nowPlayingElsewhere(null, false)).toBe('Now playing on another device');
  });
});

describe('resolveAdoptQueue', () => {
  const library = new Map([
    [120, 'a'],
    [123, 'b'],
    [131, 'c']
  ]);
  const resolve = (id: number) => library.get(id) ?? null;

  it('resolves the queue at the session song', () => {
    expect(resolveAdoptQueue(session(), resolve, () => null)).toEqual({
      queue: ['a', 'b', 'c'],
      index: 1
    });
  });

  it('skips ids this library does not hold, keeping the index on the song', () => {
    const s = session({ queue: [999, 120, 998, 123, 131], queueIndex: 3 });
    expect(resolveAdoptQueue(s, resolve, () => null)).toEqual({ queue: ['a', 'b', 'c'], index: 1 });
  });

  it('refuses when the song itself is not here and nothing stands in', () => {
    const s = session({ songId: 777, queue: [120, 777], queueIndex: 1 });
    expect(resolveAdoptQueue(s, resolve, () => null)).toBeNull();
  });

  it('lets the fallback stand in for the song (rows not loaded yet)', () => {
    const s = session({ songId: 777, queue: [120, 777, 131], queueIndex: 1 });
    expect(resolveAdoptQueue(s, resolve, () => 'hint')).toEqual({
      queue: ['a', 'hint', 'c'],
      index: 1
    });
  });

  it('keeps every id under a stand-in while the rows have not arrived', () => {
    const s = session({ songId: 777, queue: [120, 777, 888], queueIndex: 1 });
    const empty = () => null;
    expect(resolveAdoptQueue(s, empty, (id) => `stand-in ${id}`)).toEqual({
      queue: ['stand-in 120', 'stand-in 777', 'stand-in 888'],
      index: 1
    });
  });

  it("trusts the session's song id over the queue entry at its index", () => {
    const s = session({ songId: 131, queue: [120, 123], queueIndex: 1 });
    expect(resolveAdoptQueue(s, resolve, () => null)).toEqual({ queue: ['a', 'c'], index: 1 });
  });
});

describe('adoptStart', () => {
  it('starts where the session is', () => {
    expect(adoptStart(1, 3, 42_000, 'none')).toEqual({ index: 1, positionMs: 42_000 });
  });

  it('next moves one on from the top, or stays at the end', () => {
    expect(adoptStart(1, 3, 42_000, 'next')).toEqual({ index: 2, positionMs: 0 });
    expect(adoptStart(2, 3, 42_000, 'next')).toEqual({ index: 2, positionMs: 42_000 });
  });

  it('previous restarts past 3 s, else steps back (restarting the first item)', () => {
    expect(adoptStart(1, 3, 42_000, 'previous')).toEqual({ index: 1, positionMs: 0 });
    expect(adoptStart(1, 3, 1_000, 'previous')).toEqual({ index: 0, positionMs: 0 });
    expect(adoptStart(0, 3, 1_000, 'previous')).toEqual({ index: 0, positionMs: 0 });
  });
});
