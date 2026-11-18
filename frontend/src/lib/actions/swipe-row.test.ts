import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { swipeRow, type SwipeRowParams } from './swipe-row';

/**
 * Node has no DOM, so the row is an EventTarget with a style bag and one child, and touch events
 * are plain Events carrying `touches` / `changedTouches`. A touchmove left un-prevented is one
 * iOS may turn into a scroll, which is what ended a swipe halfway before.
 */

const WIDTH = 400;

class FakeElement extends EventTarget {
  style: Record<string, string> = {};
  dataset: Record<string, string> = {};
  children: FakeElement[] = [];
  innerHTML = '';
  setAttribute() {}
  append(child: FakeElement) {
    this.children.push(child);
  }
  prepend(child: FakeElement) {
    this.children.unshift(child);
  }
  getBoundingClientRect() {
    return { width: WIDTH };
  }
}

let row: FakeElement;
let label: FakeElement;
let onswipe: ReturnType<typeof vi.fn<() => void>>;

beforeEach(() => {
  vi.useFakeTimers();
  vi.stubGlobal('HTMLElement', FakeElement);
  vi.stubGlobal('window', { matchMedia: () => ({ matches: false }) });
  vi.stubGlobal('getComputedStyle', () => ({ position: 'relative' }));
  vi.stubGlobal('navigator', { vibrate: vi.fn() });
  vi.stubGlobal('document', { createElement: () => new FakeElement() });
  row = new FakeElement();
  label = new FakeElement();
  row.append(label);
  onswipe = vi.fn<() => void>();
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

function attach(params: Partial<SwipeRowParams> = {}) {
  return swipeRow(row as unknown as HTMLElement, {
    side: 'leading',
    tone: 'primary',
    icon: '<svg></svg>',
    onswipe,
    ...params
  });
}

function touch(type: string, x: number, y: number, fingers = 1): Event {
  const t = { identifier: 3, clientX: x, clientY: y };
  const e = new Event(type, { cancelable: true });
  const lifted = type === 'touchend' || type === 'touchcancel';
  return Object.assign(e, {
    touches: lifted ? [] : Array.from({ length: fingers }, () => t),
    changedTouches: [t]
  });
}

/** Down at (x0, y0), through each point, then `end` at the last one. */
function drag(x0: number, y0: number, points: [number, number][], end = 'touchend') {
  row.dispatchEvent(touch('touchstart', x0, y0));
  const moves = points.map(([x, y]) => {
    const e = touch('touchmove', x, y);
    row.dispatchEvent(e);
    return e;
  });
  const [x, y] = points.at(-1) ?? [x0, y0];
  row.dispatchEvent(touch(end, x, y));
  return moves;
}

describe('swipeRow', () => {
  it('adds the song from a swipe that drifts down, and keeps the list from scrolling it', () => {
    attach();
    const moves = drag(100, 300, [
      [106, 301],
      [130, 306],
      [170, 314],
      [215, 322]
    ]);
    expect(moves.every((e) => e.defaultPrevented)).toBe(true);
    expect(onswipe).toHaveBeenCalledOnce();
  });

  it('leaves a vertical drag to the list', () => {
    attach();
    const moves = drag(100, 300, [
      [101, 306],
      [103, 330],
      [104, 400]
    ]);
    expect(moves.some((e) => e.defaultPrevented)).toBe(false);
    expect(label.style.transform ?? '').toBe('');
    expect(onswipe).not.toHaveBeenCalled();
  });

  it('springs back without the action when the swipe stops short or is cancelled', () => {
    attach();
    row.dispatchEvent(touch('touchstart', 100, 300));
    row.dispatchEvent(touch('touchmove', 120, 300));
    row.dispatchEvent(touch('touchmove', 160, 300));
    expect(label.style.transform).toBe('translateX(60px)');
    row.dispatchEvent(touch('touchend', 160, 300));
    vi.runAllTimers();
    expect(label.style.transform).toBe('');

    drag(
      100,
      300,
      [
        [140, 300],
        [260, 300]
      ],
      'touchcancel'
    );
    vi.runAllTimers();
    expect(onswipe).not.toHaveBeenCalled();
    expect(label.style.transform).toBe('');
  });

  it('gives up when a second finger lands', () => {
    attach();
    row.dispatchEvent(touch('touchstart', 100, 300));
    row.dispatchEvent(touch('touchmove', 160, 300));
    row.dispatchEvent(touch('touchstart', 200, 300, 2));
    row.dispatchEvent(touch('touchmove', 260, 300));
    row.dispatchEvent(touch('touchend', 260, 300));
    vi.runAllTimers();
    expect(onswipe).not.toHaveBeenCalled();
    expect(label.style.transform).toBe('');
  });

  it('leaves a touch at the left edge to swipe back', () => {
    attach();
    const moves = drag(10, 300, [
      [40, 300],
      [200, 300]
    ]);
    expect(moves.some((e) => e.defaultPrevented)).toBe(false);
    expect(onswipe).not.toHaveBeenCalled();
  });

  it('puts a row back at rest when it is disabled mid-swipe', () => {
    const action = attach({ side: 'trailing', tone: 'destructive' });
    row.dispatchEvent(touch('touchstart', 300, 300));
    row.dispatchEvent(touch('touchmove', 250, 300));
    expect(label.style.transform).toBe('translateX(-50px)');
    action.update({ side: 'trailing', tone: 'destructive', icon: '', onswipe, disabled: true });
    vi.runAllTimers();
    expect(label.style.transform).toBe('');
    row.dispatchEvent(touch('touchend', 100, 300));
    expect(onswipe).not.toHaveBeenCalled();
  });

  it('swallows the click a swipe lift delivers, but not a later tap', () => {
    attach();
    const played = vi.fn();
    row.addEventListener('click', played);
    drag(100, 300, [[220, 300]]);
    row.dispatchEvent(new Event('click', { cancelable: true }));
    expect(played).not.toHaveBeenCalled();
    vi.runAllTimers();
    row.dispatchEvent(new Event('click', { cancelable: true }));
    expect(played).toHaveBeenCalledOnce();
  });
});
