/**
 * Keeps an open app honest about its session without making a navigation wait for it.
 *
 * The (app) layout's server load is the auth gate, and it used to re-run on every navigation: it
 * read `url.pathname` for the route guard, which made SvelteKit fetch `__data.json` (and the Node
 * server ask the API `/api/auth/me`) before it would render the next page. Every tap, and every
 * Back, waited on that round trip. The gate now runs once per document load; this module is what
 * still notices, after that, that the session died (signed out elsewhere, revoked) or now belongs
 * to someone else (an account switched in another tab) — in the background, at most once per
 * {@link SESSION_RECHECK_MS}, poked by navigations the way the old gate was.
 *
 * It never decides "signed out" by itself: a 401 hands the question back to the server gate
 * (`invalidate(SESSION_DEPENDENCY)`), which clears the stale cookie and redirects to /login exactly
 * as it does on a cold load — and simply returns the user again if it disagrees. A failed check
 * (a timeout, a 5xx, an API mid-restart) means "couldn't ask", never "signed out".
 */

/** What the (app) server load `depends` on, so the watch can re-run just that load. */
export const SESSION_DEPENDENCY = 'mh:session';

/** At most one background check per minute; the gate itself ran when the document loaded. */
export const SESSION_RECHECK_MS = 60_000;

export type SessionCheckOutcome = 'same' | 'signed-out' | 'switched' | 'unknown';

/**
 * Compare a fresh `/api/auth/me` answer with the account the page was loaded for. `me` is null for
 * a 401 and undefined when the check could not be made.
 */
export function classifySession(
  loadedUserId: string | null | undefined,
  me: { id: string } | null | undefined
): SessionCheckOutcome {
  if (me === undefined) return 'unknown';
  if (me === null) return 'signed-out';
  return loadedUserId && me.id !== loadedUserId ? 'switched' : 'same';
}

export type SessionWatchOptions = {
  /** The account the page was loaded for (page.data.user.id). */
  userId: () => string | null | undefined;
  /** `/api/auth/me`: the user, null on a 401, or a throw when it could not be asked. */
  check: () => Promise<{ id: string } | null>;
  /** The session is gone — let the server gate take it from here. */
  onSignedOut: () => void;
  /** The session belongs to another account now — the page must hard-reload. */
  onSwitched: () => void;
  intervalMs?: number;
  now?: () => number;
};

export function createSessionWatch(options: SessionWatchOptions): { poke(): void } {
  const interval = options.intervalMs ?? SESSION_RECHECK_MS;
  const now = options.now ?? Date.now;
  // The gate checked the session as the page loaded, so the clock starts now.
  let last = now();
  let inFlight = false;

  async function run(): Promise<void> {
    let me: { id: string } | null | undefined;
    try {
      me = await options.check();
    } catch {
      me = undefined;
    }
    const outcome = classifySession(options.userId(), me);
    if (outcome === 'signed-out') options.onSignedOut();
    else if (outcome === 'switched') options.onSwitched();
  }

  return {
    /** A navigation happened; check the session if the last check is old enough. Never awaits. */
    poke() {
      if (inFlight || now() - last < interval) return;
      last = now();
      inFlight = true;
      void run().finally(() => {
        inFlight = false;
      });
    }
  };
}
