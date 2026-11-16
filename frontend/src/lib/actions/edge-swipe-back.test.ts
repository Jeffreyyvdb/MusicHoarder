import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { navBack } from '#lib/stores/nav-back.svelte.ts';
import { edgeSwipeBack } from './edge-swipe-back';

/**
 * Node has no DOM, so the content root is an EventTarget with a style bag, and touch events are
 * plain Events carrying `touches` / `changedTouches`. The point is the arming rules and the
 * release decision, which are what make the gesture safe to put on every page.
 */

vi.mock('#lib/navigation/back-preview.ts', () => ({
  backPreviews: { get: () => null },
  copyBackPreview: () => ({ element: new FakeElement() }),
  mountBackPreview: (preview: { element: FakeElement }) => preview.element
}));

const WIDTH = 393;
const REST = {
  transform: '',
  transition: '',
  opacity: '',
  boxShadow: '',
  background: '',
  position: '',
  zIndex: ''
};

class FakeElement extends EventTarget {
  style: Record<string, string> = { ...REST };
  clientWidth = WIDTH;
  getBoundingClientRect() {
    return { width: WIDTH };
  }
  removed = false;
  innerHTML = '';
  className = '';
  setAttribute() {}
  remove() {
    this.removed = true;
  }
}

let node: FakeElement;
let created: FakeElement[];
let reduced: boolean;
let openModal: boolean;
let installed: boolean;
let clock: number;

beforeEach(() => {
  vi.useFakeTimers();
  node = new FakeElement();
  created = [];
  reduced = false;
  openModal = false;
  installed = true;
  clock = 0;
  vi.stubGlobal('window', {
    innerWidth: WIDTH,
    innerHeight: 852,
    matchMedia: (q: string) => ({
      matches:
        (q.includes('display-mode') && installed) || (q.includes('reduced-motion') && reduced)
    })
  });
  vi.stubGlobal('getComputedStyle', () => ({ backgroundColor: 'white' }));
  vi.stubGlobal('navigator', { vibrate: vi.fn() });
  vi.stubGlobal('document', {
    querySelector: () => (openModal ? {} : null),
    createElement: () => {
      const el = new FakeElement();
      created.push(el);
      return el;
    },
    body: { appendChild: () => {} }
  });
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

function touchEvent(
  type: string,
  x: number,
  y: number,
  opts: { dt?: number; target?: { closest: (s: string) => unknown } } = {}
): Event {
  clock += opts.dt ?? 16;
  const touch = { identifier: 7, clientX: x, clientY: y };
  const e = new Event(type, { cancelable: true });
  Object.defineProperty(e, 'timeStamp', { value: clock });
  Object.defineProperty(e, 'target', { value: opts.target ?? { closest: () => null } });
  return Object.assign(e, {
    touches: type === 'touchend' || type === 'touchcancel' ? [] : [touch],
    changedTouches: [touch]
  });
}

/** A swipe from (x0, 300) through `xs`, each step `dt` ms apart, then a lift. */
function swipe(x0: number, xs: number[], dt = 16, end = 'touchend') {
  node.dispatchEvent(touchEvent('touchstart', x0, 300, { dt: 0 }));
  const moves = xs.map((x) => {
    const e = touchEvent('touchmove', x, 300, { dt });
    node.dispatchEvent(e);
    return e;
  });
  node.dispatchEvent(touchEvent(end, xs.at(-1) ?? x0, 300, { dt }));
  return moves;
}

function withBack() {
  const back = vi.fn();
  const clear = navBack.set(back);
  return { back, clear };
}

function attach() {
  return edgeSwipeBack(node as unknown as HTMLElement);
}

describe('edgeSwipeBack', () => {
  it('slides an outgoing copy offscreen and waits for both motion and navigation', async () => {
    let landed = () => {};
    const back = vi.fn(() => new Promise<void>((resolve) => (landed = resolve)));
    const clear = navBack.set(back);
    const action = attach();
    const moves = swipe(5, [20, 60, 100, 140]);
    expect(moves.every((e) => e.defaultPrevented)).toBe(true);
    expect(back).toHaveBeenCalledOnce();
    expect(node.style.opacity).toBe('0');
    landed();
    await vi.advanceTimersByTimeAsync(100);
    expect(node.style.opacity).toBe('0');
    await vi.advanceTimersByTimeAsync(120);
    expect(node.style).toEqual(REST);
    // No floating chevron is created.
    expect(created).toHaveLength(0);
    action.destroy();
    clear();
  });

  it('holds until a delayed Back lands and refuses a second Back while committing', async () => {
    let landed = () => {};
    const back = vi.fn(() => new Promise<void>((resolve) => (landed = resolve)));
    const clear = navBack.set(back);
    const action = attach();
    swipe(5, [20, 60, 100, 140]);
    await vi.advanceTimersByTimeAsync(300);
    expect(node.style.opacity).toBe('0');
    swipe(5, [20, 60, 100, 140]);
    expect(back).toHaveBeenCalledOnce();
    landed();
    await vi.advanceTimersByTimeAsync(0);
    expect(node.style).toEqual(REST);
    action.destroy();
    clear();
  });

  it('puts the page back even when the Back never lands', async () => {
    const back = vi.fn(() => new Promise<void>(() => {}));
    const clear = navBack.set(back);
    const action = attach();
    swipe(5, [20, 60, 100, 140]);
    await vi.advanceTimersByTimeAsync(1200);
    expect(node.style).toEqual(REST);
    action.destroy();
    clear();
  });

  it('can interrupt a cancelled swipe with another drag', () => {
    const { back, clear } = withBack();
    const action = attach();
    swipe(5, [20, 30, 40, 50], 120);
    node.dispatchEvent(touchEvent('touchstart', 5, 300));
    node.dispatchEvent(touchEvent('touchmove', 95, 300));
    expect(node.style.transform).toBe('translate3d(90px, 0, 0)');
    expect(back).not.toHaveBeenCalled();
    action.destroy();
    expect(node.style).toEqual(REST);
    clear();
  });

  it('cancels when the finger reverses direction, even past the distance threshold', () => {
    const { back, clear } = withBack();
    const action = attach();
    swipe(5, [100, 240, 220, 190]);
    expect(back).not.toHaveBeenCalled();
    action.destroy();
    clear();
  });

  it('uses opacity feedback with Reduce Motion, without sliding pages', async () => {
    reduced = true;
    const { back, clear } = withBack();
    const action = attach();
    node.dispatchEvent(touchEvent('touchstart', 5, 300));
    node.dispatchEvent(touchEvent('touchmove', 140, 300));
    expect(node.style.transform).toBe('');
    expect(Number(node.style.opacity)).toBeLessThan(1);
    node.dispatchEvent(touchEvent('touchend', 140, 300));
    await vi.advanceTimersByTimeAsync(0);
    expect(back).toHaveBeenCalledOnce();
    expect(node.style).toEqual(REST);
    action.destroy();
    clear();
  });

  it('follows the finger one-to-one', () => {
    const { clear } = withBack();
    const action = attach();
    node.dispatchEvent(touchEvent('touchstart', 5, 300, { dt: 0 }));
    node.dispatchEvent(touchEvent('touchmove', 20, 300));
    node.dispatchEvent(touchEvent('touchmove', 95, 300));
    expect(node.style.transform).toBe('translate3d(90px, 0, 0)');
    expect(node.style.transition).toBe('none');
    action.destroy();
    clear();
  });

  it('springs back from a short, slow swipe', () => {
    const { back, clear } = withBack();
    const action = attach();
    swipe(5, [20, 30, 40, 50], 120);
    expect(back).not.toHaveBeenCalled();
    expect(node.style.transform).toBe('');
    vi.advanceTimersByTime(300);
    // No lingering transform: it would make the content root a containing block.
    expect(node.style.transform).toBe('');
    action.destroy();
    clear();
  });

  it('goes back on a flick, however short', () => {
    const { back, clear } = withBack();
    const action = attach();
    swipe(5, [20, 40, 70], 16);
    expect(back).toHaveBeenCalledOnce();
    action.destroy();
    clear();
  });

  it('springs back when the browser cancels the touch', () => {
    const { back, clear } = withBack();
    const action = attach();
    swipe(5, [20, 60, 140, 200], 16, 'touchcancel');
    expect(back).not.toHaveBeenCalled();
    action.destroy();
    clear();
  });

  it('leaves a vertical move to the scroller', () => {
    const { back, clear } = withBack();
    const action = attach();
    node.dispatchEvent(touchEvent('touchstart', 5, 300, { dt: 0 }));
    const down = touchEvent('touchmove', 8, 330);
    node.dispatchEvent(down);
    node.dispatchEvent(touchEvent('touchmove', 150, 340));
    node.dispatchEvent(touchEvent('touchend', 150, 340));
    expect(down.defaultPrevented).toBe(false);
    expect(back).not.toHaveBeenCalled();
    expect(created).toHaveLength(0);
    action.destroy();
    clear();
  });

  it('only arms from the leading edge', () => {
    const { back, clear } = withBack();
    const action = attach();
    swipe(30, [60, 120, 200]);
    expect(back).not.toHaveBeenCalled();
    action.destroy();
    clear();
  });

  it('stays out of horizontal scrollers', () => {
    const { back, clear } = withBack();
    const action = attach();
    const target = { closest: (s: string) => (s === '[data-scroll-x]' ? {} : null) };
    node.dispatchEvent(touchEvent('touchstart', 5, 300, { dt: 0, target }));
    node.dispatchEvent(touchEvent('touchmove', 200, 300));
    node.dispatchEvent(touchEvent('touchend', 200, 300));
    expect(back).not.toHaveBeenCalled();
    action.destroy();
    clear();
  });

  it('does nothing without a Back action, in a browser tab, over a dialog, or when disabled', () => {
    const action = attach();
    swipe(5, [60, 140, 200]);
    expect(created).toHaveLength(0);

    const { back, clear } = withBack();
    installed = false;
    swipe(5, [60, 140, 200]);
    installed = true;
    openModal = true;
    swipe(5, [60, 140, 200]);
    openModal = false;
    action.update({ enabled: false });
    swipe(5, [60, 140, 200]);
    expect(back).not.toHaveBeenCalled();

    action.update({ enabled: true });
    swipe(5, [60, 140, 200]);
    expect(back).toHaveBeenCalledOnce();
    action.destroy();
    clear();
  });
});
