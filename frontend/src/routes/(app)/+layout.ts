import { redirect } from '@sveltejs/kit';
import { APP_HOME } from '$lib/app-home';
import { allowedPathPrefixesFor, isPathAllowed } from '$lib/nav';
import type { LayoutLoad } from './$types';

// The (app) shell uses module-scoped $state that reads browser-only APIs
// (HTMLAudioElement, demo-mode boolean evaluated at module load).
// Disable SSR so those reads don't crash on the server.
export const ssr = false;
export const prerender = false;

/**
 * The route guard for client-side navigations. The server load checks the session and the path
 * once, when the document loads; after that it no longer re-runs per navigation (see
 * `+layout.server.ts`), so a member following a stale link to an admin page is bounced home here
 * instead — with the user the server already handed over, and no network. Cosmetic, like the
 * server's: the API enforces the real rules.
 */
export const load: LayoutLoad = ({ data, url }) => {
  if (!isPathAllowed(url.pathname, allowedPathPrefixesFor(data.user))) redirect(303, APP_HOME);
  return data;
};
