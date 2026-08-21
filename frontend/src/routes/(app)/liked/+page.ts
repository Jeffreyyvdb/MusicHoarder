import { redirect } from '@sveltejs/kit';

/**
 * "Liked songs" is no longer a route — it is the `mh-liked` chip on /tracks. This stub keeps every
 * link that predates that change working: bookmarks, the Overview's Favourite-tracks card, and any
 * share of the old URL. 308 rather than 302 so clients cache it and stop asking.
 */
export const load = () => {
  redirect(308, '/tracks?f=mh-liked');
};
