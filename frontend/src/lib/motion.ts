/**
 * Shared motion constants, so every drag-to-dismiss and presentation in the app agrees.
 *
 * Controls and menus stay at or under 200ms on {@link EASE_OUT}; presentations (sheets, Now
 * Playing rising and falling, the edge-swipe spring-back) run on {@link EASE_PRESENT}, the curve
 * iOS uses for its own sheets. A drag follows the finger with no transition and, on release,
 * dismisses past 25% of its travel OR when flicked faster than {@link DISMISS_VELOCITY}; below
 * both it springs back.
 */

/** px/ms. A release faster than this dismisses however short the drag was — a flick. */
export const DISMISS_VELOCITY = 0.11;

/** Fast start, long settle: things responding to a tap. */
export const EASE_OUT = 'cubic-bezier(0.23, 1, 0.32, 1)';

/** Things that move across the screen rather than appear or leave: a reorder. */
export const EASE_IN_OUT = 'cubic-bezier(0.77, 0, 0.175, 1)';

/** Presentations and dismissals — sheets, the Now Playing overlay, spring-backs. */
export const EASE_PRESENT = 'cubic-bezier(0.32, 0.72, 0, 1)';

/**
 * True when the person asked for Reduce Motion. SSR-safe (false on the server). Read it at the
 * moment of animating rather than caching it: the setting can change while the app is open.
 */
export function prefersReducedMotion(): boolean {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return false;
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

/**
 * Parameters for a Svelte `transition:fly` that respect Reduce Motion: the travel is dropped (a
 * duration of 0) when the setting is on. Svelte's transitions run through the Web Animations API,
 * which the global CSS clamp in app.css cannot reach — so every `fly` goes through this rather than
 * passing its parameters straight in. Called at the moment the transition runs, so a setting
 * changed while the app is open is honoured.
 */
export function motionFly<T extends { duration?: number }>(params: T): T {
  return prefersReducedMotion() ? { ...params, duration: 0 } : params;
}

/**
 * A CSS `cubic-bezier(x1, y1, x2, y2)` string as a function of progress, for Svelte transitions,
 * whose `easing` takes a function.
 */
export function easingOf(curve: string): (t: number) => number {
  const nums = curve.match(/-?\d*\.?\d+/g)?.map(Number);
  if (!nums || nums.length !== 4) throw new Error(`Not a cubic-bezier curve: ${curve}`);
  const [x1, y1, x2, y2] = nums;
  const bezier = (a: number, b: number, t: number) =>
    3 * (1 - t) * (1 - t) * t * a + 3 * (1 - t) * t * t * b + t * t * t;
  const slope = (a: number, b: number, t: number) =>
    3 * (1 - t) * (1 - t) * a + 6 * (1 - t) * t * (b - a) + 3 * t * t * (1 - b);
  return (x) => {
    if (x <= 0) return 0;
    if (x >= 1) return 1;
    let t = x;
    for (let i = 0; i < 8; i++) {
      const err = bezier(x1, x2, t) - x;
      if (Math.abs(err) < 1e-6) return bezier(y1, y2, t);
      const d = slope(x1, x2, t);
      if (Math.abs(d) < 1e-6) break;
      t -= err / d;
    }
    let lo = 0;
    let hi = 1;
    t = x;
    while (hi - lo > 1e-6) {
      if (bezier(x1, x2, t) < x) lo = t;
      else hi = t;
      t = (lo + hi) / 2;
    }
    return bezier(y1, y2, t);
  };
}

export const easeOut = easingOf(EASE_OUT);
export const easeInOut = easingOf(EASE_IN_OUT);
