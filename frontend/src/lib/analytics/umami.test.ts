import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const env = vi.hoisted(() => ({}) as Record<string, string | undefined>);
vi.mock('$env/dynamic/public', () => ({ env }));

import { trackUmamiEvent, umamiEnabled } from './umami';

type Win = { umami?: { track: ReturnType<typeof vi.fn> } };

describe('trackUmamiEvent', () => {
  let win: Win;

  beforeEach(() => {
    vi.useFakeTimers();
    win = {};
    vi.stubGlobal('window', win);
    env.PUBLIC_UMAMI_SRC = 'https://umami.example/script.js';
    env.PUBLIC_UMAMI_WEBSITE_ID = 'site';
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('sends straight away when the tracker is loaded', () => {
    win.umami = { track: vi.fn() };
    trackUmamiEvent('share-open', { scope: 'song' });
    expect(win.umami.track).toHaveBeenCalledExactlyOnceWith('share-open', { scope: 'song' });
  });

  it('waits for a deferred tracker, then sends once', () => {
    trackUmamiEvent('share-play', { title: 'Night Drive' });
    const track = vi.fn();
    vi.advanceTimersByTime(600);
    win.umami = { track };
    vi.advanceTimersByTime(2_000);
    expect(track).toHaveBeenCalledExactlyOnceWith('share-play', { title: 'Night Drive' });
  });

  it('gives up on a tracker that never loads', () => {
    trackUmamiEvent('share-open');
    vi.advanceTimersByTime(60_000);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('does nothing where analytics is not configured', () => {
    env.PUBLIC_UMAMI_WEBSITE_ID = '';
    expect(umamiEnabled()).toBe(false);
    win.umami = { track: vi.fn() };
    trackUmamiEvent('share-open');
    vi.advanceTimersByTime(60_000);
    expect(win.umami.track).not.toHaveBeenCalled();
  });

  it('swallows a tracker that throws or rejects', () => {
    win.umami = {
      track: vi.fn(() => {
        throw new Error('nope');
      })
    };
    expect(() => trackUmamiEvent('share-open')).not.toThrow();
    win.umami = { track: vi.fn(() => Promise.reject(new Error('nope'))) };
    expect(() => trackUmamiEvent('share-open')).not.toThrow();
  });
});
