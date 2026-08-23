import { getApiBaseUrl } from '$lib/server/api-target';
import { APP_HOME } from '$lib/app-home';
import type { RequestHandler } from './$types';

/**
 * Invite-accept handler. The page POSTs here (a real form navigation); we forward the token to
 * the API, mirror the resulting Set-Cookie onto the browser, and 303 into the friend surface.
 *
 * Why this isn't done via the /api/mh proxy: same reason as /auth/callback — the proxy uses
 * `redirect: 'follow'`, which would swallow any Set-Cookie issued during an intermediate hop.
 * Doing the accept here keeps the cookie write on the same response that lands in the browser.
 */
export const POST: RequestHandler = async ({ params, fetch }) => {
  const apiBase = getApiBaseUrl().replace(/\/$/, '');
  const response = await fetch(`${apiBase}/api/invite/accept`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ token: params.token })
  });

  if (response.status !== 200) {
    // Bounce back to the invite page, which re-peeks and shows the "link gone" state.
    return new Response(null, {
      status: 303,
      headers: { Location: `/invite/${encodeURIComponent(params.token)}?error=1` }
    });
  }

  const headers = new Headers({ Location: APP_HOME });
  for (const value of response.headers.getSetCookie?.() ?? []) {
    headers.append('set-cookie', value);
  }
  return new Response(null, { status: 303, headers });
};
