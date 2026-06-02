import { getVersion } from '$lib/api-client';
import type { LayoutLoad } from './$types';

// Marketing root layout — dynamic so cookie-based behavior remains an option later.
export const prerender = false;

// Fetch the running build's version once and expose it as layout data so every surface (sidebar,
// mobile landing, marketing nav/footer) shows the same real semver. Runs server-side for the SSR
// marketing route and client-side for the (app) group (ssr = false). Falls back to null on failure
// so the nav degrades gracefully rather than breaking the page.
export const load: LayoutLoad = async ({ fetch }) => {
  let appVersion: string | null = null;
  try {
    appVersion = await getVersion(fetch);
  } catch {
    appVersion = null;
  }
  return { appVersion };
};
