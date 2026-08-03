import { error, redirect } from '@sveltejs/kit';
import { probeSession, SESSION_COOKIE } from '$lib/server/session';
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
export const load: LayoutServerLoad = async ({ request, cookies }) => {
  const probe = await probeSession(request.headers.get('cookie'), {
    userAgent: request.headers.get('user-agent'),
    timeoutMs: 8000
  });

  if (probe.status === 'authenticated') return { user: probe.user };

  if (probe.status === 'anonymous') {
    // Drop any stale cookie so the browser stops sending an invalid session.
    cookies.delete(SESSION_COOKIE, { path: '/' });
    throw redirect(303, '/login');
  }

  throw error(503, "Couldn't reach MusicHoarder to check your session — you're still signed in.");
};
