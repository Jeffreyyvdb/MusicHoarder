import { beforeEach, describe, expect, it, vi } from 'vitest';
import { verticalWheel } from './vertical-wheel';

/**
 * The suite runs in node: elements are EventTargets with the few fields the action reads, and a
 * wheel event is a plain cancelable Event carrying wheel fields.
 */

class FakeElement extends EventTarget {
  overflowY = 'visible';
  scrollHeight = 100;
  clientHeight = 100;
  scrolledBy: number[] = [];
  parentElement: FakeElement | null = null;
  ownerDocument = { scrollingElement: null as FakeElement | null };
  scrollBy({ top }: { top: number }) {
    this.scrolledBy.push(top);
  }
}

function wheel(init: Partial<WheelEvent> & { cancelable?: boolean }): Event {
  const { cancelable = true, ...fields } = init;
  const e = new Event('wheel', { cancelable });
  Object.assign(e, {
    deltaX: 0,
    deltaY: 0,
    deltaMode: 0,
    ctrlKey: false,
    shiftKey: false,
    ...fields
  });
  return e;
}

let shelf: FakeElement;
let page: FakeElement;
let documentScroller: FakeElement;

beforeEach(() => {
  documentScroller = new FakeElement();
  page = new FakeElement();
  page.overflowY = 'auto';
  page.scrollHeight = 5000;
  const section = new FakeElement();
  section.parentElement = page;
  shelf = new FakeElement();
  shelf.parentElement = section;
  shelf.ownerDocument.scrollingElement = documentScroller;
  vi.stubGlobal('getComputedStyle', (el: FakeElement) => ({ overflowY: el.overflowY }));
  verticalWheel(shelf as unknown as HTMLElement);
});

describe('verticalWheel', () => {
  it('scrolls the page with a mostly vertical swipe over the row', () => {
    const e = wheel({ deltaX: 3, deltaY: 40 });
    shelf.dispatchEvent(e);
    expect(page.scrolledBy).toEqual([40]);
    expect(e.defaultPrevented).toBe(true);
  });

  it('still scrolls the page when the browser sends the swipe uncancelable', () => {
    shelf.dispatchEvent(wheel({ deltaX: 1, deltaY: 25, cancelable: false }));
    expect(page.scrolledBy).toEqual([25]);
  });

  it('leaves a mostly sideways swipe to the row', () => {
    const e = wheel({ deltaX: 30, deltaY: 10 });
    shelf.dispatchEvent(e);
    expect(page.scrolledBy).toEqual([]);
    expect(e.defaultPrevented).toBe(false);
  });

  it('leaves pinch-zoom and Shift+wheel alone', () => {
    shelf.dispatchEvent(wheel({ deltaY: 40, ctrlKey: true }));
    shelf.dispatchEvent(wheel({ deltaY: 40, shiftKey: true }));
    expect(page.scrolledBy).toEqual([]);
  });

  it('converts line deltas to pixels', () => {
    shelf.dispatchEvent(wheel({ deltaY: 3, deltaMode: 1 }));
    expect(page.scrolledBy).toEqual([48]);
  });

  it('skips an ancestor with nothing to scroll and falls back to the document', () => {
    page.scrollHeight = page.clientHeight;
    shelf.dispatchEvent(wheel({ deltaY: 40 }));
    expect(page.scrolledBy).toEqual([]);
    expect(documentScroller.scrolledBy).toEqual([40]);
  });
});
