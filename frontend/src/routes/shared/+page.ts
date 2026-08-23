import { redirect } from '@sveltejs/kit';
import { APP_HOME } from '$lib/app-home';
import type { PageLoad } from './$types';

// Friends used to live on a dedicated /shared page; they now share the owner's Listen routes
// (the client's library mode feeds them from /api/shared). Keep the old address working for
// any invite emails or bookmarks minted while it existed.
export const load: PageLoad = () => {
  throw redirect(308, APP_HOME);
};
