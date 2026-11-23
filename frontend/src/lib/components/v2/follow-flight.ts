import { EASE_IN_OUT, EASE_OUT, prefersReducedMotion } from '#lib/motion.ts';

/**
 * Following a suggested artist carries its portrait from the Suggested shelf to its place on the
 * Following shelf, so the two changes read as one move rather than a tile vanishing below and
 * another appearing above.
 *
 * The portrait that flies is a copy pinned to the viewport: both shelves are horizontal scrollers,
 * and a scroller clips on both axes, so a tile moved out of its own row would be cut off the moment
 * it left. The shelves' own tiles move with `animate:flip` at {@link SHELF_MOVE}, the same timing as
 * the flight, so the gap below closes and the gap above opens while the copy travels.
 */

/** One duration and curve for the flight and both shelves closing and opening around it. */
export const SHELF_MOVE = { duration: 400, easing: EASE_IN_OUT } as const;

/** The copy's handover to the landed tile: short, since the two show the same artist. */
const HANDOVER_MS = 150;

export type Flight = { from: DOMRect; ghost: HTMLElement };

/**
 * Pins a copy of `portrait` over it, before the real tile leaves its shelf, so no frame shows
 * neither. Null under Reduce Motion, or when the portrait is not laid out.
 */
export function takeOff(portrait: Element | null | undefined): Flight | null {
  if (!(portrait instanceof HTMLElement) || !portrait.isConnected || prefersReducedMotion()) {
    return null;
  }
  const from = portrait.getBoundingClientRect();
  if (from.width === 0 || from.height === 0) return null;

  const ghost = portrait.cloneNode(true) as HTMLElement;
  ghost.setAttribute('aria-hidden', 'true');
  ghost.removeAttribute('id');
  // A copied cover would replay its own fade-in from transparent.
  for (const img of ghost.querySelectorAll('.mh-cover-fade-in')) {
    img.classList.remove('mh-cover-fade-in');
  }
  // The portrait's sizing classes are `!important`, so the pinning has to be too.
  const pin: [string, string][] = [
    ['position', 'fixed'],
    ['left', `${from.left}px`],
    ['top', `${from.top}px`],
    ['width', `${from.width}px`],
    ['height', `${from.height}px`],
    ['margin', '0'],
    ['z-index', '40'],
    ['pointer-events', 'none'],
    ['transform-origin', '0 0'],
    ['will-change', 'transform']
  ];
  for (const [prop, value] of pin) ghost.style.setProperty(prop, value, 'important');
  document.body.append(ghost);
  return { from, ghost };
}

/**
 * Flies the copy onto `target` (the landed tile's portrait) and hands over to it. With no target,
 * as when the follow sorts beyond the shelf's preview, the copy fades where it is. `reveal` runs
 * once the copy is over the target, to show the tile under it.
 */
export async function land(
  flight: Flight,
  target: HTMLElement | null,
  reveal: () => void
): Promise<void> {
  const { from, ghost } = flight;
  try {
    if (!target?.isConnected) {
      reveal();
      await ghost.animate(
        [
          { opacity: 1, transform: 'none' },
          { opacity: 0, transform: 'scale(0.96)' }
        ],
        { duration: 200, easing: EASE_OUT, fill: 'forwards' }
      ).finished;
      return;
    }

    const to = destination(target);
    const dx = to.left - from.left;
    const dy = to.top - from.top;
    const sx = to.width / from.width;
    const sy = to.height / from.height;
    await ghost.animate(
      [{ transform: 'none' }, { transform: `translate(${dx}px, ${dy}px) scale(${sx}, ${sy})` }],
      { ...SHELF_MOVE, fill: 'forwards' }
    ).finished;

    reveal();
    await ghost.animate([{ opacity: 1 }, { opacity: 0 }], {
      duration: HANDOVER_MS,
      easing: 'ease',
      fill: 'forwards'
    }).finished;
  } catch {
    // Cancelled (the page went away mid-flight): nothing left to land on.
    reveal();
  } finally {
    ghost.remove();
  }
}

/**
 * Where `target` will be once its shelf shows it. A tile sorted off either side of the shelf is
 * scrolled to its snap point (the scroller snaps each tile's start to the scroll padding), and the
 * copy flies to where that scroll puts it rather than to where the tile is now.
 */
function destination(target: HTMLElement): DOMRect {
  const rect = target.getBoundingClientRect();
  const viewport = target.closest<HTMLElement>('[data-slot="scroll-area-viewport"]');
  if (!viewport) return rect;

  const view = viewport.getBoundingClientRect();
  if (rect.left >= view.left && rect.right <= view.right) return rect;

  const padding = Number.parseFloat(getComputedStyle(viewport).scrollPaddingLeft) || 0;
  const max = viewport.scrollWidth - viewport.clientWidth;
  const wanted = viewport.scrollLeft + (rect.left - view.left) - padding;
  const scrollTo = Math.min(Math.max(wanted, 0), max);
  const shift = scrollTo - viewport.scrollLeft;
  viewport.scrollTo({ left: scrollTo, behavior: 'smooth' });
  return new DOMRect(rect.left - shift, rect.top, rect.width, rect.height);
}
