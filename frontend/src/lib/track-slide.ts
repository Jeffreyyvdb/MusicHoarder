import type { TransitionConfig } from 'svelte/transition';
import { easeOut, prefersReducedMotion } from '#lib/motion.ts';
import type { TrackStep } from '#lib/stores/player.svelte.ts';

const TAP_TRAVEL_PX = 24;
const IN_MS = 240;
const OUT_MS = 160;
const SWIPE_OUT_MS = 200;

export type Slide = {
  /** Offsets in px at the start and end; null when the copy only fades. */
  from: number | null;
  to: number | null;
  duration: number;
};

/**
 * What one copy of the mini player's song does when the song changes. Next carries content left
 * (the new song enters from the right), Previous the other way, and a swap, or Reduce Motion, is
 * a crossfade with no travel. `fromX` is where a finger left the leaving copy.
 */
export function slideSpec(
  phase: 'in' | 'out',
  step: TrackStep,
  opts: { reduced: boolean; fromX?: number; width?: number }
): Slide {
  const leaves = step === 'next' ? -1 : step === 'previous' ? 1 : 0;
  if (opts.reduced || leaves === 0) {
    return { from: null, to: null, duration: phase === 'in' ? IN_MS : OUT_MS };
  }
  if (phase === 'in') return { from: -leaves * TAP_TRAVEL_PX, to: 0, duration: IN_MS };
  const fromX = opts.fromX ?? 0;
  if (Math.abs(fromX) >= 1 && opts.width) {
    return { from: fromX, to: leaves * opts.width, duration: SWIPE_OUT_MS };
  }
  return { from: 0, to: leaves * TAP_TRAVEL_PX, duration: OUT_MS };
}

function config(slide: Slide, outgoing: boolean): TransitionConfig {
  const { from, to } = slide;
  return {
    duration: slide.duration,
    easing: easeOut,
    css: (t, u) => {
      if (from === null || to === null) return `opacity: ${t}`;
      const x = outgoing ? from + (to - from) * u : from * u;
      return `transform: translate3d(${x}px, 0, 0); opacity: ${t}`;
    }
  };
}

export function trackSlideIn(_node: Element, params: { step: TrackStep }): TransitionConfig {
  return config(slideSpec('in', params.step, { reduced: prefersReducedMotion() }), false);
}

/** Starts from wherever the copy is now: a swipe has already dragged it off centre. */
export function trackSlideOut(node: Element, params: { step: TrackStep }): TransitionConfig {
  const fromX = new DOMMatrixReadOnly(getComputedStyle(node).transform).m41;
  const width = node.parentElement?.clientWidth ?? 0;
  return config(
    slideSpec('out', params.step, { reduced: prefersReducedMotion(), fromX, width }),
    true
  );
}
