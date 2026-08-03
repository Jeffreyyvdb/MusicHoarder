import { getApiBaseUrl } from '$lib/server/api-target';
import type { SessionRole, SessionUser } from '$lib/auth/session-types';

export type { SessionRole, SessionUser };

export const SESSION_COOKIE = 'mh_session';

/** Matches the session cookie in a raw `Cookie` header without matching `x_mh_session=` etc. */
const SESSION_COOKIE_PATTERN = new RegExp(`(?:^|;\\s*)${SESSION_COOKIE}=`);

/**
 * The three genuinely different answers to "is this request signed in?".
 *
 * `unavailable` is the one that matters: it means we could not reach the API to ask, which is
 * *not* the same as being signed out. Collapsing it into `anonymous` is what made a slow or
 * restarting API look like a logged-out user and bounce people to /login with a perfectly valid
 * session cookie still in their browser.
 */
export type SessionProbe =
  | { status: 'anonymous' }
  | { status: 'authenticated'; user: SessionUser }
  | { status: 'unavailable' };

export interface ProbeSessionOptions {
  userAgent?: string | null;
  /** Time-box the API call. Keep it short on public pages, longer on the app's auth gate. */
  timeoutMs?: number;
}

/**
 * Asks the API who the caller is, by forwarding their cookie header.
 *
 * Returns `anonymous` without any network call when no session cookie is present, so anonymous
 * traffic on the marketing page never pays for an API round-trip.
 */
export async function probeSession(
  cookieHeader: string | null | undefined,
  { userAgent = null, timeoutMs = 8000 }: ProbeSessionOptions = {}
): Promise<SessionProbe> {
  if (!cookieHeader || !SESSION_COOKIE_PATTERN.test(cookieHeader)) {
    return { status: 'anonymous' };
  }

  const apiBase = getApiBaseUrl().replace(/\/$/, '');

  let response: Response;
  try {
    response = await fetch(`${apiBase}/api/auth/me`, {
      headers: { cookie: cookieHeader, 'user-agent': userAgent ?? '' },
      signal: AbortSignal.timeout(timeoutMs)
    });
  } catch {
    // Timeout or network error — the session may well still be valid.
    return { status: 'unavailable' };
  }

  // Only a 401 is the API telling us the session is genuinely no good.
  if (response.status === 401) return { status: 'anonymous' };
  if (!response.ok) return { status: 'unavailable' };

  try {
    return { status: 'authenticated', user: (await response.json()) as SessionUser };
  } catch {
    return { status: 'unavailable' };
  }
}
