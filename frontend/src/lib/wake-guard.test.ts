import { describe, expect, it } from 'vitest';
import { appliesTo, createWakeGuard, SLEEP_GAP_MS, WAKE_GUARD_MS } from './wake-guard';

/** Two clocks that run together, until `sleep` moves only the wall clock (macOS). */
function fakeClock() {
  let wall = 1_700_000_000_000;
  let mono = 1_000;
  return {
    clock: { wall: () => wall, mono: () => mono },
    pass(ms: number) {
      wall += ms;
      mono += ms;
    },
    sleep(ms: number) {
      wall += ms;
    }
  };
}

describe('createWakeGuard', () => {
  it('sees nothing while the machine stays awake, however long', () => {
    const t = fakeClock();
    const guard = createWakeGuard(t.clock);
    t.pass(6 * 60 * 60 * 1000);
    expect(guard.justWoke()).toBe(false);
  });

  it('puts a play down to the wake when it arrives just after one', () => {
    const t = fakeClock();
    const guard = createWakeGuard(t.clock);
    t.sleep(45 * 60 * 1000);
    t.pass(800);
    expect(guard.justWoke()).toBe(true);
  });

  it('lets a press through once the guard has run out', () => {
    const t = fakeClock();
    const guard = createWakeGuard(t.clock);
    t.sleep(45 * 60 * 1000);
    expect(guard.justWoke()).toBe(true);
    t.pass(WAKE_GUARD_MS + 1);
    expect(guard.justWoke()).toBe(false);
  });

  it('measures the guard from the first sign of the wake, not from the play', () => {
    const t = fakeClock();
    const guard = createWakeGuard(t.clock);
    t.sleep(45 * 60 * 1000);
    guard.check(); // the page shown again
    t.pass(WAKE_GUARD_MS + 1);
    expect(guard.justWoke()).toBe(false);
  });

  it('takes a short suspension or a clock nudge for what it is', () => {
    const t = fakeClock();
    const guard = createWakeGuard(t.clock);
    t.sleep(SLEEP_GAP_MS - 1);
    expect(guard.justWoke()).toBe(false);
    t.sleep(-60 * 60 * 1000); // the wall clock set back an hour
    expect(guard.justWoke()).toBe(false);
  });
});

describe('appliesTo', () => {
  it('guards a computer only', () => {
    expect(appliesTo('computer')).toBe(true);
    expect(appliesTo('phone')).toBe(false);
    expect(appliesTo('tablet')).toBe(false);
    expect(appliesTo('unknown')).toBe(false);
  });
});
