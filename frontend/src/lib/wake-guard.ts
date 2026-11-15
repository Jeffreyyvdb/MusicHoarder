/**
 * Tells a "play" the computer sends by itself on waking from a deliberate one.
 *
 * Paused music on a Mac would start again when the lid was opened: around a wake macOS sends the
 * Now Playing app a play command of its own (headphones reconnecting, the system ending the
 * interruption it began on sleep), and the page's Media Session handler cannot tell that from a
 * press of the play key. So a play that arrives just after a wake is not obeyed.
 *
 * A wake is seen without a timer. On macOS `performance.now()` stops while the machine sleeps
 * (every engine reads a clock that excludes sleep there) and `Date.now()` does not, so the gap
 * between the two grows by the length of the sleep. The guard reads that gap whenever something
 * suggests the machine is back (the page shown or focused, the network back, a pointer or key) and
 * at the play itself, and treats a jump as a wake for the next few seconds. Windows browsers keep
 * both clocks running, so there the guard never fires and the page behaves as it always did.
 *
 * Only for computers: a phone sleeps between every press on its lock screen, and each of those is
 * meant — see `appliesTo`.
 */

/** A gap that grew by more than this since it was last read means the machine slept in between. */
export const SLEEP_GAP_MS = 10_000;
/** How long after a wake is seen a play from the OS is put down to the wake. */
export const WAKE_GUARD_MS = 5_000;

export interface WakeClock {
  /** Wall-clock milliseconds; keeps running through sleep. */
  wall(): number;
  /** Monotonic milliseconds; stops while the machine sleeps (macOS). */
  mono(): number;
}

export interface WakeGuard {
  /** Read the clocks now: call wherever the machine may just have come back. */
  check(): void;
  /** Whether the machine woke within the last {@link WAKE_GUARD_MS} (reads the clocks first). */
  justWoke(): boolean;
}

export function createWakeGuard(clock: WakeClock): WakeGuard {
  let gap = clock.wall() - clock.mono();
  let guardUntil = Number.NEGATIVE_INFINITY;

  const check = () => {
    const mono = clock.mono();
    const next = clock.wall() - mono;
    if (next - gap > SLEEP_GAP_MS) guardUntil = mono + WAKE_GUARD_MS;
    // Taken as it is either way: the wall clock set back, or NTP nudging it, is not a wake.
    gap = next;
  };

  return {
    check,
    justWoke() {
      check();
      return clock.mono() < guardUntil;
    }
  };
}

/**
 * The guard is for a computer's browser. On a phone or tablet the page sleeps whenever the screen
 * does, so every lock-screen press after a while would look like a wake; and the app shell's
 * native player takes its commands outside the page's Media Session.
 */
export function appliesTo(kind: 'computer' | 'phone' | 'tablet' | 'unknown'): boolean {
  return kind === 'computer';
}

/** The browser's clocks. */
export const browserClock: WakeClock = {
  wall: () => Date.now(),
  mono: () => performance.now()
};

/** The moments the machine may have just come back; each reads the clocks. */
const WAKE_HINTS = ['focus', 'online', 'pageshow', 'pointerdown', 'keydown'] as const;

/** Read the clocks at every hint of a wake, so a play arriving later is still measured from it. */
export function watchForWake(guard: WakeGuard): () => void {
  const onHint = () => guard.check();
  const options = { capture: true, passive: true };
  for (const type of WAKE_HINTS) window.addEventListener(type, onHint, options);
  document.addEventListener('visibilitychange', onHint);
  return () => {
    for (const type of WAKE_HINTS) window.removeEventListener(type, onHint, options);
    document.removeEventListener('visibilitychange', onHint);
  };
}
