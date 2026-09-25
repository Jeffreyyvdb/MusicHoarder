import { afterEach, describe, expect, it, vi } from 'vitest';
import { motionFly, prefersReducedMotion } from './motion';

afterEach(() => vi.unstubAllGlobals());

describe('prefersReducedMotion', () => {
  it('is false on the server', () => {
    expect(prefersReducedMotion()).toBe(false);
  });

  it('reads the media query in the browser', () => {
    const matchMedia = vi.fn((query: string) => ({ matches: query.includes('reduce') }));
    vi.stubGlobal('window', { matchMedia });
    expect(prefersReducedMotion()).toBe(true);
    expect(matchMedia).toHaveBeenCalledWith('(prefers-reduced-motion: reduce)');
  });
});

describe('motionFly', () => {
  it('keeps the travel when motion is allowed', () => {
    vi.stubGlobal('window', { matchMedia: () => ({ matches: false }) });
    expect(motionFly({ y: 32, duration: 220 })).toEqual({ y: 32, duration: 220 });
  });

  it('drops the duration under Reduce Motion', () => {
    vi.stubGlobal('window', { matchMedia: () => ({ matches: true }) });
    expect(motionFly({ y: 32, duration: 220 })).toEqual({ y: 32, duration: 0 });
  });
});
