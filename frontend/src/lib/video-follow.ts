/**
 * How a muted music video follows the audio. The audio is the master clock: the video is moved to
 * `mapped` (the audio's time plus the song's sync offset) whenever it drifts from it by more than
 * DRIFT_TOLERANCE_S. The three players of a song's video (Now Playing's backdrop, its Video mode,
 * the share page's backdrop) all follow through this one rule.
 */
export const DRIFT_TOLERANCE_S = 0.3;

/**
 * How far the audio's time may move between two ticks beyond what the wall clock allows (forward by
 * at most the time elapsed, backward not at all) before it jumped.
 */
const AUDIO_JUMP_S = 1;

/** How much audio a backdrop's video may stay out of step for before it is given up. */
export const BACKDROP_GIVE_UP_S = 5;

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
 * tick, and the video would never show a frame. And with `giveUpAfterS`, a video that has stayed
 * out of step for that much audio is given up (the backdrops fall back to the cover): it cannot keep
 * up, and the link it is using is the one the audio needs. The window is measured in audio played,
 * not wall time, because a video starving that link stalls the audio (#753): a stall neither counts
 * against the video nor restarts its window. Only a jump of the audio's position (a scrub, a new
 * track) gives it a fresh one.
 */
export function createVideoFollower({
  giveUpAfterS
}: { giveUpAfterS?: number } = {}): VideoFollower {
  let last: { mapped: number; now: number } | null = null;
  let outOfStepS: number | null = null;

  return {
    step({ videoTime, seeking, mapped, now }) {
      const played = last === null ? 0 : mapped - last.mapped;
      const audioJumped =
        last !== null &&
        (played < -AUDIO_JUMP_S || played > (now - last.now) / 1000 + AUDIO_JUMP_S);
      last = { mapped, now };
      if (audioJumped) outOfStepS = null;

      if (!seeking && Math.abs(videoTime - mapped) <= DRIFT_TOLERANCE_S) {
        outOfStepS = null;
        return { kind: 'hold' };
      }
      outOfStepS = outOfStepS === null ? 0 : outOfStepS + Math.max(0, played);
      if (giveUpAfterS !== undefined && outOfStepS >= giveUpAfterS) {
        return { kind: 'give-up' };
      }
      return seeking ? { kind: 'hold' } : { kind: 'seek', to: mapped };
    },
    reset() {
      last = null;
      outOfStepS = null;
    }
  };
}
