import { describe, expect, it } from 'vitest';
import {
  DIM_MAX,
  DIM_MIN,
  DIM_TARGET_GREY,
  DIM_UNKNOWN,
  brightestGrey,
  dimAlphaForGrey,
  equivalentGrey
} from './cover-dim';

// WCAG contrast of `fg` at `alpha` over an sRGB background, composited in sRGB like the browser.
function luminance([r, g, b]: number[]): number {
  const lin = (c: number) => {
    const s = c / 255;
    return s <= 0.04045 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
}
function contrastOfWhite(alpha: number, bg: number[]): number {
  const fg = bg.map((c) => c + alpha * (255 - c));
  const [hi, lo] = [luminance(fg), luminance(bg)].sort((a, b) => b - a);
  return (hi + 0.05) / (lo + 0.05);
}
const dimmed = (rgb: number[], a: number) => rgb.map((c) => c * (1 - a));

// An 8×8 RGBA sample filled from a function of the cell.
function sample(fill: (x: number, y: number) => number[]): number[] {
  const data: number[] = [];
  for (let y = 0; y < 8; y++) for (let x = 0; x < 8; x++) data.push(...fill(x, y), 255);
  return data;
}

describe('dimAlphaForGrey', () => {
  it('brings the brightest cell down to the target grey', () => {
    // 1 − 70/140 = 0.5, and 140 × (1 − 0.5) = 70.
    expect(dimAlphaForGrey(140)).toBeCloseTo(0.5);
  });

  it('keeps a floor for dark covers and a ceiling for white ones', () => {
    expect(dimAlphaForGrey(0)).toBe(DIM_MIN);
    expect(dimAlphaForGrey(60)).toBe(DIM_MIN);
    // A white cover lands just under the cap: 1 − 70/255 ≈ 0.73.
    expect(dimAlphaForGrey(255)).toBeCloseTo(0.7255, 3);
    expect(dimAlphaForGrey(255)).toBeLessThanOrEqual(DIM_MAX);
  });

  it('assumes the worst when the cover could not be measured', () => {
    expect(dimAlphaForGrey(null)).toBe(DIM_UNKNOWN);
    expect(dimAlphaForGrey(Number.NaN)).toBe(DIM_UNKNOWN);
    // The unknown dim holds even over white.
    expect(255 * (1 - DIM_UNKNOWN)).toBeLessThanOrEqual(DIM_TARGET_GREY);
  });
});

describe('equivalentGrey', () => {
  it('measures colour by luminance, not luma', () => {
    expect(equivalentGrey(255, 255, 255)).toBeCloseTo(255);
    expect(equivalentGrey(0, 0, 0)).toBeCloseTo(0);
    expect(equivalentGrey(128, 128, 128)).toBeCloseTo(128, 0);
    // Pure green is as bright as a ~220 grey (its Rec. 601 luma says 150).
    expect(equivalentGrey(0, 255, 0)).toBeGreaterThan(215);
    expect(equivalentGrey(0, 0, 255)).toBeLessThan(80);
  });
});

describe('brightestGrey', () => {
  it('takes the brightest pixel, compositing translucency over black', () => {
    const white = [255, 255, 255, 255];
    const black = [0, 0, 0, 255];
    const clearWhite = [255, 255, 255, 0];
    expect(brightestGrey([...black, ...white])).toBeCloseTo(255);
    expect(brightestGrey([...black, ...clearWhite])).toBeCloseTo(0);
    expect(brightestGrey([])).toBeNull();
  });
});

describe('the rule, end to end', () => {
  it('keeps every tone legible over the bright half of a half-white, half-black cover', () => {
    const data = sample((x) => (x < 4 ? [255, 255, 255] : [0, 0, 0]));
    const a = dimAlphaForGrey(brightestGrey(data));
    const brightest = dimmed([255, 255, 255], a);
    expect(brightest[0]).toBeLessThanOrEqual(DIM_TARGET_GREY + 0.5);
    expect(contrastOfWhite(1, brightest)).toBeGreaterThanOrEqual(9);
    expect(contrastOfWhite(0.72, brightest)).toBeGreaterThanOrEqual(4.5);
    expect(contrastOfWhite(0.55, brightest)).toBeGreaterThanOrEqual(3);
  });

  it('dims for a pale disc even when the cover is dark on average', () => {
    // A magenta field with a pale green disc in the middle: the average grey is low, the disc not.
    const magenta = [150, 20, 120];
    const paleGreen = [180, 235, 160];
    const data = sample((x, y) => ((x - 3.5) ** 2 + (y - 3.5) ** 2 < 5 ? paleGreen : magenta));
    const a = dimAlphaForGrey(brightestGrey(data));
    for (const cell of [magenta, paleGreen]) {
      const bg = dimmed(cell, a);
      expect(contrastOfWhite(0.72, bg)).toBeGreaterThanOrEqual(4.5);
      expect(contrastOfWhite(0.55, bg)).toBeGreaterThanOrEqual(3);
    }
  });
});
