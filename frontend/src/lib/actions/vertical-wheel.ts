/**
 * `use:verticalWheel` — on a row that scrolls only sideways (a shelf, a paged grid), wheel
 * movement that is mostly vertical scrolls the page around it.
 *
 * Browsers latch a trackpad gesture, momentum included, to the scroller it first moved. A swipe
 * that starts with a little sideways drift over a shelf latches to the shelf, which cannot scroll
 * vertically, so the rest of that swipe moves nothing at all. Touch screens choose an axis per
 * gesture and never had this. So this action takes vertical-dominant wheel events itself and
 * applies them to the nearest ancestor that scrolls vertically; sideways ones stay native, which
 * keeps the row's scroll snapping and momentum.
 *
 * Pinch-zoom (ctrlKey) and Shift+wheel (a mouse's sideways scroll) are left alone.
 */

const LINE_PX = 16;

function isVerticalScroller(el: Element): boolean {
  const { overflowY } = getComputedStyle(el);
  return (
    (overflowY === 'auto' || overflowY === 'scroll' || overflowY === 'overlay') &&
    el.scrollHeight > el.clientHeight
  );
}

function verticalScrollerAbove(node: HTMLElement): Element | null {
  for (let el = node.parentElement; el; el = el.parentElement) {
    if (isVerticalScroller(el)) return el;
  }
  return node.ownerDocument.scrollingElement;
}

function pixelsOf(e: WheelEvent, scroller: Element): number {
  if (e.deltaMode === 1) return e.deltaY * LINE_PX;
  if (e.deltaMode === 2) return e.deltaY * scroller.clientHeight;
  return e.deltaY;
}

export function verticalWheel(node: HTMLElement) {
  const onWheel = (e: WheelEvent) => {
    if (e.ctrlKey || e.shiftKey || e.defaultPrevented) return;
    if (Math.abs(e.deltaY) <= Math.abs(e.deltaX)) return;
    const scroller = verticalScrollerAbove(node);
    if (!scroller) return;
    // Once a gesture's first event went unhandled, Chrome sends the rest uncancelable; the
    // gesture is then latched here and the browser drops their vertical part anyway.
    if (e.cancelable) e.preventDefault();
    scroller.scrollBy({ top: pixelsOf(e, scroller), behavior: 'instant' });
  };
  node.addEventListener('wheel', onWheel, { passive: false });
  return {
    destroy() {
      node.removeEventListener('wheel', onWheel);
    }
  };
}
