/**
 * `use:dragDismiss` — pull a full-screen surface down from anywhere on it, as in Spotify and
 * Apple Music on iOS.
 *
 * Touch: a touch anywhere on the node that moves down before it moves sideways or up is the
 * host's drag. Inside a scroller that is not at its top the scroll wins, for the whole touch, as in
 * an iOS sheet; at its top a pull down drags the surface instead of rubber-banding the list. An
 * upward or sideways move is left to whatever else wants it (a scroll, a row's swipe). Elements
 * that take their own drags opt out: {@link IGNORE}.
 *
 * Mouse: only from a `[data-drag-handle]` (the top bar, the artwork), since everywhere else a
 * mouse drag selects text or works a control.
 *
 * Touch events rather than pointer events on purpose: once the browser treats a touch as a scroll
 * it cancels the pointer from under us, whereas a non-passive touchmove can hold a scroller still
 * (preventDefault) the moment the move reads as downward. iOS settles whether a touch may scroll on
 * its first touchmove, so that is where the hold has to happen; a touch already scrolling is no
 * longer cancelable and is let go.
 *
 * The host paints: it gets the downward offset as the finger moves, and on release the offset and
 * the release velocity, and decides between closing and springing back.
 */

/** Movement before a press becomes a drag; below it a tap keeps its click. */
const SLOP = 6;

/** A finger that rested this long before lifting is not a flick, whatever its last speed was. */
const REST_MS = 80;

/** Controls that take their own drags, and the regions that opt out wholesale. */
export const IGNORE =
  '[data-drag-ignore], [role="slider"], [aria-haspopup], input, textarea, select';

export type DragDismissHandlers = {
  /** The press became a downward drag. */
  onstart: () => void;
  /** The finger is `offset` px below where it went down (never negative). */
  onmove: (offset: number) => void;
  /** Let go after a drag. `velocity` is px/ms downward, 0 after a rest. */
  onend: (offset: number, velocity: number) => void;
  /** The drag was taken away (a second finger, a cancelled touch). */
  oncancel: () => void;
};

type Gesture = {
  id: number;
  x0: number;
  y0: number;
  decided: boolean;
  lastY: number;
  lastT: number;
  velocity: number;
  /** Mouse only: the handle that takes pointer capture once the press is a drag. */
  handle?: Element;
};

function closest(target: EventTarget | null, selector: string): Element | null {
  return (target as Element | null)?.closest?.(selector) ?? null;
}

/** True when a vertical scroller between `target` and `root` is scrolled away from its top. */
export function scrolledAway(target: EventTarget | null, root: Element): boolean {
  for (let el = target as Element | null; el?.closest && el !== root; el = el.parentElement) {
    if (el.scrollTop <= 0 || el.scrollHeight <= el.clientHeight) continue;
    const { overflowY } = getComputedStyle(el);
    if (overflowY === 'auto' || overflowY === 'scroll') return true;
  }
  return false;
}

export function dragDismiss(
  node: HTMLElement,
  handlers: DragDismissHandlers
): { update(handlers: DragDismissHandlers): void; destroy(): void } {
  let h = handlers;
  let gesture: Gesture | null = null;

  function start(id: number, x: number, y: number, t: number, handle?: Element): void {
    gesture = { id, x0: x, y0: y, decided: false, lastY: y, lastT: t, velocity: 0, handle };
  }

  function follow(g: Gesture, y: number, t: number): void {
    const dt = t - g.lastT;
    if (dt > 0) g.velocity = g.velocity * 0.2 + ((y - g.lastY) / dt) * 0.8;
    g.lastY = y;
    g.lastT = t;
    h.onmove(Math.max(0, y - g.y0));
  }

  function release(g: Gesture, y: number, t: number): void {
    gesture = null;
    if (!g.decided) return;
    h.onend(Math.max(0, y - g.y0), t - g.lastT > REST_MS ? 0 : g.velocity);
  }

  function cancel(): void {
    const g = gesture;
    gesture = null;
    if (g?.decided) h.oncancel();
  }

  // ── Touch ────────────────────────────────────────────────────────────────────

  function touchOf(list: TouchList, id: number): Touch | null {
    for (const t of list) if (t.identifier === id) return t;
    return null;
  }

  function onTouchStart(e: TouchEvent): void {
    // A second finger is a pinch, not a pull. A gesture still held under one finger is a touch
    // whose end never reached us (its element left the page): this touch replaces it.
    cancel();
    if (e.touches.length !== 1) return;
    if (closest(e.target, IGNORE) || scrolledAway(e.target, node)) return;
    const touch = e.touches[0];
    start(touch.identifier, touch.clientX, touch.clientY, e.timeStamp);
  }

  function onTouchMove(e: TouchEvent): void {
    const g = gesture;
    if (!g || g.handle) return;
    const touch = touchOf(e.changedTouches, g.id);
    if (!touch) return;
    const dx = touch.clientX - g.x0;
    const dy = touch.clientY - g.y0;
    if (!g.decided) {
      const downward = dy > 0 && dy >= Math.abs(dx);
      // Already scrolling (iOS let the first move through): not ours to take.
      if (!e.cancelable) {
        gesture = null;
        return;
      }
      if (downward) e.preventDefault();
      if (Math.hypot(dx, dy) < SLOP) return;
      if (!downward) {
        gesture = null;
        return;
      }
      g.decided = true;
      h.onstart();
    }
    if (e.cancelable) e.preventDefault();
    follow(g, touch.clientY, e.timeStamp);
  }

  function onTouchEnd(e: TouchEvent): void {
    const g = gesture;
    if (!g || g.handle) return;
    const touch = touchOf(e.changedTouches, g.id);
    if (!touch) return;
    if (e.type === 'touchcancel') cancel();
    else release(g, touch.clientY, e.timeStamp);
  }

  // ── Mouse ────────────────────────────────────────────────────────────────────
  // Moves and the release are followed on window: before the drag is claimed (and the pointer
  // captured) a quick flick leaves the handle within a frame.

  function listen(on: boolean): void {
    if (on) {
      window.addEventListener('pointermove', onPointerMove);
      window.addEventListener('pointerup', onPointerUp);
      window.addEventListener('pointercancel', onPointerCancel);
    } else {
      window.removeEventListener('pointermove', onPointerMove);
      window.removeEventListener('pointerup', onPointerUp);
      window.removeEventListener('pointercancel', onPointerCancel);
    }
  }

  function onPointerDown(e: PointerEvent): void {
    if (e.pointerType !== 'mouse' || gesture || !e.isPrimary || e.button !== 0) return;
    const handle = closest(e.target, '[data-drag-handle]');
    if (!handle || closest(e.target, IGNORE)) return;
    start(e.pointerId, e.clientX, e.clientY, e.timeStamp, handle);
    listen(true);
  }

  function onPointerMove(e: PointerEvent): void {
    const g = gesture;
    if (!g?.handle || e.pointerId !== g.id) return;
    const dy = e.clientY - g.y0;
    if (!g.decided) {
      if (dy < -SLOP) {
        gesture = null;
        listen(false);
        return;
      }
      if (dy < SLOP) return;
      g.decided = true;
      // Capture only once it is a drag, so a click still lands on the button pressed.
      g.handle.setPointerCapture?.(e.pointerId);
      h.onstart();
    }
    follow(g, e.clientY, e.timeStamp);
  }

  function onPointerUp(e: PointerEvent): void {
    const g = gesture;
    if (!g?.handle || e.pointerId !== g.id) return;
    listen(false);
    release(g, e.clientY, e.timeStamp);
  }

  function onPointerCancel(e: PointerEvent): void {
    if (!gesture?.handle || e.pointerId !== gesture.id) return;
    listen(false);
    cancel();
  }

  node.addEventListener('touchstart', onTouchStart, { passive: true });
  node.addEventListener('touchmove', onTouchMove, { passive: false });
  node.addEventListener('touchend', onTouchEnd);
  node.addEventListener('touchcancel', onTouchEnd);
  node.addEventListener('pointerdown', onPointerDown);

  return {
    update(next: DragDismissHandlers) {
      h = next;
    },
    destroy() {
      node.removeEventListener('touchstart', onTouchStart);
      node.removeEventListener('touchmove', onTouchMove);
      node.removeEventListener('touchend', onTouchEnd);
      node.removeEventListener('touchcancel', onTouchEnd);
      node.removeEventListener('pointerdown', onPointerDown);
      listen(false);
      gesture = null;
    }
  };
}
