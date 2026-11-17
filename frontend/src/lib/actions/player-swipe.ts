type PlayerSwipeOptions = {
  disabled?: boolean;
  onnext: () => void;
  onprevious: () => void;
  onopen: () => void;
  /** Horizontal-dominant moves past the slop, with the raw distance from where the finger went down. */
  ondrag?: (dx: number) => void;
  /** The finger let go (or was taken) after a drag, without committing to next or previous. */
  onsettle?: () => void;
};

/** Compact player shortcuts. The host owns touch-action: none; buttons remain tap targets. */
export function playerSwipe(node: HTMLElement, options: PlayerSwipeOptions) {
  let gesture: { id: number; x: number; y: number; moved: boolean; dragged: boolean } | null = null;
  let swallowClick = false;
  let expiry: ReturnType<typeof setTimeout> | undefined;

  function down(e: PointerEvent) {
    if (e.pointerType === 'mouse' || options.disabled) return;
    if (!e.isPrimary || gesture) {
      gesture = null;
      return;
    }
    clearTimeout(expiry);
    swallowClick = false;
    gesture = { id: e.pointerId, x: e.clientX, y: e.clientY, moved: false, dragged: false };
  }

  function move(e: PointerEvent) {
    if (!gesture || e.pointerId !== gesture.id) return;
    const dx = e.clientX - gesture.x;
    const dy = e.clientY - gesture.y;
    if (Math.hypot(dx, dy) <= 10) return;
    gesture.moved = true;
    // Capture only after a drag: a plain tap still clicks the original button.
    if (!node.hasPointerCapture(e.pointerId)) node.setPointerCapture(e.pointerId);
    if (Math.abs(dx) > Math.abs(dy)) {
      gesture.dragged = true;
      options.ondrag?.(dx);
    }
  }

  function end(e: PointerEvent) {
    // Touch implicitly captures on the pressed button. Its capture loss bubbles when
    // move() transfers capture to the bar; only losing the bar's own capture cancels.
    if (e.type === 'lostpointercapture' && e.target !== node) return;
    const g = gesture;
    if (!g || e.pointerId !== g.id) return;
    gesture = null;
    const dx = e.clientX - g.x;
    const dy = e.clientY - g.y;
    if (g.moved || Math.hypot(dx, dy) > 10) {
      swallowClick = true;
      clearTimeout(expiry);
      expiry = setTimeout(() => (swallowClick = false), 800);
    }
    if (node.hasPointerCapture(e.pointerId)) node.releasePointerCapture(e.pointerId);
    const settle = () => {
      if (g.dragged) options.onsettle?.();
    };
    if (e.type !== 'pointerup' || options.disabled) return settle();
    // Commit once, on release, so a long drag cannot skip several songs.
    if (Math.abs(dx) >= 48 && Math.abs(dx) > Math.abs(dy) * 1.3) {
      if (dx < 0) options.onnext();
      else options.onprevious();
      return;
    }
    settle();
    if (dy <= -24 && Math.abs(dy) > Math.abs(dx) * 1.3) {
      options.onopen();
    }
  }

  function click(e: MouseEvent) {
    if (!swallowClick || e.detail === 0) return;
    swallowClick = false;
    e.preventDefault();
    e.stopImmediatePropagation();
  }

  node.addEventListener('pointerdown', down);
  node.addEventListener('pointermove', move);
  node.addEventListener('pointerup', end);
  node.addEventListener('pointercancel', end);
  node.addEventListener('lostpointercapture', end);
  node.addEventListener('click', click, true);
  return {
    update(next: PlayerSwipeOptions) {
      options = next;
      if (options.disabled) gesture = null;
    },
    destroy() {
      clearTimeout(expiry);
      node.removeEventListener('pointerdown', down);
      node.removeEventListener('pointermove', move);
      node.removeEventListener('pointerup', end);
      node.removeEventListener('pointercancel', end);
      node.removeEventListener('lostpointercapture', end);
      node.removeEventListener('click', click, true);
    }
  };
}
