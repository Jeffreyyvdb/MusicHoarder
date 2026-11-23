import { describe, expect, it } from 'vitest';
import { createVideoFollower, type FollowInput } from './video-follow.ts';

/** Ticks at the player store's 10 Hz, with the audio flowing at real time from `audioStart`. */
function ticks(
  from: number,
  to: number,
  audioStart: number,
  video: (now: number) => Omit<FollowInput, 'mapped' | 'now'>
) {
  const out: FollowInput[] = [];
  for (let now = from; now <= to; now += 100) {
    out.push({ ...video(now), mapped: audioStart + (now - from) / 1000, now });
  }
  return out;
}

describe('createVideoFollower', () => {
  it('holds a video in step with the audio', () => {
    const follower = createVideoFollower();
    expect(follower.step({ videoTime: 10.1, seeking: false, mapped: 10, now: 0 })).toEqual({
      kind: 'hold'
    });
  });

  it('moves a video that drifted to where the audio is', () => {
    const follower = createVideoFollower();
    expect(follower.step({ videoTime: 9, seeking: false, mapped: 10, now: 0 })).toEqual({
      kind: 'seek',
      to: 10
    });
  });

  it('sends no second seek while the first is still on its way', () => {
    // A seek on a slow link takes longer than the drift tolerance to land. While it is pending,
    // the element reports the target as its time and the audio moves on, so re-seeking every tick
    // cancels the request and starts another: issue #739's storm of range requests.
    const follower = createVideoFollower();
    expect(follower.step({ videoTime: 0, seeking: false, mapped: 10, now: 0 }).kind).toBe('seek');
    const pending = ticks(100, 2000, 10.1, () => ({ videoTime: 10, seeking: true }));
    const seeks = pending
      .map((input) => follower.step(input))
      .filter((step) => step.kind === 'seek');
    expect(seeks).toEqual([]);
  });

  it('gives up on a video that cannot keep up with the audio', () => {
    // The link is slower than the video: every seek lands behind the audio, then falls further.
    const follower = createVideoFollower({ giveUpAfterMs: 5000 });
    let target = 0;
    let landsAt = 0;
    const steps = ticks(0, 8000, 10, (now) => ({
      videoTime: target,
      seeking: now < landsAt
    })).map((input) => {
      const step = follower.step(input);
      if (step.kind === 'seek') {
        target = step.to;
        landsAt = input.now + 900;
      }
      return { now: input.now, step };
    });
    const gaveUp = steps.find(({ step }) => step.kind === 'give-up');
    expect(gaveUp?.now).toBeGreaterThanOrEqual(5000);
    expect(gaveUp?.now).toBeLessThan(6000);
  });

  it('never gives up when it is not asked to', () => {
    const follower = createVideoFollower();
    const steps = ticks(0, 20000, 10, () => ({ videoTime: 0, seeking: false })).map((input) =>
      follower.step(input)
    );
    expect(steps.some((step) => step.kind === 'give-up')).toBe(false);
  });

  it('gives a video a fresh window after the audio itself jumps', () => {
    // A scrub, a new position from the lock screen, or the audio resuming after a stall: the
    // video was not too slow, the audio moved.
    const follower = createVideoFollower({ giveUpAfterMs: 5000 });
    for (const input of ticks(0, 4000, 10, () => ({ videoTime: 10, seeking: true }))) {
      follower.step(input);
    }
    const afterJump = ticks(4100, 8000, 100, () => ({ videoTime: 100, seeking: true })).map(
      (input) => follower.step(input)
    );
    expect(afterJump.some((step) => step.kind === 'give-up')).toBe(false);
  });

  it('forgets a slow stretch once the video is back in step', () => {
    const follower = createVideoFollower({ giveUpAfterMs: 5000 });
    for (const input of ticks(0, 4000, 10, () => ({ videoTime: 10, seeking: true }))) {
      follower.step(input);
    }
    follower.step({ videoTime: 14.1, seeking: false, mapped: 14.1, now: 4100 });
    const later = ticks(4200, 8000, 14.2, () => ({ videoTime: 14.1, seeking: true })).map((input) =>
      follower.step(input)
    );
    expect(later.some((step) => step.kind === 'give-up')).toBe(false);
  });

  it('starts a new song with a fresh window', () => {
    const follower = createVideoFollower({ giveUpAfterMs: 5000 });
    for (const input of ticks(0, 4000, 10, () => ({ videoTime: 10, seeking: true }))) {
      follower.step(input);
    }
    follower.reset();
    const next = ticks(4100, 8000, 0, () => ({ videoTime: 0, seeking: true })).map((input) =>
      follower.step(input)
    );
    expect(next.some((step) => step.kind === 'give-up')).toBe(false);
  });
});
