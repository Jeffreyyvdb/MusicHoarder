/**
 * How a muted music video follows the audio. The audio is the master clock: the video is moved to
 * `mapped` (the audio's time plus the song's sync offset) whenever it drifts from it by more than
 * DRIFT_TOLERANCE_S. The three players of a song's video (Now Playing's backdrop, its Video mode,
 * the share page's backdrop) all follow through this one rule.
 */
export const DRIFT_TOLERANCE_S = 0.3;

/** How far the audio's time may move from the wall clock between two ticks before it jumped. */
const AUDIO_JUMP_S = 1;

/** How long a backdrop's video may stay out of step with the audio before it is given up. */
export const BACKDROP_GIVE_UP_MS = 5000;

export type FollowStep = { kind: 'hold' } | { kind: 'seek'; to: number } | { kind: 'give-up' };

export interface FollowInput {
  /** The video element's `currentTime`. */
  videoTime: number;
  /** The video element's `seeking`. */
  seeking: boolean;
  /** Where the video should be: the audio's time plus the sync offset, in seconds. */
  mapped: number;
  /** `performance.now()`. */
  now: number;
}

export interface VideoFollower {
  step(input: FollowInput): FollowStep;
  /** A new song or file. */
  reset(): void;
}

/**
 * Two things keep a slow link from turning the rule into a storm of range requests (#739). A seek
 * still on its way is left to land: while it is pending the element reports its target as its
 * time, so the audio moving on past the tolerance would otherwise cancel it and send another, every
 * tick, and the video would never show a frame. And with `giveUpAfterMs`, a video that has stayed
 * out of step that long while the audio played straight through is given up (the backdrops fall
 * back to the cover): it cannot keep up, and the link it is using is the one the audio needs. A
 * jump of the audio itself (a scrub, a new track, a resume after a stall) gives it a fresh window.
 */
export function createVideoFollower({
  giveUpAfterMs
}: { giveUpAfterMs?: number } = {}): VideoFollower {
  let last: { mapped: number; now: number } | null = null;
  let outOfStepSince: number | null = null;

  return {
    step({ videoTime, seeking, mapped, now }) {
      const audioJumped =
        last !== null && Math.abs(mapped - last.mapped - (now - last.now) / 1000) > AUDIO_JUMP_S;
      last = { mapped, now };
      if (audioJumped) outOfStepSince = null;

      if (!seeking && Math.abs(videoTime - mapped) <= DRIFT_TOLERANCE_S) {
        outOfStepSince = null;
        return { kind: 'hold' };
      }
      outOfStepSince ??= now;
      if (giveUpAfterMs !== undefined && now - outOfStepSince >= giveUpAfterMs) {
        return { kind: 'give-up' };
      }
      return seeking ? { kind: 'hold' } : { kind: 'seek', to: mapped };
    },
    reset() {
      last = null;
      outOfStepSince = null;
    }
  };
}
