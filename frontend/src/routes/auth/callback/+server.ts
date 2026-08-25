import { error } from '@sveltejs/kit';
import { getApiBaseUrl } from '$lib/server/api-target';
import { APP_HOME } from '$lib/app-home';
import type { RequestHandler } from './$types';

/**
 * Magic-link click handler. The email contains a link to this route on the frontend; we POST
 * the token to the API, mirror the resulting Set-Cookie onto the browser, and 303 the user
 * into the app.
 *
 * Why this isn't done via the /api/mh proxy: the proxy uses `redirect: 'follow'` which would
 * swallow any Set-Cookie issued during an intermediate hop. Doing the consume here keeps the
 * cookie write on the same response that lands in the browser.
 */
export const GET: RequestHandler = async ({ url, fetch, request }) => {
  const token = url.searchParams.get('token');
  if (!token) throw error(400, 'Missing token.');

  const apiBase = getApiBaseUrl().replace(/\/$/, '');
  const response = await fetch(`${apiBase}/api/auth/consume`, {
    method: 'POST',
    headers: {
      'content-type': 'application/json',
      // Forward the browser's cookies so a still-signed-in session gets parked by the account
      // switcher instead of silently discarded.
      cookie: request.headers.get('cookie') ?? ''
    },
    body: JSON.stringify({ token })
  });

  if (response.status !== 200) {
    const message = response.status === 400 ? 'Invalid or expired link.' : 'Sign-in failed.';
    return new Response(null, {
      status: 303,
      headers: { Location: `/login?error=${encodeURIComponent(message)}` }
    });
  }

  // Forward the API's Set-Cookie header(s) so the cookie lands on the user's browser.
  const headers = new Headers({ Location: APP_HOME });
  for (const value of response.headers.getSetCookie?.() ?? []) {
    headers.append('set-cookie', value);
  }
  return new Response(null, { status: 303, headers });
};
