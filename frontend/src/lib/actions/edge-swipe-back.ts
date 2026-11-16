import { isInstalledApp } from '#lib/hooks/viewport-insets.svelte.ts';
import { nativePlatform } from '#lib/native/shell.ts';
import { DISMISS_VELOCITY, EASE_PRESENT, prefersReducedMotion } from '#lib/motion.ts';
import { backPreviews, copyBackPreview, mountBackPreview } from '#lib/navigation/back-preview.ts';
import { navBack } from '#lib/stores/nav-back.svelte.ts';

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
 * move sideways before it moves down. The foreground follows the finger one-to-one, revealing
 * a visual copy of the Back target with a slight parallax. A release completes the slide or
 * returns the page to rest; navigation happens only after committing. Player and tabs sit outside
 * this column and stay fixed. Reduce Motion uses opacity feedback without moving either page.
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

/** The previous page travels a quarter-width behind the foreground. */
const PARALLAX = 0.25;
const SPRING_MS = 250;
const LEAVE_MS = 220;
/** A Back that never reports landing must not leave the live page hidden. */
const LAND_TIMEOUT_MS = 1200;

/** Any open modal surface: bits-ui dialogs and sheets carry role + data-state; native <dialog>. */
const OPEN_MODAL =
  '[role="dialog"][data-state="open"], [role="alertdialog"][data-state="open"], dialog[open]';

type Gesture = {
  id: number;
  x0: number;
  y0: number;
  decided: boolean;
  dx: number;
  width: number;
  reduced: boolean;
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
  let underneath: HTMLElement | null = null;
  let leaving: HTMLElement | null = null;
  let committed = false;
  let settleTimer: ReturnType<typeof setTimeout> | null = null;
  let savedTransform = '';
  let savedTransition = '';
  let savedOpacity = '';
  let savedShadow = '';
  let savedBackground = '';
  let savedPosition = '';
  let savedZIndex = '';
  /** The page has been shifted and needs putting back. */
  let moved = false;
  /** Bumped whenever a settle is superseded, so a Back that lands late cannot touch a new swipe. */
  let settleGen = 0;
  function armed(): boolean {
    return (
      enabled &&
      !committed &&
      navBack.current !== null &&
      isInstalledApp() &&
      // Android has its own back gesture on this edge, in the app shell as anywhere else.
      nativePlatform() !== 'android' &&
      document.querySelector(OPEN_MODAL) === null
    );
  }

  function paint(g: Gesture): void {
    const progress = Math.min(1, g.dx / g.width);
    if (g.reduced) {
      node.style.opacity = String(1 - progress * 0.2);
      return;
    }
    node.style.transform = `translate3d(${g.dx}px, 0, 0)`;
    if (underneath) {
      underneath.style.transform = `translate3d(${(g.dx - g.width) * PARALLAX}px, 0, 0)`;
      underneath.style.opacity = String(0.8 + progress * 0.2);
    }
  }

  function begin(g: Gesture): void {
    if (settleTimer) clearTimeout(settleTimer);
    settleTimer = null;
    settleGen++;
    if (moved) restoreNode();
    savedTransform = node.style.transform;
    savedTransition = node.style.transition;
    savedOpacity = node.style.opacity;
    savedShadow = node.style.boxShadow;
    savedBackground = node.style.background;
    savedPosition = node.style.position;
    savedZIndex = node.style.zIndex;
    moved = true;
    node.style.transition = 'none';
    if (g.reduced) return;
    node.style.background = getComputedStyle(node.parentElement!).backgroundColor;
    node.style.position = 'relative';
    node.style.zIndex = '1';
    node.style.boxShadow = '-8px 0 24px rgb(0 0 0 / 0.18)';
    const preview = backPreviews.get(navBack.href, node);
    if (preview) {
      underneath = mountBackPreview(preview, node.parentElement!);
      underneath.style.zIndex = '0';
      underneath.style.transition = 'none';
    }
  }

  function restoreNode(): void {
    node.style.transform = savedTransform;
    node.style.transition = savedTransition;
    node.style.opacity = savedOpacity;
    node.style.boxShadow = savedShadow;
    node.style.background = savedBackground;
    node.style.position = savedPosition;
    node.style.zIndex = savedZIndex;
    underneath?.remove();
    underneath = null;
    leaving?.remove();
    leaving = null;
    committed = false;
    moved = false;
  }

  function springBack(g: Gesture): void {
    const ms = g.reduced ? 0 : SPRING_MS;
    node.style.transition = `transform ${ms}ms ${EASE_PRESENT}, opacity ${ms}ms ${EASE_PRESENT}`;
    node.style.transform = savedTransform;
    node.style.opacity = savedOpacity;
    if (underneath) {
      underneath.style.transition = `transform ${ms}ms ${EASE_PRESENT}, opacity ${ms}ms ${EASE_PRESENT}`;
      underneath.style.transform = `translate3d(${-g.width * PARALLAX}px, 0, 0)`;
      underneath.style.opacity = '0.8';
    }
    settleTimer = setTimeout(() => {
      settleTimer = null;
      restoreNode();
    }, ms + 20);
  }

  function commit(g: Gesture): void {
    const back = navBack.current;
    committed = true;
    // Freeze the outgoing page before routing can replace its children. The live route can
    // then render underneath without turning into the page that slides off-screen.
    if (!g.reduced) {
      leaving = mountBackPreview(copyBackPreview(node), node.parentElement!);
      leaving.style.zIndex = '2';
      leaving.style.boxShadow = node.style.boxShadow;
      leaving.style.transform = `translate3d(${g.dx}px, 0, 0)`;
      // Commit the starting position before the CSS transition is retargeted.
      leaving.getBoundingClientRect();
      leaving.style.transition = `transform ${LEAVE_MS}ms ${EASE_PRESENT}`;
      leaving.style.transform = `translate3d(${g.width}px, 0, 0)`;
      if (underneath) {
        underneath.style.transition = `transform ${LEAVE_MS}ms ${EASE_PRESENT}, opacity ${LEAVE_MS}ms ${EASE_PRESENT}`;
        underneath.style.transform = 'translate3d(0, 0, 0)';
        underneath.style.opacity = '1';
      }
    }
    node.style.transform = savedTransform;
    node.style.opacity = '0';
    const gen = ++settleGen;
    let landed = false;
    let finished = g.reduced;
    const clean = () => {
      if (gen !== settleGen || !landed || !finished) return;
      if (settleTimer) clearTimeout(settleTimer);
      settleTimer = null;
      restoreNode();
    };
    settleTimer = setTimeout(() => {
      landed = finished = true;
      clean();
    }, LAND_TIMEOUT_MS);
    if (!g.reduced)
      setTimeout(() => {
        finished = true;
        clean();
      }, LEAVE_MS);
    void (async () => {
      await back?.();
    })().then(
      () => {
        landed = true;
        clean();
      },
      () => {
        landed = true;
        clean();
      }
    );
  }

  function velocity(g: Gesture): number {
    const previous = g.samples.at(-2);
    const lastMove = g.samples.at(-1);
    // A reversal at release is cancellation intent, even after a long forward drag.
    const first = previous && lastMove && lastMove.x < previous.x ? previous : g.samples[0];
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
      if (g.decided) springBack(g);
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
      width: node.clientWidth,
      reduced: prefersReducedMotion(),
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
    g.dx = Math.min(g.width, Math.max(0, dx));
    g.samples.push({ x: touch.clientX, t: e.timeStamp });
    // Velocity over the last ~100ms, so a slow drag that ends in a flick still reads as a flick.
    while (g.samples.length > 2 && e.timeStamp - g.samples[0].t > 100) g.samples.shift();
    paint(g);
  }

  function onTouchEnd(e: TouchEvent): void {
    const g = gesture;
    if (!g || !track(e.changedTouches, g.id)) return;
    gesture = null;
    if (!g.decided) return;
    const speed = velocity(g);
    const flicked = g.dx >= MIN_FLICK && speed > DISMISS_VELOCITY;
    if (
      e.type === 'touchend' &&
      speed >= -DISMISS_VELOCITY &&
      (g.dx > g.width * COMMIT_FRACTION || flicked)
    )
      commit(g);
    else springBack(g);
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
        if (g.decided) springBack(g);
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
    }
  };
}
