/**
 * How a muted music video follows the audio. The audio is the master clock: the video is moved to
 * `mapped` (the audio's time plus the song's sync offset) whenever it drifts from it by more than
 * DRIFT_TOLERANCE_S. The three players of a song's video (Now Playing's backdrop, its Video mode,
 * the share page's backdrop) all follow through this one rule.
 */
export const DRIFT_TOLERANCE_S = 0.3;

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

export function createVideoFollower(_options: { giveUpAfterMs?: number } = {}): VideoFollower {
  return {
    step({ videoTime, mapped }) {
      return Math.abs(videoTime - mapped) > DRIFT_TOLERANCE_S
        ? { kind: 'seek', to: mapped }
        : { kind: 'hold' };
    },
    reset() {}
  };
}
