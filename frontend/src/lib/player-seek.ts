/**
 * Shared keyboard seek behaviour for every audio scrubber surface (MiniPlayer
 * seek line, TrackPanel scrubber). One mapping so seeking feels identical
 * wherever audio can be scrubbed:
 *
 *   ArrowLeft / ArrowRight  → ±5s
 *   PageDown / PageUp       → ±30s
 *   Home / End              → start / end
 *
 * Returns the clamped target time in seconds, or `null` when the key is not a
 * seek key (so callers can let it propagate).
 */
export function seekTargetForKey(
  key: string,
  currentTime: number,
  duration: number
): number | null {
  let next: number | null = null;
  switch (key) {
    case 'ArrowLeft':
      next = currentTime - 5;
      break;
    case 'ArrowRight':
      next = currentTime + 5;
      break;
    case 'PageDown':
      next = currentTime - 30;
      break;
    case 'PageUp':
      next = currentTime + 30;
      break;
    case 'Home':
      next = 0;
      break;
    case 'End':
      next = duration;
      break;
  }
  if (next === null) return null;
  return Math.max(0, Math.min(duration, next));
}

/**
 * How far into a track Previous still means "the track before this one". Past it, Previous
 * restarts the current track — the rule iOS, Android's Media3 and every hardware remote share, so
 * the web player and the Android client agree about what the button does.
 */
export const PREVIOUS_RESTART_AFTER_S = 3;

/**
 * What Previous does right now: `restart` the current track, or step back to the `previous` queue
 * item. Past {@link PREVIOUS_RESTART_AFTER_S} it always restarts; before that it steps back, except
 * on the first item, which has nothing behind it and restarts too. So Previous is never a dead
 * button while a track is loaded — `none` only when nothing is.
 */
export function previousAction(
  position: number,
  queueIndex: number
): 'restart' | 'previous' | 'none' {
  if (queueIndex < 0) return 'none';
  if (position > PREVIOUS_RESTART_AFTER_S) return 'restart';
  return queueIndex > 0 ? 'previous' : 'restart';
}
