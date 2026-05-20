import { redirect } from '@sveltejs/kit';
import { env as publicEnv } from '$env/dynamic/public';
import { getApiBaseUrl } from '$lib/server/api-target';
import type { LayoutServerLoad } from './$types';

const DEMO_MODE_TRUE_VALUES = new Set(['1', 'true', 'yes', 'on']);

/**
 * Auth gate for every (app) route. Despite `(app)/+layout.ts` setting `ssr = false`, server load
 * functions still run on Node — that's where we want auth. If `PUBLIC_DEMO_MODE` is on, bypass
 * the gate entirely (client mock-data takes over).
 */
export const load: LayoutServerLoad = async ({ request, cookies }) => {
  if (DEMO_MODE_TRUE_VALUES.has((publicEnv.PUBLIC_DEMO_MODE ?? '').trim().toLowerCase())) {
    return { user: { id: 'demo', email: 'demo@musichoarder.local', role: 'Demo' as const, displayName: 'Demo' } };
  }

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
