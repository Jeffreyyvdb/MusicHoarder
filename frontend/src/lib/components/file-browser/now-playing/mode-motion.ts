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

// The move each element is making, so the next one can stop it. `getAnimations()` would answer
// too, but it recalculates the styles of everything just built under the element first.
const running = new WeakMap<HTMLElement, Animation>();

function play(el: HTMLElement, keyframes: Keyframe[], options: KeyframeAnimationOptions): void {
  running.set(el, el.animate(keyframes, options));
}

function stop(el: HTMLElement): void {
  running.get(el)?.cancel();
  running.delete(el);
}

/**
 * The transform that puts `to` back where `from` was, with its origin on `to`'s centre in the
 * `mover`'s own coordinates. It keeps the proportions (scaled by width) unless `stretch`, for a box
 * whose shape changed: a video monitor that learnt its clip's size, a picture going full screen.
 * `null` when nothing visibly moved, or when there was nothing to move from.
 */
export function glideFrame(
  from: Box,
  to: Box,
  mover: Box,
  stretch = false
): { transform: string; transformOrigin: string } | null {
  if (from.width * from.height === 0 || to.width * to.height === 0) return null;
  const toX = to.left + to.width / 2;
  const toY = to.top + to.height / 2;
  const dx = from.left + from.width / 2 - toX;
  const dy = from.top + from.height / 2 - toY;
  const sx = from.width / to.width;
  const sy = stretch ? from.height / to.height : sx;
  if (Math.abs(dx) < 1 && Math.abs(dy) < 1 && Math.abs(sx - 1) < 0.01 && Math.abs(sy - 1) < 0.01)
    return null;
  return {
    transform: `translate(${dx}px, ${dy}px) ${stretch ? `scale(${sx}, ${sy})` : `scale(${sx})`}`,
    transformOrigin: `${toX - mover.left}px ${toY - mover.top}px`
  };
}

/**
 * Moves `block` from `from` (measured before the layout changed) to where it is now. The transform
 * goes on `mover`, an ancestor nothing clips: `block` sits in a scroller that would cut it off
 * mid-way. `from` is read with any glide still running applied, so a second switch mid-flight
 * continues from where the player is on screen rather than jumping.
 */
export function glide(
  mover: HTMLElement,
  block: HTMLElement,
  from: DOMRect,
  stretch = false
): void {
  stop(mover);
  if (prefersReducedMotion()) return;
  const frame = glideFrame(
    from,
    block.getBoundingClientRect(),
    mover.getBoundingClientRect(),
    stretch
  );
  if (!frame) return;
  play(mover, [frame, { transform: 'none', transformOrigin: frame.transformOrigin }], {
    duration: MODE_MOTION_MS,
    easing: EASE_OUT
  });
}

/** Brings the column's new view in: from the side when the column just opened, else up in place. */
export function arrive(column: HTMLElement, opened: boolean): void {
  stop(column);
  const still = prefersReducedMotion();
  const offset = still ? 'none' : opened ? 'translateX(24px)' : 'translateY(6px)';
  play(
    column,
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
