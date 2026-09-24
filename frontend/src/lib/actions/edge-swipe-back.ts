import { isInstalledApp } from '$lib/hooks/viewport-insets.svelte';
import { DISMISS_VELOCITY, EASE_OUT, EASE_PRESENT, prefersReducedMotion } from '$lib/motion';
import { navBack } from '$lib/stores/nav-back.svelte';

/**
 * `use:edgeSwipeBack` — swipe right from the leading edge to go back, in the installed app.
 *
 * Safari gives a browser tab its own edge-swipe back, but an installed (home-screen) web app has
 * none, so without this the only way back on an iPhone is reaching for the top-left corner. It
 * runs exactly the active nav bar's Back action (published to `navBack` by PageToolbarV2), so the
 * gesture and the button can never disagree about where Back goes.
 *
 * Put it on the content root. It arms only when the app is installed, a Back action is published,
 * and no dialog or sheet is open; the touch must start within {@link EDGE}px of the left edge and
 * move sideways before it moves down. A 44px glass chevron follows the finger in from the edge
 * while the page shifts with it at a third of the distance; letting go past 30% of the width, or
 * with a flick, goes back, anything less springs back. A page that goes back stays where the finger
 * left it, fading, until the Back action reports the page it returns to is on screen — it never
 * snaps home first, which read as the page jumping back against the gesture.
 *
 * Touch events rather than pointer events on purpose: a pointer gesture the browser has started
 * to treat as a scroll is cancelled from under us, whereas a non-passive touchmove can claim the
 * gesture (preventDefault) the moment it reads as horizontal. Touches outside `[data-scroll-x]`
 * only — horizontal shelves and chip rows keep their own swipe.
 */

/** How close to the leading edge a swipe must start, in CSS px. */
export const EDGE = 20;

/** Movement before the gesture decides whether it is horizontal (ours) or vertical (a scroll). */
const SLOP = 10;

/** Share of the width past which a release goes back. */
const COMMIT_FRACTION = 0.3;

/** A flick shorter than this does not count, however fast — a twitch at the edge is not intent. */
const MIN_FLICK = 24;

/** How far the page follows the finger. */
const PARALLAX = 0.3;

const SPRING_MS = 250;
/** How long a committed page takes to fade while the page it goes back to comes in. */
const LEAVE_MS = 180;
/** A Back that never reports landing gets its page put back after this, whatever happened. */
const LAND_TIMEOUT_MS = 1200;
const CIRCLE = 44;
/** Where the chevron comes to rest, from the edge, once the swipe would commit. */
const CIRCLE_INSET = 16;

/** Any open modal surface: bits-ui dialogs and sheets carry role + data-state; native <dialog>. */
const OPEN_MODAL =
  '[role="dialog"][data-state="open"], [role="alertdialog"][data-state="open"], dialog[open]';

const CHEVRON_SVG =
  '<svg xmlns="http://www.w3.org/2000/svg" width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.25" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="m15 18-6-6 6-6"/></svg>';

type Gesture = {
  id: number;
  x0: number;
  y0: number;
  decided: boolean;
  dx: number;
  width: number;
  reduced: boolean;
  committable: boolean;
  /** Recent (x, t) samples for the release velocity. */
  samples: { x: number; t: number }[];
};

export type EdgeSwipeBackParams = { enabled?: boolean };

export function edgeSwipeBack(
  node: HTMLElement,
  params?: EdgeSwipeBackParams
): { update(params?: EdgeSwipeBackParams): void; destroy(): void } {
  let enabled = params?.enabled ?? true;
  let gesture: Gesture | null = null;
  let chevron: HTMLElement | null = null;
  let settleTimer: ReturnType<typeof setTimeout> | null = null;
  let savedTransform = '';
  let savedTransition = '';
  let savedOpacity = '';
  /** The page has been shifted and needs putting back. */
  let moved = false;
  /** Bumped whenever a settle is superseded, so a Back that lands late cannot touch a new swipe. */
  let settleGen = 0;
  /** The chevron's current vertical position, so it leaves along the line it came in on. */
  let chevronTop = 0;

  function armed(): boolean {
    return (
      enabled &&
      navBack.current !== null &&
      isInstalledApp() &&
      document.querySelector(OPEN_MODAL) === null
    );
  }

  function ensureChevron(): HTMLElement {
    if (chevron) return chevron;
    const el = document.createElement('div');
    // The glass comes from the chrome classes (and with it the solid Reduce Transparency /
    // Increase Contrast fallback) — no inline background, which would override that fallback.
    el.className = 'mh-glass mh-chrome';
    el.setAttribute('aria-hidden', 'true');
    Object.assign(el.style, {
      position: 'fixed',
      left: '0px',
      top: '0px',
      width: `${CIRCLE}px`,
      height: `${CIRCLE}px`,
      borderRadius: '9999px',
      display: 'grid',
      placeItems: 'center',
      color: 'var(--foreground)',
      zIndex: '60',
      pointerEvents: 'none',
      opacity: '0',
      transform: `translate3d(${-CIRCLE - 8}px, 0, 0)`,
      willChange: 'transform, opacity'
    });
    el.innerHTML = CHEVRON_SVG;
    document.body.appendChild(el);
    chevron = el;
    return el;
  }

  function chevronY(clientY: number): number {
    const max = window.innerHeight - CIRCLE - 8;
    return Math.min(Math.max(clientY - CIRCLE / 2, 8), Math.max(8, max));
  }

  function paint(g: Gesture, clientY: number): void {
    const commitAt = g.width * COMMIT_FRACTION;
    const progress = Math.min(1, g.dx / commitAt);
    // In from off-screen with the finger, coming to rest a little inside the edge.
    const x = Math.min(g.dx * 0.5, CIRCLE + CIRCLE_INSET) - CIRCLE;
    const el = ensureChevron();
    chevronTop = chevronY(clientY);
    el.style.transition = 'none';
    el.style.opacity = String(progress);
    el.style.transform = `translate3d(${x}px, ${chevronTop}px, 0) scale(${0.8 + 0.2 * progress})`;
    if (!g.reduced) node.style.transform = `translate3d(${g.dx * PARALLAX}px, 0, 0)`;

    // One tick of the haptic engine as the swipe crosses into "will go back", like iOS does.
    const committable = g.dx >= commitAt;
    if (committable && !g.committable) {
      try {
        navigator.vibrate?.(10);
      } catch {
        // no haptics here — the chevron's full opacity is the signal
      }
    }
    g.committable = committable;
  }

  function begin(g: Gesture): void {
    // A swipe that starts while the last one is still settling takes over from the resting page.
    if (settleTimer) {
      clearTimeout(settleTimer);
      settleTimer = null;
      settleGen++;
      if (moved) restoreNode();
    }
    savedTransform = node.style.transform;
    savedTransition = node.style.transition;
    savedOpacity = node.style.opacity;
    // Reduce Motion: the chevron alone carries the gesture; the page does not move.
    if (g.reduced) return;
    moved = true;
    // Follow the finger exactly: no transition while dragging.
    node.style.transition = 'none';
  }

  function restoreNode(): void {
    node.style.transform = savedTransform;
    node.style.transition = savedTransition;
    node.style.opacity = savedOpacity;
    moved = false;
  }

  function hideChevron(ms: number, easing: string): void {
    if (!chevron) return;
    chevron.style.transition = `transform ${ms}ms ${easing}, opacity ${ms}ms ${easing}`;
    chevron.style.opacity = '0';
    chevron.style.transform = `translate3d(${-CIRCLE - 8}px, ${chevronTop}px, 0) scale(0.8)`;
  }

  function springBack(): void {
    hideChevron(SPRING_MS, EASE_PRESENT);
    if (!moved) return;
    node.style.transition = `transform ${SPRING_MS}ms ${EASE_PRESENT}`;
    node.style.transform = 'translate3d(0, 0, 0)';
    settleTimer = setTimeout(() => {
      settleTimer = null;
      // Drop the transform entirely: a lingering one makes the content root the containing
      // block for every position: fixed descendant.
      restoreNode();
    }, SPRING_MS + 20);
  }

  function commit(): void {
    const back = navBack.current;
    hideChevron(150, EASE_OUT);
    if (settleTimer) clearTimeout(settleTimer);
    settleTimer = null;
    if (!moved) {
      back?.();
      return;
    }
    // Hold the page where the finger left it and let it fade while the Back runs; put it back only
    // once the page it returns to is on screen, so the swap happens in one frame.
    node.style.transition = `opacity ${LEAVE_MS}ms ${EASE_OUT}`;
    node.style.opacity = '0';
    const gen = ++settleGen;
    const land = () => {
      if (gen !== settleGen) return;
      if (settleTimer) clearTimeout(settleTimer);
      settleTimer = null;
      restoreNode();
    };
    settleTimer = setTimeout(land, LAND_TIMEOUT_MS);
    void Promise.resolve(back?.()).then(land, land);
  }

  function velocity(g: Gesture): number {
    const first = g.samples[0];
    const last = g.samples[g.samples.length - 1];
    if (!first || !last || last.t <= first.t) return 0;
    return (last.x - first.x) / (last.t - first.t);
  }

  function track(touches: TouchList, id: number): Touch | null {
    for (const t of touches) if (t.identifier === id) return t;
    return null;
  }

  function onTouchStart(e: TouchEvent): void {
    if (gesture) {
      // A second finger: this is a pinch or a scroll, not a swipe back.
      const g = gesture;
      gesture = null;
      if (g.decided) springBack();
      return;
    }
    if (e.touches.length !== 1 || !armed()) return;
    const touch = e.touches[0];
    if (touch.clientX > EDGE) return;
    const target = e.target as Element | null;
    if (target?.closest?.('[data-scroll-x]')) return;
    gesture = {
      id: touch.identifier,
      x0: touch.clientX,
      y0: touch.clientY,
      decided: false,
      dx: 0,
      width: window.innerWidth,
      reduced: prefersReducedMotion(),
      committable: false,
      samples: [{ x: touch.clientX, t: e.timeStamp }]
    };
  }

  function onTouchMove(e: TouchEvent): void {
    const g = gesture;
    if (!g) return;
    const touch = track(e.changedTouches, g.id);
    if (!touch) return;
    const dx = touch.clientX - g.x0;
    const dy = touch.clientY - g.y0;

    if (!g.decided) {
      // Hold the page still while the move still reads as sideways, so the scroll does not start
      // before the gesture has decided; a downward move is left alone and scrolls as usual.
      const sideways = dx > 0 && Math.abs(dx) > Math.abs(dy);
      if (sideways && e.cancelable) e.preventDefault();
      if (Math.hypot(dx, dy) < SLOP) return;
      if (!sideways) {
        gesture = null;
        return;
      }
      g.decided = true;
      begin(g);
    }

    if (e.cancelable) e.preventDefault();
    g.dx = Math.max(0, dx);
    g.samples.push({ x: touch.clientX, t: e.timeStamp });
    // Velocity over the last ~100ms, so a slow drag that ends in a flick still reads as a flick.
    while (g.samples.length > 2 && e.timeStamp - g.samples[0].t > 100) g.samples.shift();
    paint(g, touch.clientY);
  }

  function onTouchEnd(e: TouchEvent): void {
    const g = gesture;
    if (!g || !track(e.changedTouches, g.id)) return;
    gesture = null;
    if (!g.decided) return;
    const flicked = g.dx >= MIN_FLICK && velocity(g) > DISMISS_VELOCITY;
    if (e.type === 'touchend' && (g.dx > g.width * COMMIT_FRACTION || flicked)) commit();
    else springBack();
  }

  node.addEventListener('touchstart', onTouchStart, { passive: true });
  node.addEventListener('touchmove', onTouchMove, { passive: false });
  node.addEventListener('touchend', onTouchEnd);
  node.addEventListener('touchcancel', onTouchEnd);

  return {
    update(next?: EdgeSwipeBackParams) {
      enabled = next?.enabled ?? true;
      if (!enabled && gesture) {
        const g = gesture;
        gesture = null;
        if (g.decided) springBack();
      }
    },
    destroy() {
      node.removeEventListener('touchstart', onTouchStart);
      node.removeEventListener('touchmove', onTouchMove);
      node.removeEventListener('touchend', onTouchEnd);
      node.removeEventListener('touchcancel', onTouchEnd);
      if (settleTimer) clearTimeout(settleTimer);
      settleGen++;
      if (moved) restoreNode();
      gesture = null;
      chevron?.remove();
      chevron = null;
    }
  };
}
