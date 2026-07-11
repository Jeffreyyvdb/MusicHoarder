import type { PageLoad } from './$types';
import { fetchSharePayload } from '$lib/share-client';

export const prerender = false;

// Universal load: runs on the server for the first paint (so <head> og-tags are in the
// HTML link-preview crawlers see) and in the browser on client-side navigation. Unknown or
// revoked tokens don't throw — the page renders a friendly "link gone" state instead of the
// framework error page.
export const load: PageLoad = async ({ params, fetch }) => {
  try {
    const payload = await fetchSharePayload(fetch, params.token);
    return { token: params.token, payload };
  } catch {
    return { token: params.token, payload: null };
  }
};
