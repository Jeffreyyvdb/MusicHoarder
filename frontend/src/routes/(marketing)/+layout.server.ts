import { probeSession, type SessionRole } from '$lib/server/session';
import type { LayoutServerLoad } from './$types';

/**
 * Same session probe as the landing page, for the same reason: `LandingNav`'s primary call-to-action
 * calls `signInAsDemo()` when it believes the visitor is signed out, which would overwrite a real
 * owner session with a read-only demo one. Anonymous visitors short-circuit inside `probeSession`
 * with no API call, so these public pages keep their cold-start cost.
 */
export const load: LayoutServerLoad = async ({ request }) => {
  const probe = await probeSession(request.headers.get('cookie'), {
    userAgent: request.headers.get('user-agent'),
    timeoutMs: 3000
  });

  const sessionRole: SessionRole | null = probe.status === 'authenticated' ? probe.user.role : null;

  return { sessionRole };
};
