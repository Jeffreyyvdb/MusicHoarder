import { describe, expect, it } from 'vitest';
import { albumTint, washFromPixels } from './album-tint';

function pixels(...rgba: [number, number, number, number][]): number[] {
  return rgba.flat();
}

function parse(oklch: string): { l: number; c: number; h: number } {
  const [l, c, h] = oklch.slice('oklch('.length, -1).split(' ').map(Number);
  return { l, c, h };
}

describe('albumTint', () => {
  it('is deterministic per album', () => {
    expect(albumTint('Aurora Vale', 'Glass Horizons')).toEqual(
      albumTint(' aurora vale ', 'GLASS HORIZONS')
    );
  });
});

describe('washFromPixels', () => {
  it('takes the hue of the artwork', () => {
    // A saturated purple record: the wash is purple (OKLCH hue ~300–330), not a hashed blue.
    const { h } = parse(washFromPixels(pixels([128, 20, 160, 255], [140, 30, 170, 255]))!);
    expect(h).toBeGreaterThan(290);
    expect(h).toBeLessThan(335);
  });

  it('keeps lightness in the dark range, however bright or dark the cover is', () => {
    expect(parse(washFromPixels(pixels([250, 250, 240, 255]))!).l).toBeCloseTo(0.45, 3);
    expect(parse(washFromPixels(pixels([2, 2, 4, 255]))!).l).toBeCloseTo(0.3, 3);
  });

  it('never invents colour for a grey record, and caps a loud one', () => {
    expect(parse(washFromPixels(pixels([120, 120, 120, 255]))!).c).toBeLessThan(0.01);
    expect(parse(washFromPixels(pixels([255, 0, 0, 255]))!).c).toBeLessThanOrEqual(0.18);
  });

  it('skips transparent pixels, and gives nothing when nothing is opaque', () => {
    const withHole = washFromPixels(pixels([0, 0, 255, 255], [255, 0, 0, 0]));
    expect(withHole).toBe(washFromPixels(pixels([0, 0, 255, 255])));
    expect(washFromPixels(pixels([255, 0, 0, 0]))).toBeNull();
  });
});
