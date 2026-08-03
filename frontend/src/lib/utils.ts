import { clsx, type ClassValue } from 'clsx';
import { twMerge } from 'tailwind-merge';

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}

/** Non-mutating Fisher–Yates shuffle; returns a new array. */
export function shuffle<T>(arr: readonly T[]): T[] {
  const out = arr.slice();
  for (let i = out.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [out[i], out[j]] = [out[j], out[i]];
  }
  return out;
}

/**
 * Bring the active item of a horizontally scrollable strip (segmented tab bars)
 * into view, centred when there's room. Mobile is the case that matters: those
 * bars overflow on narrow screens, so the active tab can sit off-screen after a
 * route change unless we scroll to it. No-ops when the strip isn't overflowing,
 * and only moves the strip's own scrollLeft (never an ancestor, which
 * `scrollIntoView` would do).
 */
export function scrollStripToActive(
  scroller: HTMLElement | null,
  active: HTMLElement | null | undefined,
  behavior: ScrollBehavior = 'smooth'
): void {
  if (!scroller || !active) return;
  if (scroller.scrollWidth <= scroller.clientWidth + 1) return;
  const box = active.getBoundingClientRect();
  const view = scroller.getBoundingClientRect();
  const delta = box.left - view.left - (view.width - box.width) / 2;
  if (Math.abs(delta) < 1) return;
  const reduced =
    typeof window !== 'undefined' &&
    window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true;
  scroller.scrollTo({ left: scroller.scrollLeft + delta, behavior: reduced ? 'auto' : behavior });
}

export type WithoutChild<T> = T extends { child?: unknown } ? Omit<T, 'child'> : T;
export type WithoutChildren<T> = T extends { children?: unknown } ? Omit<T, 'children'> : T;
export type WithoutChildrenOrChild<T> = WithoutChildren<WithoutChild<T>>;
export type WithElementRef<T, U extends HTMLElement = HTMLElement> = T & { ref?: U | null };
