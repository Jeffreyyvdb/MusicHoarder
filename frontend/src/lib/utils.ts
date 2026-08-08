import { clsx, type ClassValue } from 'clsx';
import { extendTailwindMerge } from 'tailwind-merge';

/**
 * The chrome type scale (`--text-nav*` in app.css) has to be declared here too.
 * tailwind-merge only knows Tailwind's stock scale, so it read `text-nav-sm` as a
 * *color* utility and dropped it whenever the same `cn()` call also carried a real
 * color — which is every FilterChip (`text-nav-sm … text-muted-foreground`). The
 * chips lost their size class entirely and rendered at the 16px root size.
 * Registering the names under `font-size` puts them in the right conflict group:
 * a size and a color now coexist, and two sizes still collapse to the last one.
 */
const twMerge = extendTailwindMerge({
  extend: {
    classGroups: {
      'font-size': [{ text: ['nav', 'nav-sm', 'nav-xs', 'nav-count', 'nav-badge'] }]
    }
  }
});

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

/**
 * Shared look for the naked-glyph transport controls (MiniPlayer, SongTransport):
 * bare solid glyph, no disc, no hover wash, press-scale for feedback.
 *
 * The focus ring is the part that matters. The shadcn button base paints
 * `focus-visible:border-ring` + a 3px `ring-ring/50` on a `rounded-lg` box, which
 * frames a bare play triangle in a green rectangle. Here it's a thin ring shaped
 * to the round glyph instead — still a clear keyboard focus indicator, but it
 * hugs the control rather than boxing it.
 */
export const transportGlyphClass =
  'text-foreground hover:text-foreground rounded-full bg-transparent transition-transform duration-100 ease-out hover:bg-transparent active:scale-90 focus-visible:border-transparent focus-visible:ring-2 focus-visible:ring-ring/60 dark:hover:bg-transparent';

/**
 * Drop focus after a pointer-driven click, the way native macOS controls do.
 *
 * A mouse click leaves the button focused even though `:focus-visible` stays off,
 * and the browser flips that still-focused element into `:focus-visible` on the
 * next keyboard event — so tapping ⌘⇧4 to grab a screenshot (or any stray
 * modifier press) paints a focus ring around whatever you last clicked. Keyboard
 * activation reports `detail === 0` and is left alone, so Tab/Enter users keep
 * both focus and the ring.
 */
export function blurAfterPointerClick(event: MouseEvent): void {
  if (event.detail > 0) (event.currentTarget as HTMLElement | null)?.blur();
}

export type WithoutChild<T> = T extends { child?: unknown } ? Omit<T, 'child'> : T;
export type WithoutChildren<T> = T extends { children?: unknown } ? Omit<T, 'children'> : T;
export type WithoutChildrenOrChild<T> = WithoutChildren<WithoutChild<T>>;
export type WithElementRef<T, U extends HTMLElement = HTMLElement> = T & { ref?: U | null };
