import { afterEach, describe, expect, it, vi } from 'vitest';

const env = vi.hoisted(() => ({
  PUBLIC_LOOPING_URL: 'https://inbox.example' as string | undefined
}));
vi.mock('$app/env/public', () => env);

import { installLooping, looping, parseLoopingCommand } from './looping.svelte.ts';

describe('installLooping', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('adopts a widget that loads after the app on a first ?looping= visit', () => {
    // No key stored yet: the widget stores it when its script runs, after the app has mounted.
    vi.stubGlobal('localStorage', { getItem: () => null });
    const win = new EventTarget() as EventTarget & { looping?: unknown };
    vi.stubGlobal('window', win);

    const cleanup = installLooping();
    expect(looping.active).toBe(false);

    win.looping = { enabled: true, setContext: () => {}, open: () => {} };
    win.dispatchEvent(new Event('looping:ready'));
    expect(looping.active).toBe(true);
    cleanup();
  });
});

describe('parseLoopingCommand', () => {
  afterEach(() => {
    env.PUBLIC_LOOPING_URL = 'https://inbox.example';
  });

  it('reads a key in either spelling', () => {
    expect(parseLoopingCommand('looping:abcDEF123_-')).toEqual({ kind: 'on', key: 'abcDEF123_-' });
    expect(parseLoopingCommand('  Looping abcDEF123  ')).toEqual({ kind: 'on', key: 'abcDEF123' });
  });

  it('reads off', () => {
    expect(parseLoopingCommand('looping:off')).toEqual({ kind: 'off' });
    expect(parseLoopingCommand('looping OFF')).toEqual({ kind: 'off' });
  });

  it('ignores ordinary searches and typos', () => {
    expect(parseLoopingCommand('looping')).toBeNull();
    expect(parseLoopingCommand('looping louie')).toBeNull(); // too short to be a key
    expect(parseLoopingCommand('looping in the air')).toBeNull();
    expect(parseLoopingCommand('the looping song')).toBeNull();
  });

  it('does nothing on an instance without an inbox', () => {
    env.PUBLIC_LOOPING_URL = undefined;
    expect(parseLoopingCommand('looping:abcDEF123')).toBeNull();
  });
});
