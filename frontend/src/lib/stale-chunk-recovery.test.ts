import { describe, expect, it } from 'vitest';
import {
  BACKOFF_MS,
  DEBOUNCE_MS,
  EPISODE_WINDOW_MS,
  MAX_ATTEMPTS,
  isStaleChunkError,
  nextRecoveryStep,
  type RecoveryState
} from './stale-chunk-recovery';

describe('isStaleChunkError', () => {
  it('matches the browser phrasings for a failed dynamic import', () => {
    expect(isStaleChunkError(new Error('Failed to fetch dynamically imported module: /x.js'))).toBe(
      true
    );
    expect(isStaleChunkError(new Error('error loading dynamically imported module'))).toBe(true);
    // The exact Safari wording the user reported.
    expect(isStaleChunkError(new Error('Importing a module script failed. 500'))).toBe(true);
  });

  it('accepts strings and { message } shapes (e.g. unhandledrejection reasons)', () => {
    expect(isStaleChunkError('Importing a module script failed.')).toBe(true);
    expect(isStaleChunkError({ message: 'Failed to fetch dynamically imported module' })).toBe(true);
  });

  it('ignores unrelated and empty errors', () => {
    expect(isStaleChunkError(new Error('TypeError: undefined is not a function'))).toBe(false);
    expect(isStaleChunkError(null)).toBe(false);
    expect(isStaleChunkError(undefined)).toBe(false);
    expect(isStaleChunkError({})).toBe(false);
  });
});

describe('nextRecoveryStep', () => {
  const NOW = 1_000_000;

  it('reloads immediately on the first failure', () => {
    const step = nextRecoveryStep(null, NOW);
    expect(step).toEqual({
      type: 'reload',
      delayMs: 0,
      state: { attempts: 1, firstAt: NOW, lastAt: NOW }
    });
  });

  it('backs off on successive failures within an episode', () => {
    let state: RecoveryState = { attempts: 0, firstAt: 0, lastAt: 0 };
    const delays: number[] = [];
    let t = NOW;
    for (let i = 0; i < MAX_ATTEMPTS; i++) {
      t += DEBOUNCE_MS + 1; // beyond the debounce so each counts as a new failure
      const step = nextRecoveryStep(state, t);
      if (step.type !== 'reload') throw new Error(`expected reload, got ${step.type}`);
      delays.push(step.delayMs);
      state = step.state;
    }
    expect(delays).toEqual([...BACKOFF_MS]);
  });

  it('collapses a duplicate event for the same failure (debounce)', () => {
    const state: RecoveryState = { attempts: 1, firstAt: NOW, lastAt: NOW };
    expect(nextRecoveryStep(state, NOW + DEBOUNCE_MS - 1)).toEqual({ type: 'debounce' });
  });

  it('gives up after the attempt budget is exhausted within the episode', () => {
    const state: RecoveryState = { attempts: MAX_ATTEMPTS, firstAt: NOW, lastAt: NOW };
    expect(nextRecoveryStep(state, NOW + DEBOUNCE_MS + 1)).toEqual({ type: 'exhausted' });
  });

  it('starts a fresh episode when the previous burst has aged out', () => {
    const old: RecoveryState = { attempts: MAX_ATTEMPTS, firstAt: NOW, lastAt: NOW };
    const later = NOW + EPISODE_WINDOW_MS + 1;
    expect(nextRecoveryStep(old, later)).toEqual({
      type: 'reload',
      delayMs: 0,
      state: { attempts: 1, firstAt: later, lastAt: later }
    });
  });
});
