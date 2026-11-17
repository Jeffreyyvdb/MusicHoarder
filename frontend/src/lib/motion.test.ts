import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  EASE_IN_OUT,
  EASE_OUT,
  easingOf,
  motionFly,
  prefersReducedMotion
} from './motion';

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

describe('easingOf', () => {
  it('pins progress at both ends', () => {
    const ease = easingOf(EASE_OUT);
    expect(ease(0)).toBe(0);
    expect(ease(1)).toBe(1);
  });

  it.each([
    ['cubic-bezier(0.25, 0.1, 0.25, 1)', 0.5, 0.8024],
    [EASE_OUT, 0.1, 0.3981],
    [EASE_OUT, 0.5, 0.966],
    [EASE_IN_OUT, 0.25, 0.0529],
    [EASE_IN_OUT, 0.5, 0.596],
    ['cubic-bezier(0, 0, 1, 1)', 0.3, 0.3]
  ])('%s at %s is %s', (curve, t, expected) => {
    expect(easingOf(curve)(t)).toBeCloseTo(expected, 3);
  });

  it('rejects anything that is not a cubic-bezier', () => {
    expect(() => easingOf('ease-out')).toThrow();
  });
});
