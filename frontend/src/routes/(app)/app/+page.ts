import { redirect } from '@sveltejs/kit';
import type { PageLoad } from './$types';

/**
 * Legacy redirect: pre-design /app?view=source|destination URLs now live at
 * /app/files. Strip the param and route through.
 */
export const load: PageLoad = ({ url }) => {
  const view = url.searchParams.get('view');
  if (view === 'source' || view === 'destination') {
    const target = new URL(url);
    target.pathname = '/app/files';
    return redirect(307, target.pathname + target.search);
  }
  return {};
};
