import { error, redirect } from '@sveltejs/kit';
import { probeSession, SESSION_COOKIE } from '$lib/server/session';
import { APP_HOME } from '$lib/app-home';
import { allowedPathPrefixesFor, isPathAllowed } from '$lib/nav';
import type { LayoutServerLoad } from './$types';

/**
 * Auth gate for every (app) route. Despite `(app)/+layout.ts` setting `ssr = false`, server load
 * functions still run on Node — that's where we want auth.
 *
 * Only a 401 from the API means "signed out". A timeout, a 5xx, or an API that is mid-restart
 * means "we couldn't check" — that surfaces an error page with a retry instead of a redirect to
 * /login, because bouncing to the sign-in form makes people re-authenticate a session that was
 * never actually invalid.
 */
export const load: LayoutServerLoad = async ({ request, cookies, url }) => {
  const probe = await probeSession(request.headers.get('cookie'), {
    userAgent: request.headers.get('user-agent'),
    timeoutMs: 8000
  });

  if (probe.status === 'authenticated') {
    // What a non-admin may open is DERIVED from the nav groups they can see, so the sidebar and
    // this guard cannot disagree. Deep-linking outside it bounces home rather than rendering
    // pages full of empty or 403ing panels. Cosmetic only — the API enforces the real rules.
    if (!isPathAllowed(url.pathname, allowedPathPrefixesFor(probe.user))) {
      throw redirect(303, APP_HOME);
    }
    return { user: probe.user };
  }

  if (probe.status === 'anonymous') {
    // Drop any stale cookie so the browser stops sending an invalid session. Deliberately only
    // the active-session cookie: the parked-accounts cookie (mh_session_alts) stays, so accounts
    // remembered by the switcher survive an active-session expiry — the next login parks/dedupes
    // against them server-side.
    cookies.delete(SESSION_COOKIE, { path: '/' });
    throw redirect(303, '/login');
  }

  throw error(503, "Couldn't reach MusicHoarder to check your session — you're still signed in.");
};
