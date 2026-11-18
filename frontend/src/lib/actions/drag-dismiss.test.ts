import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { dragDismiss, type DragDismissHandlers } from './drag-dismiss';

/**
 * Node has no DOM: the overlay is an EventTarget, a touched element is a plain object with
 * `closest` and a `parentElement` chain, and touch events are plain Events carrying `touches` /
 * `changedTouches`. What is pinned is which presses become a pull, which the page keeps (a scroll,
 * a row swipe, a slider), and what the release reports.
 */

type Fake = {
  closest: (selector: string) => Fake | null;
  parentElement: Fake | null;
  scrollTop: number;
  scrollHeight: number;
  clientHeight: number;
  overflowY: string;
  attrs: string[];
  setPointerCapture?: (id: number) => void;
};

function el(parent: Fake | null, init: Partial<Fake> = {}): Fake {
  const self: Fake = {
    parentElement: parent,
    scrollTop: 0,
    scrollHeight: 100,
    clientHeight: 100,
    overflowY: 'visible',
    attrs: [],
    ...init,
    closest(selector) {
      for (let e: Fake | null = self; e; e = e.parentElement) {
        if (e.attrs.some((a) => selector.includes(a))) return e;
      }
      return null;
    }
  };
  return self;
}

let node: EventTarget & Fake;
let clock: number;
let calls: string[];
let handlers: DragDismissHandlers;
let windowTarget: EventTarget;

beforeEach(() => {
  node = Object.assign(new EventTarget(), el(null));
  clock = 0;
  calls = [];
  handlers = {
    onstart: () => calls.push('start'),
    onmove: (offset) => calls.push(`move ${offset}`),
    onend: (offset, velocity) => calls.push(`end ${offset} ${velocity > 0.11 ? 'flick' : 'slow'}`),
    oncancel: () => calls.push('cancel')
  };
  windowTarget = new EventTarget();
  vi.stubGlobal('window', windowTarget);
  vi.stubGlobal('getComputedStyle', (e: Fake) => ({ overflowY: e.overflowY }));
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function touchEvent(
  type: string,
  x: number,
  y: number,
  opts: { dt?: number; target?: Fake; cancelable?: boolean } = {}
): Event {
  clock += opts.dt ?? 16;
  const touch = { identifier: 7, clientX: x, clientY: y };
  const e = new Event(type, { cancelable: opts.cancelable ?? true });
  Object.defineProperty(e, 'timeStamp', { value: clock });
  Object.defineProperty(e, 'target', { value: opts.target ?? el(node) });
  return Object.assign(e, {
    touches: type === 'touchend' || type === 'touchcancel' ? [] : [touch],
    changedTouches: [touch]
  });
}

/** A touch at (200, 300) on `target`, through each [dx, dy], each `dt` ms apart, then a lift. */
function pull(
  steps: [number, number][],
  opts: { target?: Fake; dt?: number; restBeforeLift?: number; end?: string } = {}
) {
  const target = opts.target ?? el(node);
  node.dispatchEvent(touchEvent('touchstart', 200, 300, { dt: 0, target }));
  const moves = steps.map(([dx, dy]) => {
    const e = touchEvent('touchmove', 200 + dx, 300 + dy, { dt: opts.dt ?? 16, target });
    node.dispatchEvent(e);
    return e;
  });
  const [dx, dy] = steps.at(-1) ?? [0, 0];
  node.dispatchEvent(
    touchEvent(opts.end ?? 'touchend', 200 + dx, 300 + dy, {
      dt: opts.restBeforeLift ?? opts.dt ?? 16,
      target
    })
  );
  return moves;
}

function attach() {
  return dragDismiss(node as unknown as HTMLElement, handlers);
}

describe('dragDismiss, touch', () => {
  it('pulls the overlay down from anywhere on it and reports the release', () => {
    attach();
    const moves = pull([
      [0, 4],
      [1, 40],
      [2, 120]
    ]);
    expect(calls).toEqual(['start', 'move 40', 'move 120', 'end 120 flick']);
    expect(moves.every((m) => m.defaultPrevented)).toBe(true);
  });

  it('leaves a tap alone', () => {
    attach();
    pull([[1, 3]]);
    expect(calls).toEqual([]);
  });

  it('reports a slow release as no flick, so the host can spring back', () => {
    attach();
    pull(
      [
        [0, 10],
        [0, 30]
      ],
      { restBeforeLift: 200 }
    );
    expect(calls.at(-1)).toBe('end 30 slow');
  });

  it('lets an upward or sideways move go, unprevented', () => {
    attach();
    const up = pull([
      [0, -10],
      [0, -60]
    ]);
    const sideways = pull([
      [12, 4],
      [80, 20]
    ]);
    expect(calls).toEqual([]);
    expect([...up, ...sideways].some((m) => m.defaultPrevented)).toBe(false);
  });

  it('lets a scroller that is scrolled down keep the whole touch', () => {
    attach();
    const list = el(node, { overflowY: 'auto', scrollHeight: 2000, scrollTop: 300 });
    const moves = pull(
      [
        [0, 10],
        [0, 150]
      ],
      { target: el(list) }
    );
    expect(calls).toEqual([]);
    expect(moves.some((m) => m.defaultPrevented)).toBe(false);
  });

  it('pulls from a scroller at its top, holding it still', () => {
    attach();
    const list = el(node, { overflowY: 'auto', scrollHeight: 2000, scrollTop: 0 });
    const moves = pull(
      [
        [0, 3],
        [0, 90]
      ],
      { target: el(list) }
    );
    expect(calls).toEqual(['start', 'move 90', 'end 90 flick']);
    expect(moves[0].defaultPrevented).toBe(true);
  });

  it('ignores an element whose overflow is hidden, however its scroll position reads', () => {
    attach();
    const clipped = el(node, { overflowY: 'hidden', scrollHeight: 2000, scrollTop: 300 });
    pull([[0, 60]], { target: el(clipped) });
    expect(calls[0]).toBe('start');
  });

  it.each([
    ['data-drag-ignore', 'a wide window, full-screen video, a queue handle'],
    ['role="slider"', 'the scrubber'],
    ['aria-haspopup', 'a menu trigger']
  ])('leaves a press on [%s] to its owner (%s)', (attr) => {
    attach();
    const owner = el(node, { attrs: [attr] });
    pull([[0, 80]], { target: el(owner) });
    expect(calls).toEqual([]);
  });

  it('lets go of a touch the browser is already scrolling', () => {
    attach();
    node.dispatchEvent(touchEvent('touchstart', 200, 300, { dt: 0 }));
    node.dispatchEvent(touchEvent('touchmove', 200, 340, { cancelable: false }));
    node.dispatchEvent(touchEvent('touchend', 200, 340));
    expect(calls).toEqual([]);
  });

  it('springs back when a second finger lands or the touch is cancelled', () => {
    attach();
    node.dispatchEvent(touchEvent('touchstart', 200, 300, { dt: 0 }));
    node.dispatchEvent(touchEvent('touchmove', 200, 360));
    const second = touchEvent('touchstart', 100, 300);
    Object.assign(second, { touches: [{}, {}] });
    node.dispatchEvent(second);
    node.dispatchEvent(touchEvent('touchmove', 200, 400));
    expect(calls).toEqual(['start', 'move 60', 'cancel']);

    calls = [];
    pull([[0, 60]], { end: 'touchcancel' });
    expect(calls).toEqual(['start', 'move 60', 'cancel']);
  });

  it('takes the next touch when the last one never reported its end', () => {
    attach();
    node.dispatchEvent(touchEvent('touchstart', 200, 300, { dt: 0 }));
    pull([[0, 50]]);
    expect(calls).toEqual(['start', 'move 50', 'end 50 flick']);
  });

  it('stops listening when destroyed', () => {
    attach().destroy();
    pull([[0, 80]]);
    expect(calls).toEqual([]);
  });
});

describe('dragDismiss, mouse', () => {
  function pointer(type: string, y: number, target: Fake, dt = 16): Event {
    clock += dt;
    const e = new Event(type);
    Object.defineProperty(e, 'timeStamp', { value: clock });
    Object.defineProperty(e, 'target', { value: target });
    return Object.assign(e, {
      pointerType: 'mouse',
      pointerId: 1,
      isPrimary: true,
      button: 0,
      clientX: 200,
      clientY: y
    });
  }

  it('drags only from a handle, capturing once it is a drag', () => {
    attach();
    const capture = vi.fn();
    const handle = el(node, { attrs: ['data-drag-handle'], setPointerCapture: capture });
    node.dispatchEvent(pointer('pointerdown', 300, el(handle)));
    windowTarget.dispatchEvent(pointer('pointermove', 303, el(handle)));
    expect(capture).not.toHaveBeenCalled();
    windowTarget.dispatchEvent(pointer('pointermove', 400, el(handle)));
    windowTarget.dispatchEvent(pointer('pointerup', 400, el(handle)));
    expect(capture).toHaveBeenCalledWith(1);
    expect(calls).toEqual(['start', 'move 100', 'end 100 flick']);
  });

  it('does not drag from the rest of the overlay, where a mouse selects or works a control', () => {
    attach();
    node.dispatchEvent(pointer('pointerdown', 300, el(node)));
    windowTarget.dispatchEvent(pointer('pointermove', 400, el(node)));
    windowTarget.dispatchEvent(pointer('pointerup', 400, el(node)));
    expect(calls).toEqual([]);
  });
});
