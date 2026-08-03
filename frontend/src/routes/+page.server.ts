import { probeSession, type SessionRole } from '$lib/server/session';
import type { PageServerLoad } from './$types';

/**
 * The marketing page needs to know whether the visitor already has a session, so its call-to-action
 * buttons can send them into their library instead of calling `signInAsDemo()` — which would
 * overwrite a real owner session with a read-only demo one.
 *
 * Anonymous visitors (no session cookie) short-circuit inside `probeSession` with no API call, so
 * the public landing page keeps its cold-start cost. An unreachable API degrades to "signed out",
 * which is the correct default for a marketing page.
 */
export const load: PageServerLoad = async ({ request }) => {
  const probe = await probeSession(request.headers.get('cookie'), {
    userAgent: request.headers.get('user-agent'),
    timeoutMs: 3000
  });

  const sessionRole: SessionRole | null =
    probe.status === 'authenticated' ? probe.user.role : null;

  return { sessionRole };
};
