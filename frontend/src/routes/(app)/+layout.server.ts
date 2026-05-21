import { redirect } from '@sveltejs/kit';
import { getApiBaseUrl } from '$lib/server/api-target';
import type { LayoutServerLoad } from './$types';

/**
 * Auth gate for every (app) route. Despite `(app)/+layout.ts` setting `ssr = false`, server load
 * functions still run on Node — that's where we want auth.
 */
export const load: LayoutServerLoad = async ({ request, cookies }) => {
  const apiBase = getApiBaseUrl().replace(/\/$/, '');
  const response = await fetch(`${apiBase}/api/auth/me`, {
    headers: {
      cookie: request.headers.get('cookie') ?? '',
      'user-agent': request.headers.get('user-agent') ?? ''
    }
  });

  if (response.status === 401) {
    // Drop any stale cookie so the browser stops sending an invalid session.
    cookies.delete('mh_session', { path: '/' });
    throw redirect(303, '/login');
  }
  if (!response.ok) {
    throw redirect(303, '/login');
  }

  const user = (await response.json()) as {
    id: string;
    email: string;
    role: 'Owner' | 'Demo';
    displayName: string | null;
  };
  return { user };
};
