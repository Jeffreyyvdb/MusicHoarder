import { getApiBaseUrl } from '$lib/server/api-target';
import type { RequestHandler } from './$types';

/**
 * Spotify OAuth redirect-back handler. Spotify redirects the browser here (the registered
 * redirect URI is <PUBLIC_BASE_URL>/api/spotify/callback). We forward the query to the API's
 * callback server-side, then 303 the browser into the app.
 *
 * The API would normally 302 straight back to the app, but a redirect is unreadable across this
 * Node→API hop: undici turns it into an opaque response with no Location. So we ask the API for a
 * JSON outcome (X-Spotify-Callback-Mode: json) and build the browser redirect here. This is also
 * why we can't use the /api/mh proxy, whose `redirect: 'follow'` would swallow the hop.
 *
 * Spotify's redirect-back is a top-level GET navigation to our own origin, so the SameSite=Lax
 * `mh_session` cookie rides along. We forward it to the API so its RequireAuth middleware sees the
 * authenticated owner — without it the hop is anonymous and the API rejects with `unauthenticated`.
 */
export const GET: RequestHandler = async ({ url, fetch, request }) => {
  const apiBase = getApiBaseUrl().replace(/\/$/, '');

  const headers: Record<string, string> = { 'X-Spotify-Callback-Mode': 'json' };
  const cookie = request.headers.get('cookie');
  if (cookie) headers.cookie = cookie;

  let connected = false;
  let errorMessage: string | null = null;
  try {
    const response = await fetch(`${apiBase}/api/spotify/callback${url.search}`, { headers });
    const body = (await response.json()) as { connected?: boolean; error?: string | null };
    connected = body.connected === true;
    errorMessage = body.error ?? null;
  } catch {
    errorMessage = 'Could not complete Spotify connection.';
  }

  const location = connected
    ? '/spotify?spotify_connected=1'
    : `/spotify?spotify_error=${encodeURIComponent(errorMessage ?? 'Could not complete Spotify connection.')}`;

  return new Response(null, { status: 303, headers: { Location: location } });
};
