import { env } from '$env/dynamic/public';

/**
 * Custom events for the self-hosted Umami tracker that `Analytics.svelte` loads. Umami records page
 * views on its own; this is for moments a page view does not capture — a shared song being opened
 * or played — named and with properties, so they read in Umami's Events view.
 *
 * Silent in every failure mode: analytics not configured, the tracker blocked or slow to load, a
 * server that refuses the event. A named event is a nicety, never something a page waits on.
 */

export type UmamiEventData = Record<string, string | number | boolean>;

type UmamiTracker = { track: (event: string, data?: UmamiEventData) => unknown };

/** The same test `Analytics.svelte` applies before it loads the tracker. */
export function umamiEnabled(): boolean {
  return Boolean(env.PUBLIC_UMAMI_WEBSITE_ID && env.PUBLIC_UMAMI_SRC);
}

// The tracker script is `defer`red, so an event fired from a page's first effect can arrive before
// `window.umami` exists. Wait for it a while, then give up (an ad blocker, most likely).
const WAIT_MS = 10_000;
const STEP_MS = 250;

export function trackUmamiEvent(name: string, data?: UmamiEventData): void {
  if (typeof window === 'undefined' || !umamiEnabled()) return;
  let waited = 0;
  const attempt = () => {
    const tracker = (window as Window & { umami?: UmamiTracker }).umami;
    if (tracker && typeof tracker.track === 'function') {
      try {
        void Promise.resolve(tracker.track(name, data)).catch(() => {});
      } catch {
        // The tracker threw synchronously; nothing to do about it.
      }
      return;
    }
    waited += STEP_MS;
    if (waited <= WAIT_MS) setTimeout(attempt, STEP_MS);
  };
  attempt();
}
