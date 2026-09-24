import { EASE_OUT, prefersReducedMotion } from '$lib/motion';

/**
 * `use:longpress` — touch-and-hold for touch and pen.
 *
 * iOS WebKit (Safari and installed web apps alike) never dispatches `contextmenu` for a
 * touch-and-hold, and on a link it shows its own link-preview callout instead. So a menu that is
 * only behind `oncontextmenu` is unreachable on an iPhone. This action gives touch and pen a real
 * long-press; a mouse or trackpad keeps the element's own `oncontextmenu` (right-click), which
 * this action leaves alone.
 *
 * Holding for {@link HOLD_MS} without moving more than {@link MOVE_TOLERANCE}px (and without the
 * page scrolling) calls `onlongpress` with the touch point, taps the haptic engine where there is
 * one, and swallows the click that the lift would otherwise deliver — so a long-press on a row
 * does not also play it. While held the element settles to 97% scale, the press feedback iOS
 * gives before a context menu.
 *
 * The element gets `-webkit-touch-callout: none` and `user-select: none` (the callout and the text
 * selection loupe would otherwise fight the gesture) and its images are made non-draggable. Every
 * long-press menu needs a visible twin (a ⋯ button) — a long-press is never discoverable alone.
 *
 * SSR-safe: actions only run in the browser.
 */

export type LongPressPoint = { x: number; y: number };

export type LongPressParams = {
  onlongpress: (point: LongPressPoint) => void;
  /** Off: no gesture, no styles, no interference with the element's own events. */
  disabled?: boolean;
};

/** How long a hold takes. iOS's context-menu hold is about half a second. */
export const HOLD_MS = 500;

/** Movement beyond this is a scroll or a drag, not a hold. */
export const MOVE_TOLERANCE = 10;

/**
 * Press feedback starts a beat after the finger lands, not on contact: a flick that scrolls a
 * list also begins with a pointerdown, and shrinking every row it passes over would make the
 * list shimmer while it scrolls.
 */
const FEEDBACK_DELAY_MS = 100;
const FEEDBACK_MS = 150;

/** How long after the lift a swallowed click may still arrive. */
const CLICK_SWALLOW_MS = 800;

const STYLE_PROPS: [name: string, value: string][] = [
  ['-webkit-touch-callout', 'none'],
  ['-webkit-user-select', 'none'],
  ['user-select', 'none']
];

export function longpress(
  node: HTMLElement,
  params: LongPressParams
): { update(params: LongPressParams): void; destroy(): void } {
  let options = params;
  let styled = false;
  const savedStyles = new Map<string, string>();

  /** The press in progress, if any. */
  let press: { id: number; x: number; y: number } | null = null;
  let holdTimer: ReturnType<typeof setTimeout> | null = null;
  let feedbackTimer: ReturnType<typeof setTimeout> | null = null;
  let feedbackOn = false;
  let savedTransform = '';
  let savedTransition = '';

  let swallowClick = false;
  let swallowTimer: ReturnType<typeof setTimeout> | null = null;
  /** The pointer type of the most recent press on this element, for the contextmenu guard. */
  let lastPointerType = '';

  function applyStyles(): void {
    if (styled) return;
    styled = true;
    for (const [name, value] of STYLE_PROPS) {
      savedStyles.set(name, node.style.getPropertyValue(name));
      node.style.setProperty(name, value);
    }
  }

  function restoreStyles(): void {
    if (!styled) return;
    styled = false;
    for (const [name] of STYLE_PROPS) {
      const previous = savedStyles.get(name);
      if (previous) node.style.setProperty(name, previous);
      else node.style.removeProperty(name);
    }
  }

  function undraggableImages(): void {
    for (const img of node.querySelectorAll('img')) img.draggable = false;
  }

  function startFeedback(): void {
    if (prefersReducedMotion()) return;
    feedbackOn = true;
    savedTransform = node.style.transform;
    savedTransition = node.style.transition;
    node.style.transition = `transform ${FEEDBACK_MS}ms ${EASE_OUT}`;
    node.style.transform = `${savedTransform} scale(0.97)`.trim();
  }

  function endFeedback(): void {
    if (!feedbackOn) return;
    feedbackOn = false;
    node.style.transform = savedTransform;
    // Leave the transition on for the settle back, then hand the element its own transition back.
    const transition = savedTransition;
    setTimeout(() => {
      if (!feedbackOn) node.style.transition = transition;
    }, FEEDBACK_MS);
  }

  function clearTimers(): void {
    if (holdTimer) clearTimeout(holdTimer);
    if (feedbackTimer) clearTimeout(feedbackTimer);
    holdTimer = null;
    feedbackTimer = null;
  }

  function stopListening(): void {
    document.removeEventListener('scroll', onScroll, true);
  }

  function cancel(): void {
    clearTimers();
    endFeedback();
    press = null;
    stopListening();
  }

  function fire(): void {
    if (!press) return;
    const point = { x: press.x, y: press.y };
    clearTimers();
    endFeedback();
    stopListening();
    press = null;
    swallowClick = true;
    try {
      navigator.vibrate?.(10);
    } catch {
      // vibrate can throw without a user activation in some browsers; the menu matters, not it
    }
    options.onlongpress(point);
  }

  function armSwallowExpiry(): void {
    if (!swallowClick) return;
    if (swallowTimer) clearTimeout(swallowTimer);
    swallowTimer = setTimeout(() => {
      swallowClick = false;
      swallowTimer = null;
    }, CLICK_SWALLOW_MS);
  }

  function onPointerDown(e: PointerEvent): void {
    lastPointerType = e.pointerType;
    if (options.disabled || e.pointerType === 'mouse') return;
    // A second finger is a pinch or a two-finger scroll, never a hold.
    if (press || !e.isPrimary) {
      cancel();
      return;
    }
    swallowClick = false;
    undraggableImages();
    press = { id: e.pointerId, x: e.clientX, y: e.clientY };
    feedbackTimer = setTimeout(startFeedback, FEEDBACK_DELAY_MS);
    holdTimer = setTimeout(fire, HOLD_MS);
    document.addEventListener('scroll', onScroll, { capture: true, passive: true });
  }

  function onPointerMove(e: PointerEvent): void {
    if (!press || e.pointerId !== press.id) return;
    if (Math.hypot(e.clientX - press.x, e.clientY - press.y) > MOVE_TOLERANCE) cancel();
  }

  function onPointerEnd(e: PointerEvent): void {
    if (press && e.pointerId === press.id) cancel();
    armSwallowExpiry();
  }

  /** Any scroll of a scroller the element sits in means the finger is scrolling, not holding. */
  function onScroll(e: Event): void {
    const target = e.target as Partial<Node> | null;
    if (target === document || target?.contains?.(node)) cancel();
  }

  function onClick(e: MouseEvent): void {
    if (!swallowClick) return;
    swallowClick = false;
    if (swallowTimer) clearTimeout(swallowTimer);
    swallowTimer = null;
    e.preventDefault();
    e.stopImmediatePropagation();
  }

  /**
   * Android Chrome DOES fire contextmenu on a touch hold, at about the same moment as this
   * action — so a row with both would open its menu twice (and the system link menu with it).
   * A touch or pen contextmenu is ours: it is swallowed, and when it arrives mid-press it IS the
   * long-press (fire now rather than race Chrome, which may cancel the pointer once it has
   * recognised the hold). A mouse contextmenu goes through untouched.
   */
  function onContextMenu(e: MouseEvent): void {
    if (options.disabled) return;
    const type = (e as PointerEvent).pointerType || lastPointerType;
    if (type !== 'touch' && type !== 'pen') return;
    e.preventDefault();
    e.stopImmediatePropagation();
    if (press) fire();
  }

  node.addEventListener('pointerdown', onPointerDown);
  node.addEventListener('pointermove', onPointerMove);
  node.addEventListener('pointerup', onPointerEnd);
  node.addEventListener('pointercancel', onPointerEnd);
  node.addEventListener('click', onClick, true);
  node.addEventListener('contextmenu', onContextMenu, true);
  if (!options.disabled) {
    applyStyles();
    undraggableImages();
  }

  return {
    update(next: LongPressParams) {
      options = next;
      if (next.disabled) {
        cancel();
        swallowClick = false;
        restoreStyles();
      } else {
        applyStyles();
      }
    },
    destroy() {
      cancel();
      if (swallowTimer) clearTimeout(swallowTimer);
      node.removeEventListener('pointerdown', onPointerDown);
      node.removeEventListener('pointermove', onPointerMove);
      node.removeEventListener('pointerup', onPointerEnd);
      node.removeEventListener('pointercancel', onPointerEnd);
      node.removeEventListener('click', onClick, true);
      node.removeEventListener('contextmenu', onContextMenu, true);
      restoreStyles();
    }
  };
}
