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
