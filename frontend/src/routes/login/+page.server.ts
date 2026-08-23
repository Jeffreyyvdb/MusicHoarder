import { redirect } from '@sveltejs/kit';
import { probeSession } from '$lib/server/session';
import { APP_HOME } from '$lib/app-home';
import type { PageServerLoad } from './$types';

/**
 * Send owners who already have a valid session straight into the app. Without this, /login
 * happily re-authenticates someone who was never signed out — which is exactly what makes the
 * app feel like it forgets you on every visit.
 *
 * Two deliberate exceptions:
 *  - a Demo session still gets the form, since it's the only way to sign in as the owner from a
 *    stale demo tab (see the demo passkey fix in #204);
 *  - `?switch` opts out of the redirect entirely, so a signed-in owner can still reach the form.
 */
export const load: PageServerLoad = async ({ request, url }) => {
  if (url.searchParams.has('switch')) return {};

  const probe = await probeSession(request.headers.get('cookie'), {
    userAgent: request.headers.get('user-agent'),
    timeoutMs: 4000
  });

  // Friends share the owner's front door: both land on the Listen home (the client's library
  // mode decides whose songs it shows).
  if (probe.status === 'authenticated' && (probe.user.role === 'Owner' || probe.user.role === 'Friend')) {
    throw redirect(303, APP_HOME);
  }

  return {};
};
