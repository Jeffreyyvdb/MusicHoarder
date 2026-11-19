import { EASE_OUT, prefersReducedMotion } from '#lib/motion.ts';

/**
 * The wide Now Playing's mode changes, choreographed. The right column's view is the lead: it
 * arrives from the side when the column opens beside the player, and fades up when it replaces
 * another view. The player beside it is carried to its new place in the same beat and timing
 * family, so the two read as one move. Leaving the column needs no exit of its own: the video
 * backdrop sharpening behind the player (VideoBackdrop) is what the eye follows.
 */

/** What the right column holds, or `null` while the player stands alone. */
export type Stage = 'lyrics' | 'video' | 'queue' | 'info' | null;

export const MODE_MOTION_MS = 300;
const SWAP_MS = 200;

type Box = Pick<DOMRect, 'left' | 'top' | 'width' | 'height'>;

/**
 * The transform that puts `to` back where `from` was, with its origin on `to`'s centre in the
 * `mover`'s own coordinates. `null` when nothing visibly moved.
 */
export function glideFrame(
  from: Box,
  to: Box,
  mover: Box
): { transform: string; transformOrigin: string } | null {
  const toX = to.left + to.width / 2;
  const toY = to.top + to.height / 2;
  const dx = from.left + from.width / 2 - toX;
  const dy = from.top + from.height / 2 - toY;
  const scale = to.width > 0 ? from.width / to.width : 1;
  if (Math.abs(dx) < 1 && Math.abs(dy) < 1 && Math.abs(scale - 1) < 0.01) return null;
  return {
    transform: `translate(${dx}px, ${dy}px) scale(${scale})`,
    transformOrigin: `${toX - mover.left}px ${toY - mover.top}px`
  };
}

/**
 * Moves `block` from `from` (measured before the layout changed) to where it is now. The transform
 * goes on `mover`, an ancestor nothing clips: `block` sits in a scroller that would cut it off
 * mid-way. `from` is read with any glide still running applied, so a second switch mid-flight
 * continues from where the player is on screen rather than jumping.
 */
export function glide(mover: HTMLElement, block: HTMLElement, from: DOMRect): void {
  for (const running of mover.getAnimations()) running.cancel();
  if (prefersReducedMotion()) return;
  const frame = glideFrame(from, block.getBoundingClientRect(), mover.getBoundingClientRect());
  if (!frame) return;
  mover.animate([frame, { transform: 'none', transformOrigin: frame.transformOrigin }], {
    duration: MODE_MOTION_MS,
    easing: EASE_OUT
  });
}

/** Brings the column's new view in: from the side when the column just opened, else up in place. */
export function arrive(column: HTMLElement, opened: boolean): void {
  for (const running of column.getAnimations()) running.cancel();
  const still = prefersReducedMotion();
  const offset = still ? 'none' : opened ? 'translateX(24px)' : 'translateY(6px)';
  column.animate(
    [
      { opacity: 0, transform: offset },
      { opacity: 1, transform: 'none' }
    ],
    {
      duration: opened ? MODE_MOTION_MS : SWAP_MS,
      easing: EASE_OUT
    }
  );
}
