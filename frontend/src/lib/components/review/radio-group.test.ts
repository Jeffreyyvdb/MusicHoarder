import { describe, expect, it } from 'vitest';
import { radioStep, radioTabIndex } from './radio-group';

describe('radioStep', () => {
  it('moves forward and back, wrapping at the ends like a native radio group', () => {
    expect(radioStep('ArrowDown', 0, 3)).toBe(1);
    expect(radioStep('ArrowRight', 2, 3)).toBe(0);
    expect(radioStep('ArrowUp', 0, 3)).toBe(2);
    expect(radioStep('ArrowLeft', 1, 3)).toBe(0);
  });

  it('jumps to the ends with Home and End', () => {
    expect(radioStep('Home', 2, 5)).toBe(0);
    expect(radioStep('End', 0, 5)).toBe(4);
  });

  it('leaves every other key alone, and an empty group', () => {
    expect(radioStep('Enter', 0, 3)).toBeNull();
    expect(radioStep('a', 0, 3)).toBeNull();
    expect(radioStep('ArrowDown', 0, 0)).toBeNull();
  });
});

describe('radioTabIndex', () => {
  it('makes the checked option the one Tab stop', () => {
    expect([0, 1, 2].map((i) => radioTabIndex(i, 1))).toEqual([-1, 0, -1]);
  });

  it('falls back to the first option when nothing is checked', () => {
    expect([0, 1, 2].map((i) => radioTabIndex(i, -1))).toEqual([0, -1, -1]);
  });
});
