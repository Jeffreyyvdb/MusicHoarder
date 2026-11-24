import type { HandleClientError } from '@sveltejs/kit/hooks';
import { isChunkLoadError } from '#lib/chunk-load-error.ts';

export const handleError: HandleClientError = ({ kind, error }) => {
  // SvelteKit gets here with a route's code missing only after its version check found no newer
  // build to load instead: offline, a hung request, or the seconds a redeploy takes to come up.
  if (isChunkLoadError(error)) {
    return { message: 'Couldn’t load this page. Check your connection and try again.' };
  }

  // Since SvelteKit 3 this hook also sees expected errors: `error(404, …)` from a load ('app')
  // and SvelteKit's own, such as an unknown route ('framework'). Those already carry the status
  // and message meant for the error page, so keep them; only an unexpected throw is masked.
  if (kind !== 'unknown') return error;

  return { message: 'Something went wrong.' };
};
