import { describe, expect, it } from 'vitest';
import { indexAfterKey, indexAt, scrub } from './scrub.ts';

describe('indexAt', () => {
  it('divides the width into equal positions and clamps the edges', () => {
    expect(indexAt(100, 100, 300, 3)).toBe(0);
    expect(indexAt(199, 100, 300, 3)).toBe(0);
    expect(indexAt(200, 100, 300, 3)).toBe(1);
    expect(indexAt(399, 100, 300, 3)).toBe(2);
    expect(indexAt(50, 100, 300, 3)).toBe(0);
    expect(indexAt(900, 100, 300, 3)).toBe(2);
  });

  it('has no answer for a box with no width or nothing to select', () => {
    expect(indexAt(10, 0, 0, 3)).toBeNull();
    expect(indexAt(10, 0, 100, 0)).toBeNull();
  });
});

describe('indexAfterKey', () => {
  it('starts from the newest position when nothing is selected', () => {
    expect(indexAfterKey('ArrowLeft', null, 10)).toBe(9);
    expect(indexAfterKey('ArrowRight', null, 10)).toBe(9);
  });

  it('steps, clamps, jumps and clears', () => {
    expect(indexAfterKey('ArrowLeft', 3, 10)).toBe(2);
    expect(indexAfterKey('ArrowLeft', 0, 10)).toBe(0);
    expect(indexAfterKey('ArrowRight', 9, 10)).toBe(9);
    expect(indexAfterKey('Home', 5, 10)).toBe(0);
    expect(indexAfterKey('End', 5, 10)).toBe(9);
    expect(indexAfterKey('Escape', 5, 10)).toBeNull();
  });

  it('leaves other keys to the browser', () => {
    expect(indexAfterKey('Tab', 3, 10)).toBeUndefined();
    expect(indexAfterKey('ArrowLeft', null, 0)).toBeUndefined();
  });
});

describe('scrub', () => {
  it('claims horizontal drags for itself and gives the touch action back on destroy', () => {
    const node = {
      style: { touchAction: 'manipulation' },
      addEventListener: () => {},
      removeEventListener: () => {}
    } as unknown as HTMLElement;

    const action = scrub(node, { count: 3, index: null, onchange: () => {} });
    expect(node.style.touchAction).toBe('pan-y');

    action.destroy();
    expect(node.style.touchAction).toBe('manipulation');
  });
});
