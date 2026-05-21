import { env } from '$env/dynamic/private';
import { isAllowedReturnOrigin, verifyRelayState } from '$lib/server/spotify-relay-state';
import type { RequestHandler } from './$types';

/**
 * Spotify OAuth relay. This is the single redirect URI registered in the Spotify dashboard, so it must live on a
 * stable public origin (production frontend). Every environment — local dev, each PR preview, prod — sends Spotify
 * here. The signed `state` carries the originating environment's own origin; we verify it, check it against the
 * allowlist, then 303 the browser to that origin's `/api/spotify/callback`, where the token exchange completes with
 * the owner's session cookie present (the browser arrives there top-level, so SameSite=Lax rides along).
 *
 * Pure browser bounce: no API call, no Spotify credentials, no DB. A forged/tampered state (or one whose origin
 * isn't allowlisted) is rejected with 400 rather than bounced, so the OAuth `code` can never be redirected to an
 * attacker-chosen origin.
 */
export const GET: RequestHandler = async ({ url }) => {
  const signingKey = env.SPOTIFY_OAUTH_STATE_SIGNING_KEY ?? '';
  const allowlist = env.SPOTIFY_RETURN_ORIGIN_ALLOWLIST ?? '';

  if (!signingKey) {
    return new Response('Spotify OAuth relay is not configured.', { status: 500 });
  }

  const returnOrigin = verifyRelayState(url.searchParams.get('state'), signingKey);
  if (!returnOrigin || !isAllowedReturnOrigin(returnOrigin, allowlist)) {
    return new Response('Invalid OAuth state.', { status: 400 });
  }

  // Forward the full original query (code + state, or error) to the originating env's own callback.
  const location = `${returnOrigin}/api/spotify/callback${url.search}`;
  return new Response(null, { status: 303, headers: { Location: location } });
};
