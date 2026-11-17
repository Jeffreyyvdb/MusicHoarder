import { describe, expect, it } from 'vitest';
import { slideSpec } from './track-slide';

const motion = { reduced: false };

describe('slideSpec', () => {
  it('moves a skipped song left and brings the next one in from the right', () => {
    expect(slideSpec('out', 'next', motion)).toEqual({ from: 0, to: -24, duration: 160 });
    expect(slideSpec('in', 'next', motion)).toEqual({ from: 24, to: 0, duration: 240 });
  });

  it('mirrors it for previous', () => {
    expect(slideSpec('out', 'previous', motion)).toEqual({ from: 0, to: 24, duration: 160 });
    expect(slideSpec('in', 'previous', motion)).toEqual({ from: -24, to: 0, duration: 240 });
  });

  it('lets the leaving song carry on from where the finger left it', () => {
    const width = 300;
    expect(slideSpec('out', 'next', { ...motion, fromX: -70, width })).toEqual({
      from: -70,
      to: -300,
      duration: 200
    });
    expect(slideSpec('out', 'previous', { ...motion, fromX: 90, width })).toEqual({
      from: 90,
      to: 300,
      duration: 200
    });
  });

  it('only crossfades for a swap', () => {
    expect(slideSpec('out', 'swap', motion).from).toBeNull();
    expect(slideSpec('in', 'swap', motion).from).toBeNull();
  });

  it('drops the travel under Reduce Motion, a swiped copy included', () => {
    const reduced = { reduced: true, fromX: -70, width: 300 };
    expect(slideSpec('out', 'next', reduced).from).toBeNull();
    expect(slideSpec('in', 'next', reduced).from).toBeNull();
  });
});
