import { redirect } from '@sveltejs/kit';
import { APP_HOME } from '$lib/app-home';
import type { PageLoad } from './$types';

// Invited accounts used to live on a dedicated /shared page; they now use the ordinary Listen
// routes, which return their own rows plus whatever was shared with them. Keep the old address
// working for any invite emails or bookmarks minted while it existed.
export const load: PageLoad = () => {
  throw redirect(308, APP_HOME);
};
