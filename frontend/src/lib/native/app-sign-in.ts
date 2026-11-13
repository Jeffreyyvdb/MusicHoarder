/**
 * Sign-in by email inside the native app shell.
 *
 * The emailed link opens in the browser, on a page that hands its token back to the app through
 * `musichoarder://auth`. Any page or app can open a link with that scheme, though, and carry a
 * token of its own, which would sign the app in to somebody else's account. So the app only takes
 * a token while this web view is waiting for one: the login page notes that a link was asked for
 * here, and `/auth/app` (where the shell lands with the token) spends that note.
 */

const KEY = 'mh:app-sign-in';
/** A little longer than a sign-in link lives (`Auth:MagicLinkTtlMinutes`, 15 by default). */
const WAIT_MS = 20 * 60 * 1000;

/** A sign-in link was just asked for from this web view. */
export function noteAppSignInRequested(now = Date.now()): void {
  try {
    localStorage.setItem(KEY, String(now));
  } catch {
    // No storage: the hand-off is refused later, and the link's own page offers the browser.
  }
}

/** Whether this web view is waiting for a sign-in link; asking spends the wait. */
export function takeAppSignInRequest(now = Date.now()): boolean {
  try {
    const asked = Number(localStorage.getItem(KEY));
    localStorage.removeItem(KEY);
    return Number.isFinite(asked) && asked > 0 && now >= asked && now - asked <= WAIT_MS;
  } catch {
    return false;
  }
}
