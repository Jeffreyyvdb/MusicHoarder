import type { PageLoad } from './$types';

export const prerender = false;

export interface InvitePeek {
  inviterName: string;
  email: string;
}

// Universal load, same posture as /share/[token]: peek the invite (the GET never consumes it,
// so an email scanner's prefetch can't burn the single-use token) and render a friendly
// "link gone" state instead of the framework error page when it doesn't resolve.
export const load: PageLoad = async ({ params, fetch }) => {
  try {
    const response = await fetch(`/api/mh/api/invite/${encodeURIComponent(params.token)}`, {
      cache: 'no-store'
    });
    if (!response.ok) return { token: params.token, invite: null };
    const invite = (await response.json()) as InvitePeek;
    return { token: params.token, invite };
  } catch {
    return { token: params.token, invite: null };
  }
};
