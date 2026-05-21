import { getApiBaseUrl } from '$lib/server/api-target';
import type { RequestHandler } from './$types';

/**
 * Spotify OAuth redirect-back handler. Spotify redirects the browser here (the registered
 * redirect URI is <PUBLIC_BASE_URL>/api/spotify/callback). We forward the query to the API's
 * callback server-side, then mirror its redirect back to the browser as a 303.
 *
 * Why this isn't done via the /api/mh proxy: that proxy uses `redirect: 'follow'`, which would
 * swallow the API's 302 back into the app and leave the browser on the wrong URL. This mirrors
 * the magic-link /auth/callback route for the same reason.
 */
export const GET: RequestHandler = async ({ url, fetch }) => {
  const apiBase = getApiBaseUrl().replace(/\/$/, '');
  const response = await fetch(`${apiBase}/api/spotify/callback${url.search}`, {
    redirect: 'manual'
  });

  const location = response.headers.get('location');
  return new Response(null, {
    status: 303,
    headers: { Location: location ?? '/spotify' }
  });
};
