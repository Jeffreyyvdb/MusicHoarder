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
