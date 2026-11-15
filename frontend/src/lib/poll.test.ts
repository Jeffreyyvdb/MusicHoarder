import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { poll } from './poll';

/**
 * A poll must go quiet while the page is hidden (a background tab, the installed app in the
 * background), catch up once when it is shown again, and stop for good when its owner unmounts.
 */

type FakeDocument = EventTarget & { visibilityState: 'visible' | 'hidden' };

let doc: FakeDocument;

function setVisibility(state: 'visible' | 'hidden') {
  doc.visibilityState = state;
  doc.dispatchEvent(new Event('visibilitychange'));
}

beforeEach(() => {
  vi.useFakeTimers();
  doc = Object.assign(new EventTarget(), { visibilityState: 'visible' as const });
  vi.stubGlobal('document', doc);
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('poll', () => {
  it('calls on the interval while visible', () => {
    const fn = vi.fn();
    const stop = poll(fn, 1000);
    expect(fn).not.toHaveBeenCalled();
    vi.advanceTimersByTime(3000);
    expect(fn).toHaveBeenCalledTimes(3);
    stop();
  });

  it('calls once at the start when asked', () => {
    const fn = vi.fn();
    const stop = poll(fn, 1000, { immediate: true });
    expect(fn).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(1000);
    expect(fn).toHaveBeenCalledTimes(2);
    stop();
  });

  it('stays quiet while hidden and catches up once when shown', () => {
    const fn = vi.fn();
    const stop = poll(fn, 1000);
    vi.advanceTimersByTime(1000);
    expect(fn).toHaveBeenCalledTimes(1);

    setVisibility('hidden');
    vi.advanceTimersByTime(60_000);
    expect(fn).toHaveBeenCalledTimes(1);

    setVisibility('visible');
    expect(fn).toHaveBeenCalledTimes(2);
    vi.advanceTimersByTime(1000);
    expect(fn).toHaveBeenCalledTimes(3);
    stop();
  });

  it('does not start a timer when it begins hidden, but still makes the first call', () => {
    doc.visibilityState = 'hidden';
    const fn = vi.fn();
    const stop = poll(fn, 1000, { immediate: true });
    expect(fn).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(10_000);
    expect(fn).toHaveBeenCalledTimes(1);
    setVisibility('visible');
    expect(fn).toHaveBeenCalledTimes(2);
    stop();
  });

  it('a repeated visible event does not double the timer', () => {
    const fn = vi.fn();
    const stop = poll(fn, 1000);
    setVisibility('visible');
    setVisibility('visible');
    expect(fn).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1000);
    expect(fn).toHaveBeenCalledTimes(1);
    stop();
  });

  it('stops for good, whatever the page does afterwards', () => {
    const fn = vi.fn();
    const stop = poll(fn, 1000);
    stop();
    vi.advanceTimersByTime(5000);
    setVisibility('hidden');
    setVisibility('visible');
    vi.advanceTimersByTime(5000);
    expect(fn).not.toHaveBeenCalled();
  });

  it('is a no-op on the server', () => {
    vi.stubGlobal('document', undefined);
    const fn = vi.fn();
    const stop = poll(fn, 1000, { immediate: true });
    vi.advanceTimersByTime(5000);
    expect(fn).not.toHaveBeenCalled();
    stop();
  });
});
