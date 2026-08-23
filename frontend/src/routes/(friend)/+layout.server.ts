import { error, redirect } from '@sveltejs/kit';
import { probeSession, SESSION_COOKIE } from '$lib/server/session';
import { APP_HOME } from '$lib/app-home';
import type { LayoutServerLoad } from './$types';

/**
 * Auth gate for the friend surface — the mirror image of the (app) group's guard: a Friend
 * session belongs here, everyone else authenticated belongs in the owner chrome. Same
 * three-state probe semantics: only a literal 401 means signed out; an unreachable API is an
 * error page with the session left intact, never a bounce to /login.
 */
export const load: LayoutServerLoad = async ({ request, cookies }) => {
  const probe = await probeSession(request.headers.get('cookie'), {
    userAgent: request.headers.get('user-agent'),
    timeoutMs: 8000
  });

  if (probe.status === 'authenticated') {
    if (probe.user.role !== 'Friend') throw redirect(303, APP_HOME);
    return { user: probe.user };
  }

  if (probe.status === 'anonymous') {
    cookies.delete(SESSION_COOKIE, { path: '/' });
    throw redirect(303, '/login');
  }

  throw error(503, "Couldn't reach MusicHoarder to check your session — you're still signed in.");
};
