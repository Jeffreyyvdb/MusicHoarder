import { redirect } from '@sveltejs/kit';

// The Overview dashboard was migrated to /runs. Keep old links working.
export const ssr = false;

export function load() {
  redirect(307, '/runs');
}
