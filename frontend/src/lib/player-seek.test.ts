import { describe, expect, it } from 'vitest';
import { PREVIOUS_RESTART_AFTER_S, previousAction, seekTargetForKey } from './player-seek';

describe('seekTargetForKey', () => {
  it('maps the seek keys and clamps to the track', () => {
    expect(seekTargetForKey('ArrowRight', 10, 100)).toBe(15);
    expect(seekTargetForKey('ArrowLeft', 2, 100)).toBe(0);
    expect(seekTargetForKey('PageUp', 90, 100)).toBe(100);
    expect(seekTargetForKey('Home', 50, 100)).toBe(0);
    expect(seekTargetForKey('End', 50, 100)).toBe(100);
    expect(seekTargetForKey('a', 50, 100)).toBeNull();
  });
});

describe('previousAction', () => {
  it('restarts the track once it is past the threshold, wherever it sits in the queue', () => {
    expect(previousAction(PREVIOUS_RESTART_AFTER_S + 0.1, 3)).toBe('restart');
    expect(previousAction(120, 0)).toBe('restart');
  });

  it('steps back to the previous item near the start of a track', () => {
    expect(previousAction(0, 3)).toBe('previous');
    expect(previousAction(PREVIOUS_RESTART_AFTER_S, 1)).toBe('previous');
  });

  it('restarts the first item instead of doing nothing', () => {
    expect(previousAction(0, 0)).toBe('restart');
    expect(previousAction(1.5, 0)).toBe('restart');
  });

  it('does nothing when no track is loaded', () => {
    expect(previousAction(0, -1)).toBe('none');
    expect(previousAction(10, -1)).toBe('none');
  });
});
