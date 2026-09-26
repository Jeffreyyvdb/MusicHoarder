import { redirect } from '@sveltejs/kit';
import { pendingShareFrom, savePendingShare, toSearchParams } from '$lib/chat/pending-share';
import type { PageLoad } from './$types';

// The manifest's `share_target`: where Android's share sheet (Spotify's "Share", YouTube's…) sends
// the installed app. Public, and browser-only, so the share is stored before the (app) gate can send
// a signed-out visitor to /login — the shell picks it up again after they sign in.
export const ssr = false;
export const prerender = false;

export const load: PageLoad = ({ url }) => {
  const share = pendingShareFrom(url.searchParams);
  if (!share) redirect(303, '/chats');
  savePendingShare(share);
  redirect(303, `/chats/share?${toSearchParams(share)}`);
};
